#requires -Version 5.1
<#
.SYNOPSIS
    Hirake をデータ領域ごと隔離して起動し、CDP のターゲットまで面倒を見る（ISSUE #94）。

.DESCRIPTION
    このハーネスで一番危険なのは「実利用のデータを汚すこと」。ピン留め（canvas）は
    ワークスペースに属さずアプリ全体で共有されるため、混ざったあとの切り分けは
    手作業になる。そこで HIRAKE_DATA_ROOT で使い捨てフォルダへ丸ごと逃がし、
    終了時にフォルダごと消す。

    既知の罠への対処もここに集約する:

    - 二重起動でパスが転送される（SingleInstanceManager）。既存 Hirake が
      生きているとハーネスの起動が別プロセスへ吸われるため、起動前に検出して
      中止する。勝手に kill はしない（利用者の作業中ウィンドウを閉じる事故を避ける）
    - ポートが別プロセスに使われていると接続先を取り違える。起動前に確認する
    - 非表示タブ（Visibility=Collapsed）は setTimeout がスロットリングされ、
      Page.captureScreenshot はハングする。評価は選択中（＝可視）のタブに限る
#>

Set-StrictMode -Version Latest

function Resolve-HirakeExe {
    <#
    .SYNOPSIS
        検証に使う Hirake.exe を決める。publish を優先し、無ければビルド出力を探す。
    #>
    [CmdletBinding()]
    param([string]$RepoRoot)

    if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }

    $publish = Join-Path $RepoRoot 'publish\Hirake.exe'
    if (Test-Path -LiteralPath $publish -PathType Leaf) { return $publish }

    $binRoot = Join-Path $RepoRoot 'src\Hirake\bin'
    if (Test-Path -LiteralPath $binRoot) {
        $found = Get-ChildItem -LiteralPath $binRoot -Filter 'Hirake.exe' -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    return $null
}

function Test-HirakeRunning {
    [CmdletBinding()]
    param()
    return [bool](Get-Process -Name 'Hirake' -ErrorAction SilentlyContinue)
}

function Test-BinaryContainsMarker {
    <#
    .SYNOPSIS
        バイナリの中に文字列が焼き込まれているかを見る（定数メモリ）。
    .DESCRIPTION
        .NET の文字列リテラルはメタデータへ UTF-16LE で入る。ここでは
        「UTF-16LE のバイト列」と「そのままのバイト列」の 2 つを**バイト列として**探す。
        バイト列で探すので、2 バイト境界のずれを気にする必要がない。

        照合は Latin-1（ISO-8859-1）で 1 バイト = 1 文字へ写してから String.IndexOf に
        任せる。Latin-1 は 0..255 を無損失で往復できるので、バイト列の探索を
        そのまま文字列探索に置き換えられる。自前のバイト比較ループより桁違いに速い。

        読みは 1MB ずつで、境界をまたぐ一致を落とさないよう末尾を持ち越す。
        自己完結・単一ファイル発行の大きな exe でもメモリは一定に保つ。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Marker
    )

    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $needleUtf16 = $latin1.GetString([System.Text.Encoding]::Unicode.GetBytes($Marker))
    $needlePlain = $latin1.GetString($latin1.GetBytes($Marker))
    $overlap = [Math]::Max($needleUtf16.Length, $needlePlain.Length) - 1

    $chunk = 1MB
    $buffer = New-Object byte[] ($chunk + $overlap)
    $carry = 0

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        while ($true) {
            $read = $stream.Read($buffer, $carry, $chunk)
            if ($read -le 0) { break }

            $length = $carry + $read
            $text = $latin1.GetString($buffer, 0, $length)
            if ($text.Contains($needleUtf16) -or $text.Contains($needlePlain)) { return $true }

            $carry = [Math]::Min($overlap, $length)
            if ($carry -gt 0) {
                [Array]::Copy($buffer, $length - $carry, $buffer, 0, $carry)
            }
        }
    } finally {
        $stream.Dispose()
    }

    return $false
}

function Test-HirakeSupportsDataRoot {
    <#
    .SYNOPSIS
        その exe が HIRAKE_DATA_ROOT を知っているかを、起動する前に確かめる。
    .DESCRIPTION
        知らないバイナリ（#94 より前の publish 成果物など）を起動してしまうと、
        環境変数は黙って無視され、その時点で実利用の %LocalAppData%\Hirake に
        WebView2 プロファイルや設定が作られる。**起動してからでは遅い。**

        判定は「AppPaths が持つ環境変数名の文字列が焼き込まれているか」で行う。
        製品側にテスト専用の版番号や口を足さずに済む。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExePath,
        # 起動役（apphost）と見なす上限。実測で 150KB 前後。単一ファイル発行は
        # アプリと依存を丸ごと抱えるので桁が違う。
        [long]$ApphostMaxBytes = 4MB
    )

    $marker = 'HIRAKE_DATA_ROOT'

    # 1) exe 自身が持っていれば、それが走る中身。単一ファイル発行はここで通る。
    if (Test-BinaryContainsMarker -Path $ExePath -Marker $marker) { return $true }

    # 2) exe が持っていない。ここから先は
    #      (a) 起動役だけの apphost（中身は隣の Hirake.dll）
    #      (b) HIRAKE_DATA_ROOT を知らない古い単一ファイル発行物
    #    のどちらか。取り違えると (b) を起動してしまうので、
    #    「apphost だと言い切れないなら通さない」側へ倒す。
    #
    #    隣にファイルが在るかどうかでは (a) と (b) を区別できない
    #    （古い exe の隣に新しい publish の dll が残っている配置があり得る）。
    #    大きさは中身の有無を直接反映するので、こちらを根拠にする。
    $info = Get-Item -LiteralPath $ExePath -ErrorAction SilentlyContinue
    if (-not $info -or $info.Length -gt $ApphostMaxBytes) { return $false }

    $dll = Join-Path (Split-Path -Parent $ExePath) 'Hirake.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { return $false }

    return (Test-BinaryContainsMarker -Path $dll -Marker $marker)
}

function Test-PortInUse {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Port)

    # Listen だけを見る。TIME_WAIT の残骸まで「使用中」と数えると、
    # 直前の実行の後始末が終わる前に次の実行が理由なく止まる。
    try {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop
        if ($conn) { return $true }
    } catch [System.Management.Automation.CommandNotFoundException] {
        # 下のフォールバックへ
    } catch {
        # 「該当なし」も例外で返る実装があるため、ここでは確定させずフォールバックする
    }

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if ($async.AsyncWaitHandle.WaitOne(300)) {
            $client.EndConnect($async)
            return $true
        }
        return $false
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Test-CanvasHarnessPrecondition {
    <#
    .SYNOPSIS
        起動前に満たすべき条件を確認する。満たさないときは理由の文字列を返す。
    .OUTPUTS
        問題なければ $null。問題があればメッセージ。
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Port)

    if (Test-HirakeRunning) {
        return '既に Hirake が起動しています。終了してから再実行してください（二重起動でパスが転送されるため）。'
    }
    if (Test-PortInUse -Port $Port) {
        return "ポート $Port が使用中です。別のプロセスを終了するか -Port で変更してください。"
    }
    return $null
}

function Get-CdpTargets {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Port)

    try {
        $response = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/json/list" -TimeoutSec 5 -ErrorAction Stop
    } catch {
        return @()
    }
    if ($null -eq $response) { return @() }
    return @($response)
}

# 判別式。canvas.js が公開している契約（#cv-stage と __cvFitToContent）で見分ける。
# title / url での判別は NavigateToString 経路の有無で揺れるため使わない。
# 「そのページに何があるか」を実際に評価するので、表示経路が変わっても壊れない。
$script:CanvasProbe = "!!(document.getElementById('cv-stage') && typeof window.__cvFitToContent === 'function')"
$script:DocumentProbe = "!!(window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') && !document.getElementById('cv-stage')"

function Get-CanvasProbeExpression { return $script:CanvasProbe }
function Get-DocumentProbeExpression { return $script:DocumentProbe }

function Find-CdpTarget {
    <#
    .SYNOPSIS
        ページ種別のターゲットを 1 つずつ調べ、Probe が真になった最初のものを返す。
    .DESCRIPTION
        戻り値には接続済みセッションを含める。呼び出し側が Disconnect-Cdp すること。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$Probe
    )

    foreach ($target in (Get-CdpTargets -Port $Port)) {
        if (-not $target.PSObject.Properties['webSocketDebuggerUrl']) { continue }
        if ($target.PSObject.Properties['type'] -and $target.type -ne 'page') { continue }

        $session = $null
        try {
            $session = Connect-Cdp -WebSocketUrl $target.webSocketDebuggerUrl
            $hit = Invoke-CdpEvaluate -Session $session -Expression $Probe -TimeoutSec 5
            if ($hit -eq $true) {
                return [pscustomobject]@{ Target = $target; Session = $session }
            }
        } catch {
            # 接続直後に閉じたターゲットなど。次を見る。
        }
        if ($session) { Disconnect-Cdp -Session $session }
    }
    return $null
}

function Wait-CdpTarget {
    <#
    .SYNOPSIS
        Probe に合うターゲットが現れるまでポーリングする（イベント購読はしない）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][string]$Probe,
        [int]$TimeoutSec = 30,
        [string]$What = 'CDP ターゲット'
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $found = Find-CdpTarget -Port $Port -Probe $Probe
        if ($found) { return $found }
        Start-Sleep -Milliseconds 500
    }
    throw "$What に接続できません（$TimeoutSec s）。"
}

function Start-HirakeHost {
    <#
    .SYNOPSIS
        隔離したデータ領域で Hirake を起動する。
    .OUTPUTS
        後始末に必要な情報を持つオブジェクト。必ず finally で Stop-HirakeHost すること。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$OpenFile,
        [int]$Port = 9333,
        [int]$IsolationTimeoutSec = 40
    )

    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        throw "Hirake.exe が見つかりません: $ExePath"
    }
    if (-not (Test-Path -LiteralPath $OpenFile -PathType Leaf)) {
        throw "開くファイルが見つかりません: $OpenFile"
    }

    $dataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("hirake-harness-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

    $savedDataRoot = $env:HIRAKE_DATA_ROOT
    $savedBrowserArgs = $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
    $hostInfo = $null
    try {
        $env:HIRAKE_DATA_ROOT = $dataRoot
        $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"

        # 空白を含むパスでも 1 引数として渡す（NormalizeArgs は 1 要素ずつ解決する）。
        $process = Start-Process -FilePath $ExePath -ArgumentList ('"{0}"' -f $OpenFile) -PassThru
        $hostInfo = [pscustomobject]@{
            Process  = $process
            DataRoot = $dataRoot
            Port     = $Port
            ExePath  = $ExePath
        }
    } catch {
        # 起動に失敗したら、作った一時領域をその場で片づける。
        # 呼び出し側は $hostInfo を受け取れないので finally では消せない。
        Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
        throw
    } finally {
        # 子プロセスへ渡すためだけに触る。自プロセスには残さない。
        if ($null -eq $savedDataRoot) {
            if (Test-Path Env:\HIRAKE_DATA_ROOT) { Remove-Item Env:\HIRAKE_DATA_ROOT }
        } else {
            $env:HIRAKE_DATA_ROOT = $savedDataRoot
        }

        if ($null -eq $savedBrowserArgs) {
            if (Test-Path Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS) {
                Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
            }
        } else {
            $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = $savedBrowserArgs
        }
    }

    # 隔離が本当に効いたかを、起動した実物で確かめてから先へ進む。
    try {
        Assert-HirakeIsolated -HostInfo $hostInfo -TimeoutSec $IsolationTimeoutSec
    } catch {
        Stop-HirakeHost -HostInfo $hostInfo
        throw
    }

    return $hostInfo
}

function Assert-HirakeIsolated {
    <#
    .SYNOPSIS
        起動した Hirake が本当に隔離先へ書いているかを確認する。
    .DESCRIPTION
        「隔離先に WebView2 のプロファイルができたか」で実物を確かめる。
        WebView2 の環境はタブの初期化時に作られるので、起動直後に必ず現れる。

        古いバイナリを掴む事故そのものは Test-HirakeSupportsDataRoot が起動前に
        止める。ここは環境変数の受け渡しが崩れた場合などに備えた 2 枚目の網で、
        「効かないまま検証を続けない」ことを担保する。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$HostInfo,
        [int]$TimeoutSec = 40
    )

    $marker = Join-Path $HostInfo.DataRoot 'WebView2'
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $marker) { return }
        if ($HostInfo.Process.HasExited) {
            throw "Hirake が起動直後に終了しました（終了コード $($HostInfo.Process.ExitCode)）。"
        }
        Start-Sleep -Milliseconds 500
    }

    throw @"
データ領域の隔離が効いていません。実利用のデータを汚す恐れがあるため中止しました。
  隔離先: $($HostInfo.DataRoot)（WebView2 プロファイルができていない）
  使った exe: $($HostInfo.ExePath)
HIRAKE_DATA_ROOT を知らない古いバイナリの可能性があります。発行し直してください:
  dotnet publish src/Hirake -c Release -r win-x64 --self-contained false -o publish
"@
}

function Stop-HirakeHost {
    <#
    .SYNOPSIS
        プロセスを終了し、隔離したデータ領域を消す。finally から呼ぶ。
    #>
    [CmdletBinding()]
    param([AllowNull()]$HostInfo)

    if ($null -eq $HostInfo) { return }

    if ($HostInfo.Process -and -not $HostInfo.Process.HasExited) {
        try { $HostInfo.Process.CloseMainWindow() | Out-Null } catch { }
        if (-not $HostInfo.Process.WaitForExit(5000)) {
            try { $HostInfo.Process.Kill() } catch { }
            try { $HostInfo.Process.WaitForExit(5000) | Out-Null } catch { }
        }
    }

    # 親が消えても WebView2 の子プロセスとデバッグポートはしばらく残る。
    # 次のケースは同じポートを使うため、ここで空くまで待たないと
    # 前のケースのターゲットへ繋いで誤判定しかねない。
    if ($HostInfo.Port) {
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            if (-not (Test-HirakeRunning) -and -not (Test-PortInUse -Port $HostInfo.Port)) { break }
            Start-Sleep -Milliseconds 500
        }
        if (Test-PortInUse -Port $HostInfo.Port) {
            Write-Warning "ポート $($HostInfo.Port) が解放されません。次のケースが前の実行へ繋がる恐れがあります。"
        }
    }

    # 同じデータ領域を掴んだ WebView2 の子プロセスが残っていると削除に失敗する。
    # 少し待って数回試し、それでも駄目ならパスを表示して人に委ねる（黙って諦めない）。
    if ($HostInfo.DataRoot -and (Test-Path -LiteralPath $HostInfo.DataRoot)) {
        # WebView2 の子プロセス（msedgewebview2.exe）が落ちきるまで数秒かかる。
        for ($i = 0; $i -lt 20; $i++) {
            try {
                Remove-Item -LiteralPath $HostInfo.DataRoot -Recurse -Force -ErrorAction Stop
                break
            } catch {
                Start-Sleep -Milliseconds 500
            }
        }
        if (Test-Path -LiteralPath $HostInfo.DataRoot) {
            Write-Warning "一時データ領域を削除できませんでした: $($HostInfo.DataRoot)"
        }
    }
}

function Open-CanvasOverview {
    <#
    .SYNOPSIS
        ドキュメントのページから全体俯瞰（Ctrl+G）を要求し、キャンバスのターゲットを返す。
    .DESCRIPTION
        キー入力の合成ではなく、viewer.js が Ctrl+G で送っているのと同じ postMessage を
        直接送る。ショートカットの経路（viewer.js → DocumentTab → IDocumentTabHost）は
        そのままなので、確認したい経路を迂回してはいない。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$DocumentSession,
        [Parameter(Mandatory)][int]$Port,
        [int]$TimeoutSec = 90
    )

    $request = "window.chrome.webview.postMessage({ type: 'shortcut', action: 'toggleGraphView' }); true"
    Invoke-CdpEvaluate -Session $DocumentSession -Expression $request | Out-Null

    return Wait-CdpTarget -Port $Port -Probe (Get-CanvasProbeExpression) `
        -TimeoutSec $TimeoutSec -What 'キャンバスのターゲット'
}

function Wait-CanvasReady {
    <#
    .SYNOPSIS
        ノードが描かれるまで待つ（グラフ構築と力学モデルの初期配置が終わるまで）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [int]$ExpectedNodes = 1,
        [int]$TimeoutSec = 90
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = 0
    while ((Get-Date) -lt $deadline) {
        $count = [int](Invoke-CdpEvaluate -Session $Session `
                -Expression "document.querySelectorAll('g.cv-node').length")
        $last = $count
        if ($count -ge $ExpectedNodes) { return $count }
        Start-Sleep -Milliseconds 500
    }
    throw "キャンバスにノードが揃いません（期待 $ExpectedNodes 件 / 実際 $last 件 / $TimeoutSec s）。"
}

function Save-CanvasScreenshot {
    <#
    .SYNOPSIS
        失敗時の証跡としてスクリーンショットを保存する（可視タブでのみ呼ぶこと）。
    .DESCRIPTION
        非表示タブへ Page.captureScreenshot を送るとハングする（既知の罠）。
        成功時は撮らない。ハングの要因を平常運転に持ち込まない。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][string]$Path
    )

    try {
        $result = Invoke-CdpCommand -Session $Session -Method 'Page.captureScreenshot' `
            -Params @{ format = 'png' } -TimeoutSec 20
        if ($null -eq $result -or -not $result.PSObject.Properties['data']) { return $null }

        $dir = Split-Path -Parent $Path
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        [System.IO.File]::WriteAllBytes($Path, [Convert]::FromBase64String($result.data))
        return $Path
    } catch {
        Write-Warning "スクリーンショットを保存できませんでした: $($_.Exception.Message)"
        return $null
    }
}
