#requires -Version 5.1
<#
.SYNOPSIS
    Chrome DevTools Protocol の最小クライアント（ISSUE #94）。

.DESCRIPTION
    新しい依存を増やさないため、CDP は .NET 標準の ClientWebSocket を直接叩く。

    設計上の割り切り（提案時のリスク対処をそのまま実装している）:

    - リクエスト / レスポンス同期しかしない。送った id と一致するフレームを
      読むまで受信し続け、途中のイベント（Page.loadEventFired など）は捨てる。
      イベント購読を持ち込むと受信ループが状態を持ち、「たまに取りこぼす」形の
      不安定さを抱える。待ちは呼び出し側のポーリングで表現する。
    - 分割フレームは EndOfMessage まで連結してから JSON にする。
      1 フレーム = 1 メッセージという前提を置かない。

    どの関数も同期的に振る舞う（GetAwaiter().GetResult()）。ハーネスは逐次実行で、
    並列化しても二重起動の制約で得がない。
#>

Set-StrictMode -Version Latest

function Connect-Cdp {
    <#
    .SYNOPSIS
        webSocketDebuggerUrl へ接続してセッションを返す。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$WebSocketUrl,
        [int]$TimeoutSec = 15
    )

    $socket = New-Object System.Net.WebSockets.ClientWebSocket
    $cts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($TimeoutSec))
    try {
        [void]$socket.ConnectAsync([Uri]$WebSocketUrl, $cts.Token).GetAwaiter().GetResult()
    } catch {
        $socket.Dispose()
        throw "CDP へ接続できません: $WebSocketUrl / $($_.Exception.Message)"
    } finally {
        $cts.Dispose()
    }

    return [pscustomobject]@{
        Socket = $socket
        Url    = $WebSocketUrl
        NextId = 1
    }
}

function Disconnect-Cdp {
    [CmdletBinding()]
    param([AllowNull()]$Session)

    if ($null -eq $Session -or $null -eq $Session.Socket) { return }
    try {
        if ($Session.Socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $cts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds(3))
            try {
                [void]$Session.Socket.CloseAsync(
                    [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                    'bye', $cts.Token).GetAwaiter().GetResult()
            } finally {
                $cts.Dispose()
            }
        }
    } catch {
        # 相手が先に閉じた場合など。後始末なので握りつぶす。
    } finally {
        $Session.Socket.Dispose()
    }
}

function Read-CdpMessage {
    <#
    .SYNOPSIS
        1 メッセージ分（EndOfMessage まで）読んで JSON に変換する。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][System.Threading.CancellationToken]$Token
    )

    $buffer = New-Object byte[] 16384
    $segment = New-Object System.ArraySegment[byte] (, $buffer)
    $stream = New-Object System.IO.MemoryStream
    try {
        while ($true) {
            $result = $Session.Socket.ReceiveAsync($segment, $Token).GetAwaiter().GetResult()
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                throw 'CDP の接続が相手側から閉じられました。'
            }
            if ($result.Count -gt 0) {
                $stream.Write($buffer, 0, $result.Count)
            }
            if ($result.EndOfMessage) { break }
        }

        $text = [System.Text.Encoding]::UTF8.GetString($stream.ToArray())
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return ($text | ConvertFrom-Json)
    } finally {
        $stream.Dispose()
    }
}

function Invoke-CdpCommand {
    <#
    .SYNOPSIS
        CDP のコマンドを 1 本送り、同じ id の応答が返るまで待つ。
    .OUTPUTS
        応答の result（PSCustomObject）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][string]$Method,
        [hashtable]$Params,
        [int]$TimeoutSec = 15
    )

    $id = [int]$Session.NextId
    $Session.NextId = $id + 1

    $payload = @{ id = $id; method = $Method }
    if ($Params) { $payload['params'] = $Params }
    $json = $payload | ConvertTo-Json -Depth 12 -Compress

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $cts = New-Object System.Threading.CancellationTokenSource ([TimeSpan]::FromSeconds($TimeoutSec))
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $segment = New-Object System.ArraySegment[byte] (, $bytes)
        [void]$Session.Socket.SendAsync(
            $segment,
            [System.Net.WebSockets.WebSocketMessageType]::Text,
            $true,
            $cts.Token).GetAwaiter().GetResult()

        while ((Get-Date) -lt $deadline) {
            try {
                $message = Read-CdpMessage -Session $Session -Token $cts.Token
            } catch [System.OperationCanceledException] {
                # ReceiveAsync が待ちきった。下のタイムアウトと同じ扱いにする。
                break
            }
            if ($null -eq $message) { continue }

            # id を持たないフレームはイベント。ここでは使わないので読み飛ばす。
            if (-not $message.PSObject.Properties['id']) { continue }
            if ([int]$message.id -ne $id) { continue }

            if ($message.PSObject.Properties['error']) {
                throw "CDP がエラーを返しました（$Method）: $($message.error | ConvertTo-Json -Compress)"
            }
            if ($message.PSObject.Properties['result']) { return $message.result }
            return $null
        }
    } finally {
        $cts.Dispose()
    }

    throw ("CDP の応答がありません（{0} / {1}s）。" -f $Method, $TimeoutSec)
}

function Invoke-CdpEvaluate {
    <#
    .SYNOPSIS
        ページ内で式を評価し、値をそのまま受け取る（returnByValue）。
    .DESCRIPTION
        判定の根拠は常にページ内の実測値にする。DOM から取れる値だけで
        assertion を書けるようにするための唯一の入口。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][string]$Expression,
        [int]$TimeoutSec = 15
    )

    $result = Invoke-CdpCommand -Session $Session -Method 'Runtime.evaluate' -TimeoutSec $TimeoutSec -Params @{
        expression    = $Expression
        returnByValue = $true
        awaitPromise  = $true
    }

    if ($null -eq $result) { return $null }

    if ($result.PSObject.Properties['exceptionDetails']) {
        $detail = $result.exceptionDetails
        $text = if ($detail.PSObject.Properties['exception'] -and
            $detail.exception.PSObject.Properties['description']) {
            $detail.exception.description
        } else {
            $detail | ConvertTo-Json -Compress
        }
        throw "ページ内で例外が起きました: $text"
    }

    if (-not $result.PSObject.Properties['result']) { return $null }
    if (-not $result.result.PSObject.Properties['value']) { return $null }
    return $result.result.value
}
