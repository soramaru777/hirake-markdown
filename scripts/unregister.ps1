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
$schemeKey = 'HKCU:\Software\Classes\hirake'
$exeName = 'Hirake.exe'

# 手順そのものは association-lib.ps1 の Invoke-HirakeUnregister が持つ。
# ここに残すのは、表示と終了コードの適用だけ。
$result = Invoke-HirakeUnregister `
    -ProgId          $progId `
    -SchemeKey       $schemeKey `
    -Extensions      @('.md', '.markdown') `
    -ExpectedExeName $exeName

$presentation = Get-HirakeUnregisterPresentation `
    -Result    $result `
    -ProgId    $progId `
    -SchemeKey $schemeKey

Write-HirakePresentation -Presentation $presentation

# エクスプローラーへの反映通知
# （SHChangeNotify はファイル関連付け向けの通知。URL プロトコルには不要。）
if ($presentation.Notify) {
    Send-ShellChangeNotification
}

exit $presentation.ExitCode
