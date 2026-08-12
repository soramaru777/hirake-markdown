#requires -Version 5.1
<#
.SYNOPSIS
    Hirake の .md / .markdown 関連付け（HKCU）を削除します。
    管理者権限は不要です。他アプリが設定した既定プログラムや OpenWithProgIds の
    エントリは、通常の逐次実行では変更しません（空になったキーの削除だけは、
    判定と削除の間に別プロセスが値を書き込んだ場合に巻き添えにする余地が
    わずかに残ります。下記 Remove-RegistryKeyIfEmpty のコメントを参照）。

    このスクリプトの契約は「現時点で存在する Hirake の関連付けをすべて解除する」
    ことであり、「register.ps1 が今回作成したものだけを消す」ではありません
    （どちらが作ったかを記録していないため）。したがって、

      - Hirake が追加した値を除去し、その結果として空になった関連キー
        （OpenWithProgIds / 拡張子キー）も削除します。実行前から空のキーが
        存在していた場合、そのキーも掃除の対象になります。
      - 実行前から Hirake の関連付けが存在した場合は、それも解除されます
        （Hirake の関連付けを消すことが目的なので、これが正しい挙動です）。

    失敗した場合は終了コード 1 で終わります。その時点で一部の変更が適用済みの
    ことがあります（値ごとのロールバックは行いません）。原因を解消してから
    再実行してください。
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# レジストリ操作は register.ps1 と共通のヘルパーに集約している。
$libPath = Join-Path $PSScriptRoot 'association-lib.ps1'
if (-not (Test-Path -LiteralPath $libPath -PathType Leaf)) {
    Write-Error "共通スクリプトが見つかりません: $libPath"
    exit 1
}
. $libPath

$progId = 'Hirake.md'
$progIdKey = "HKCU:\Software\Classes\$progId"
$schemeKey = 'HKCU:\Software\Classes\hirake'
$exeName = 'Hirake.exe'
$failures = 0

$targets = @(
    @{ Path = $progIdKey; Label = "ProgId '$progId'" },
    @{ Path = $schemeKey; Label = "URL プロトコル 'hirake://'" }
)

# 1. Hirake 名義のキーの状態を、拡張子側に手を付ける前にすべて確認する。
#    削除できないキーが 1 つでもあれば、レジストリを 1 つも変更せずに中止する。
#    後から気付く形にすると、削除できないキーを参照したままの OpenWithProgIds
#    エントリだけが先に消える中途半端な解除になるため。
foreach ($target in $targets) {
    switch (Test-HirakeOwnedKey -Path $target.Path -ExeName $exeName) {
        'NotOwned' {
            Write-Error "$($target.Label) のキーは Hirake 以外のプログラムが使用しているため、解除を中止しました（レジストリは変更していません）: $($target.Path)"
            exit 1
        }
        'Unknown' {
            Write-Error "$($target.Label) のキーは shell\open\command が無く由来を判定できないため、解除を中止しました（レジストリは変更していません）。register.ps1 を実行して登録を修復してから、再度 unregister.ps1 を実行してください: $($target.Path)"
            exit 1
        }
    }
}

# 2. .md / .markdown の解除
#    自分が足した値だけを削除し、空になったキーを掃除する。
#    メッセージは「実際に削除できたとき」だけ表示する。
foreach ($ext in @('.md', '.markdown')) {
    try {
        $result = Unregister-HirakeExtension -Extension $ext -ProgId $progId

        if ($result.RemovedProgId) {
            Write-Host "拡張子 '$ext' の OpenWithProgIds から '$progId' を削除しました。"
        }

        if ($result.RemovedDefault) {
            Write-Host "'$ext' の既定プログラム設定（'$progId'）を削除しました。"
        } elseif ($result.ExistingDefault) {
            Write-Host "'$ext' の既定プログラムは '$($result.ExistingDefault)' のままです（Hirake が設定したものではないため削除しません）。"
        }

        if ($result.RemovedExtKey) {
            Write-Host "空になった拡張子キー '$ext' を削除しました。"
        }
    } catch {
        $failures++
        Write-Warning "拡張子 '$ext' の解除に失敗しました: $($_.Exception.Message)"
    }
}

# 3. ProgId と URL プロトコルを削除する。
#    拡張子側の解除に失敗している場合は、参照先だけが消えたダングリング状態を
#    作らないよう削除を見送る。
if ($failures -gt 0) {
    Write-Warning "拡張子の解除に失敗したため、ProgId '$progId' と URL プロトコル 'hirake://' は削除しません（拡張子側に参照が残るため）。"
} else {
    foreach ($target in $targets) {
        try {
            $status = Remove-HirakeOwnedKey -Path $target.Path -ExeName $exeName
            switch ($status) {
                'Removed'  { Write-Host "$($target.Label) を削除しました。" }
                'NotFound' { Write-Host "$($target.Label) は登録されていませんでした。" }
                'Unknown'  {
                    $failures++
                    Write-Warning "$($target.Label) は shell\open\command が無く由来を判定できないため削除しませんでした。register.ps1 を実行してから再度 unregister.ps1 を実行してください: $($target.Path)"
                }
                'NotOwned' {
                    # 手順 1 で確認済みなので通常ここには来ない（実行中に書き換えられた場合のみ）。
                    $failures++
                    Write-Warning "$($target.Label) は Hirake 以外のプログラムを指しているため削除しませんでした。"
                }
            }
        } catch {
            $failures++
            Write-Warning "$($target.Label) を削除できませんでした: $($_.Exception.Message)"
        }
    }
}

# 4. エクスプローラーへの反映通知
#    （SHChangeNotify はファイル関連付け向けの通知。URL プロトコルには不要。）
Send-ShellChangeNotification

if ($failures -gt 0) {
    Write-Host ''
    Write-Warning "$failures 件の処理に失敗しました。上記の警告を確認してください。"
    exit 1
}

Write-Host ''
Write-Host '===================================================================='
Write-Host 'Hirake の関連付け解除が完了しました。'
Write-Host '===================================================================='
