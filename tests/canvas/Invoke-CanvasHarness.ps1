#requires -Version 5.1
<#
.SYNOPSIS
    キャンバスの描画を CDP で検証する（ISSUE #94）。

.DESCRIPTION
    #89（全体俯瞰の見切れ）と #90（カードの重なり）は、どちらも WebView2 内の
    描画が絡むため CLI で確認できず、毎回その場で条件を用意して目視していた。
    どちらもページ内の値を見れば判定できるので、条件（testdata）と手順（このスクリプト）
    を固定資産にして、次に壊れたときに気づけるようにする。

    実行の前提:
      - Hirake を終了しておくこと（二重起動でパスが転送され、起動が別プロセスへ吸われる）
      - ポート 9333（-Port で変更可）が空いていること

    データ領域は HIRAKE_DATA_ROOT で %TEMP% の使い捨てフォルダへ丸ごと逃がす。
    実利用の settings.json / canvas（ピン留めはアプリ全体で共有される）/ WebView2
    プロファイルには触らない。隔離先は必ず冒頭に表示する。

.PARAMETER ExePath
    使う Hirake.exe。省略時は publish\Hirake.exe → src\Hirake\bin 配下の順に探す。

.PARAMETER Port
    WebView2 のリモートデバッグポート。

.PARAMETER Case
    実行するケースを絞る（89 / 90 / 93）。省略時はすべて。

.EXAMPLE
    pwsh -NoProfile -File tests\canvas\Invoke-CanvasHarness.ps1

.EXAMPLE
    pwsh -NoProfile -File tests\canvas\Invoke-CanvasHarness.ps1 -Case 89
#>

[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Port = 9333,
    [ValidateSet('89', '90', '93')]
    [string[]]$Case
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\lib\Assert.ps1')
. (Join-Path $PSScriptRoot '..\lib\Cdp.ps1')
. (Join-Path $PSScriptRoot '..\lib\HirakeHost.ps1')
. (Join-Path $PSScriptRoot 'lib\CanvasEval.ps1')
. (Join-Path $PSScriptRoot 'cases\Case-89-FitToContent.ps1')
. (Join-Path $PSScriptRoot 'cases\Case-90-CardStacking.ps1')
. (Join-Path $PSScriptRoot 'cases\Case-93-ZoomFloor.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$testDataRoot = Join-Path $PSScriptRoot 'testdata'
$artifactDir = Join-Path $PSScriptRoot '_artifacts'

# ケース定義。1 ファイル 1 観点にして、落ちた行がそのまま ISSUE 番号に対応するようにする。
$allCases = @(
    [pscustomobject]@{
        Id = '90'; Title = '#90 card stacking'; TestData = 'overlap30'
        ExpectedNodes = 30; Entry = 'overlap-01.md'; Invoke = 'Invoke-Case90'
    }
    [pscustomobject]@{
        Id = '89'; Title = '#89 fit to content'; TestData = 'wide60'
        ExpectedNodes = 60; Entry = 'wide-01.md'; Invoke = 'Invoke-Case89'
    }
    [pscustomobject]@{
        Id = '93'; Title = '#93 zoom floor'; TestData = 'dense500'
        ExpectedNodes = 500; Entry = 'dense-0001.md'; Invoke = 'Invoke-Case93'
    }
)

$targets = if ($Case) { @($allCases | Where-Object { $Case -contains $_.Id }) } else { $allCases }

Write-Host ''
Write-Host '  Hirake canvas harness'

if (-not $ExePath) { $ExePath = Resolve-HirakeExe -RepoRoot $repoRoot }
if (-not $ExePath -or -not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Host '  Hirake.exe が見つかりません。先に発行してください:'
    Write-Host '    dotnet publish src/Hirake -c Release -r win-x64 --self-contained false -o publish'
    exit 2
}
# 更新日時も出す。古い publish 成果物を掴むと HIRAKE_DATA_ROOT を知らない
# バイナリで走りかねない（起動後に Assert-HirakeIsolated が実物で確かめるが、
# 気づきは早いほどよい）。
$exeInfo = Get-Item -LiteralPath $ExePath
Write-Host ("  exe       : {0}" -f $ExePath)
Write-Host ("  built     : {0:yyyy-MM-dd HH:mm:ss}" -f $exeInfo.LastWriteTime)
Write-Host ("  port      : {0}" -f $Port)

# 起動する前に「その exe が隔離を知っているか」を確かめる。知らないバイナリは
# 起動した瞬間に実利用の %LocalAppData%\Hirake を作りに行くため、後追いの検知では遅い。
if (-not (Test-HirakeSupportsDataRoot -ExePath $ExePath)) {
    Write-Host ''
    Write-Host '  中止: この Hirake.exe は HIRAKE_DATA_ROOT を知りません（#94 より前のバイナリ）。'
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

# testdata の存在確認。dense500 は生成物なので、無ければ作り方を案内する。
foreach ($c in $targets) {
    $dir = Join-Path $testDataRoot $c.TestData
    if (Test-Path -LiteralPath $dir -PathType Container) { continue }

    Write-Host ''
    Write-Host "  中止: testdata が見つかりません: $dir"
    if ($c.TestData -eq 'dense500') {
        Write-Host '        生成してから再実行してください:'
        Write-Host '          pwsh -NoProfile -File tests\canvas\tools\New-DenseTestdata.ps1'
    }
    exit 2
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$caseFailures = 0
$index = 0

foreach ($c in $targets) {
    $index++
    Write-Host ''
    Write-Host ("  [{0}/{1}] {2,-22} ({3})" -f $index, $targets.Count, $c.Title, $c.TestData)

    $entryFile = Join-Path (Join-Path $testDataRoot $c.TestData) $c.Entry
    $hostInfo = $null
    $docTarget = $null
    $canvasTarget = $null

    try {
        # 前のケースの後始末が終わっていないまま次を起動すると、古い CDP ターゲットへ
        # 繋いで「通ったように見える」誤判定になる。ケースごとに前提を見直す。
        $problem = Test-HirakeHarnessPrecondition -Port $Port
        if ($problem) { throw "前提が崩れました: $problem" }

        $hostInfo = Start-HirakeHost -ExePath $ExePath -OpenFile $entryFile -Port $Port
        Write-Host ("        data root : {0}" -f $hostInfo.DataRoot)

        $docTarget = Wait-CdpTarget -Port $Port -Probe (Get-DocumentProbeExpression) `
            -TimeoutSec 60 -What 'ドキュメントのターゲット'
        $canvasTarget = Open-CanvasOverview -DocumentSession $docTarget.Session -Port $Port
        Wait-CanvasReady -Session $canvasTarget.Session -ExpectedNodes $c.ExpectedNodes | Out-Null

        $context = [pscustomobject]@{
            ExpectedNodes = $c.ExpectedNodes
            TestData      = $c.TestData
            EntryFile     = $entryFile
        }
        $ok = & $c.Invoke -Session $canvasTarget.Session -Context $context

        if (-not $ok) {
            $caseFailures++
            $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
            $shot = Save-CanvasScreenshot -Session $canvasTarget.Session `
                -Path (Join-Path $artifactDir ("case-{0}-{1}.png" -f $c.Id, $stamp))
            if ($shot) { Write-Host ("        screenshot: {0}" -f $shot) }
        }
    } catch {
        $caseFailures++
        Write-Host ("        NG   ケースが異常終了: {0}" -f $_.Exception.Message)
    } finally {
        if ($docTarget) { Disconnect-Cdp -Session $docTarget.Session }
        if ($canvasTarget) { Disconnect-Cdp -Session $canvasTarget.Session }
        try {
            Stop-HirakeHost -HostInfo $hostInfo
            if ($hostInfo -and -not (Test-Path -LiteralPath $hostInfo.DataRoot)) {
                Write-Host ("        cleanup   : removed {0}" -f $hostInfo.DataRoot)
            }
        } catch {
            # 後始末の失敗も失敗として数える。ここを黙って通すと、Hirake を残したまま
            # 「全部成功」で終わる（最後のケースでは次の前提チェックも走らない）。
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
