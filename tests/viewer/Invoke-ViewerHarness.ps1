#requires -Version 5.1
<#
.SYNOPSIS
    ドキュメント表示（viewer.js）の描画を CDP で検証する（ISSUE #105）。

.DESCRIPTION
    tests/canvas が俯瞰画面（canvas.js）専用なのに対し、こちらは通常の文書タブが対象。
    #105（テーマ切替で mermaid 図が再描画されない）は、Markdig が出す HTML の形と
    viewer.js の受理条件の食い違いで起きた。壊れているかどうかはページ内の値
    （svg の id・図形の fill・click 記法の <a> の有無）で機械的に判定できるので、
    条件（testdata/mermaid-click.md）と手順（このスクリプト）を固定資産にする。

    起動・隔離・CDP 接続・assertion は tests/lib の共通層をそのまま使う。
    このスクリプトが足しているのは「文書タブに繋いでケースを呼ぶ」だけ。

    実行の前提:
      - Hirake を終了しておくこと（二重起動でパスが転送され、起動が別プロセスへ吸われる）
      - ポート 9333（-Port で変更可）が空いていること

.PARAMETER ExePath
    使う Hirake.exe。省略時は publish\Hirake.exe → src\Hirake\bin 配下の順に探す。

.PARAMETER Port
    WebView2 のリモートデバッグポート。

.PARAMETER Case
    実行するケースを絞る（105）。省略時はすべて。

.EXAMPLE
    pwsh -NoProfile -File tests\viewer\Invoke-ViewerHarness.ps1

.EXAMPLE
    pwsh -NoProfile -File tests\viewer\Invoke-ViewerHarness.ps1 -Case 105
#>

[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Port = 9333,
    [ValidateSet('105')]
    [string[]]$Case
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\lib\Assert.ps1')
. (Join-Path $PSScriptRoot '..\lib\Cdp.ps1')
. (Join-Path $PSScriptRoot '..\lib\HirakeHost.ps1')
. (Join-Path $PSScriptRoot 'cases\Case-105-MermaidTheme.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$artifactDir = Join-Path $PSScriptRoot '_artifacts'

# ケース定義。testdata はリポジトリ直下の testdata/ を使う（文書 1 本で足りるため
# ハーネス専用の生成物は持たない）。
$allCases = @(
    [pscustomobject]@{
        Id = '105'; Title = '#105 mermaid theme'; Entry = 'testdata\mermaid-click.md'
        ExpectedDiagrams = 1; Invoke = 'Invoke-Case105'
    }
)

$targets = if ($Case) { @($allCases | Where-Object { $Case -contains $_.Id }) } else { $allCases }

Write-Host ''
Write-Host '  Hirake viewer harness'

if (-not $ExePath) { $ExePath = Resolve-HirakeExe -RepoRoot $repoRoot }
if (-not $ExePath -or -not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Host '  Hirake.exe が見つかりません。先に発行してください:'
    Write-Host '    dotnet publish src/Hirake -c Release -r win-x64 --self-contained false -o publish'
    exit 2
}
$exeInfo = Get-Item -LiteralPath $ExePath
Write-Host ("  exe       : {0}" -f $ExePath)
Write-Host ("  built     : {0:yyyy-MM-dd HH:mm:ss}" -f $exeInfo.LastWriteTime)
Write-Host ("  port      : {0}" -f $Port)

# 隔離を知らない古いバイナリは起動した瞬間に実利用のデータ領域を使う。起動前に弾く。
$gateReason = ''
if (-not (Test-HirakeSupportsDataRoot -ExePath $ExePath -Reason ([ref]$gateReason))) {
    Write-Host ''
    Write-Host '  中止: この Hirake.exe は HIRAKE_DATA_ROOT を知りません（#94 より前のバイナリ）。'
    if ($gateReason) { Write-Host ("        理由: {0}" -f $gateReason) }
    Write-Host '        起動すると実利用のデータ領域を使ってしまうため、発行し直してください:'
    Write-Host '          dotnet publish src/Hirake -c Release -r win-x64 --self-contained false -o publish'
    exit 2
}

$problem = Test-HirakeHarnessPrecondition -Port $Port
if ($problem) {
    Write-Host ''
    Write-Host "  中止: $problem"
    exit 2
}

foreach ($c in $targets) {
    $file = Join-Path $repoRoot $c.Entry
    if (Test-Path -LiteralPath $file -PathType Leaf) { continue }
    Write-Host ''
    Write-Host "  中止: testdata が見つかりません: $file"
    exit 2
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$caseFailures = 0
$index = 0

foreach ($c in $targets) {
    $index++
    Write-Host ''
    Write-Host ("  [{0}/{1}] {2,-22} ({3})" -f $index, $targets.Count, $c.Title, $c.Entry)

    $entryFile = Join-Path $repoRoot $c.Entry
    $hostInfo = $null
    $docTarget = $null

    try {
        # 前のケースの後始末が終わっていないまま次を起動すると、古い CDP ターゲットへ
        # 繋いで「通ったように見える」誤判定になる。ケースごとに前提を見直す。
        $problem = Test-HirakeHarnessPrecondition -Port $Port
        if ($problem) { throw "前提が崩れました: $problem" }

        $hostInfo = Start-HirakeHost -ExePath $ExePath -OpenFile $entryFile -Port $Port
        Write-Host ("        data root : {0}" -f $hostInfo.DataRoot)

        $docTarget = Wait-CdpTarget -Port $Port -Probe (Get-DocumentProbeExpression) `
            -TimeoutSec 60 -What 'ドキュメントのターゲット'

        $context = [pscustomobject]@{
            ExpectedDiagrams = $c.ExpectedDiagrams
            EntryFile        = $entryFile
        }
        $ok = & $c.Invoke -Session $docTarget.Session -Context $context

        if (-not $ok) {
            $caseFailures++
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $shot = Save-CanvasScreenshot -Session $docTarget.Session `
                -Path (Join-Path $artifactDir ("case-{0}-{1}.png" -f $c.Id, $stamp))
            if ($shot) { Write-Host ("        screenshot: {0}" -f $shot) }
        }
    } catch {
        $caseFailures++
        Write-Host ("        NG   ケースが異常終了: {0}" -f $_.Exception.Message)
    } finally {
        # 切断の例外で後始末（Stop-HirakeHost）まで飛ばさない。Hirake が残ると次回の
        # 前提チェック（二重起動・ポート使用中）で止まる。
        try {
            if ($docTarget) { Disconnect-Cdp -Session $docTarget.Session }
        } catch {
            $caseFailures++
            Write-Host ("        NG   CDP 切断に失敗: {0}" -f $_.Exception.Message)
        }
        try {
            Stop-HirakeHost -HostInfo $hostInfo
            if ($hostInfo -and -not (Test-Path -LiteralPath $hostInfo.DataRoot)) {
                Write-Host ("        cleanup   : removed {0}" -f $hostInfo.DataRoot)
            }
        } catch {
            # 後始末の失敗も失敗として数える（Hirake を残したまま「全部成功」で終わらせない）。
            $caseFailures++
            Write-Host ("        NG   後始末に失敗: {0}" -f $_.Exception.Message)
        }
    }
}

$stopwatch.Stop()
$passed = Get-HarnessPassedCount
$failed = Get-HarnessFailedCount

Write-Host ''
Write-Host ("  passed: {0}  failed: {1}   ({2:F1}s)" -f $passed, $failed, $stopwatch.Elapsed.TotalSeconds)

if ($failed -eq 0 -and $caseFailures -eq 0) { exit 0 }
exit 1
