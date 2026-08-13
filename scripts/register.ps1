#requires -Version 5.1
<#
.SYNOPSIS
    Hirake を .md / .markdown ファイルの「プログラムから開く」候補として HKCU に登録します。
    管理者権限は不要です（HKCU のみを使用）。

    他アプリが既に設定している .md の既定プログラムや OpenWithProgIds のエントリは
    変更しません。unregister.ps1 で現時点の Hirake 関連付けを解除できますが、
    実行前のレジストリ状態を復元するものではありません。

    失敗時は終了コード 1 で終了します。値ごとのロールバックは行わないため、
    その時点で一部の変更が適用済みのことがあります。原因を解消してから
    再実行してください（何度実行しても同じ結果になります）。

.PARAMETER ExePath
    Hirake.exe への明示パス。ファイル名は 'Hirake.exe' である必要があります
    （unregister.ps1 がこの名前を手掛かりに Hirake の登録かどうかを判定するため）。
    省略時は以下の順で自動検出します。
      1. <スクリプトの親>\publish\Hirake.exe
      2. <スクリプトの親>\src\Hirake\bin\Release\net10.0-windows\Hirake.exe
    どちらも見つからない場合はエラーを表示して終了します。

.EXAMPLE
    .\register.ps1
    .\register.ps1 -ExePath "C:\Tools\Hirake\Hirake.exe"
#>

[CmdletBinding()]
param(
    [string]$ExePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# レジストリ操作は unregister.ps1 と共通のヘルパーに集約している。
$libPath = Join-Path $PSScriptRoot 'association-lib.ps1'
if (-not (Test-Path -LiteralPath $libPath -PathType Leaf)) {
    Write-Error "共通スクリプトが見つかりません: $libPath"
    exit 1
}
. $libPath

function Resolve-HirakeExePath {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit -PathType Leaf) {
            return (Resolve-Path -LiteralPath $Explicit).ProviderPath
        }
        Write-Error "指定された -ExePath が見つかりません: $Explicit"
        exit 1
    }

    $candidate1 = Join-Path $PSScriptRoot '..\publish\Hirake.exe'
    if (Test-Path -LiteralPath $candidate1 -PathType Leaf) {
        return (Resolve-Path -LiteralPath $candidate1).ProviderPath
    }

    $candidate2 = Join-Path $PSScriptRoot '..\src\Hirake\bin\Release\net10.0-windows\Hirake.exe'
    if (Test-Path -LiteralPath $candidate2 -PathType Leaf) {
        return (Resolve-Path -LiteralPath $candidate2).ProviderPath
    }

    Write-Error @"
Hirake.exe が見つかりませんでした。以下のいずれかの方法で解決してください。
  1. -ExePath パラメータで exe のフルパスを明示指定する
  2. 次のいずれかの場所に exe を配置してビルドする
       $((Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath)\publish\Hirake.exe
       $((Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath)\src\Hirake\bin\Release\net10.0-windows\Hirake.exe
"@
    exit 1
}

$resolvedExePath = Resolve-HirakeExePath -Explicit $ExePath

$progId = 'Hirake.md'
$schemeKey = 'HKCU:\Software\Classes\hirake'
$exeName = 'Hirake.exe'

# 手順そのものは association-lib.ps1 の Invoke-HirakeRegister が持つ。
# ここに残すのは、引数の解決・メッセージ表示・終了コードだけ
# （手順を関数にしておくことで、テストから使い捨ての対象を渡して
#  本番と同一の順序・同一の中止判断を検証できる）。
$result = Invoke-HirakeRegister `
    -ExePath          $resolvedExePath `
    -ProgId           $progId `
    -SchemeKey        $schemeKey `
    -Extensions       @('.md', '.markdown') `
    -DefaultExtension '.md' `
    -ExpectedExeName  $exeName

# 表示内容と終了コードの決定も退行しうるため、純粋関数として切り出してある
# （tests/test-association-flow.ps1 がレジストリに触れずに検証する）。
$presentation = Get-HirakeRegisterPresentation `
    -Result          $result `
    -ExePath         $resolvedExePath `
    -ProgId          $progId `
    -SchemeKey       $schemeKey `
    -ExpectedExeName $exeName

Write-HirakePresentation -Presentation $presentation

# エクスプローラーへの反映通知は、実際に書き込みを行った場合だけ意味がある。
# （中止パスは Write-Error が終端例外になるためここへは到達しない）
if ($presentation.Notify) {
    Send-ShellChangeNotification
}

exit $presentation.ExitCode