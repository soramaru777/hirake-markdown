#requires -Version 5.1
<#
.SYNOPSIS
    WPF シェル側を UI Automation で検証する（ISSUE #96）。

.DESCRIPTION
    WebView2 の中は CDP で見られる（→ #94）が、**WPF シェル側は CDP から一切触れない。**
    ツールバーの並び・タブ操作・二重起動のパス転送は全部目視で、実際に手戻りが出た
    （#86）。ここを UI Automation のツリーで固定資産にする。

    見えないものもはっきりさせておく。**UI Automation は描画順（paint order）を
    公開しない。** #90 の重なりや #89 の SVG ノード位置は原理的に観測できないので、
    そちらは #94 の CDP 側の担当のまま（→ docs/wiki/hirake-shell-harness.md）。

    実行の前提:
      - Hirake を終了しておくこと（二重起動でパスが転送され、起動が別プロセスへ吸われる）
      - ポート 9333（-Port で変更可）が空いていること

    データ領域は HIRAKE_DATA_ROOT で %TEMP% の使い捨てフォルダへ丸ごと逃がす
    （#94 と同じ仕組みをそのまま使う）。実利用の settings.json / canvas / WebView2
    プロファイルには触らない。隔離先は必ずケースごとに表示する。

    終了コード: 0 = 全て成功 / 1 = 失敗あり / 2 = そもそも回せなかった

.PARAMETER ExePath
    使う Hirake.exe。省略時は publish\Hirake.exe → src\Hirake\bin 配下の順に探す。

.PARAMETER Port
    WebView2 のリモートデバッグポート。このハーネス自体は CDP を使わないが、
    起動条件を #94 と揃えるため同じ口を開ける（→ 前提チェックの対象）。

.PARAMETER Case
    実行するケースを絞る（86 / tabs / forward）。省略時はすべて。

.EXAMPLE
    pwsh -NoProfile -File tests\shell\Invoke-ShellHarness.ps1

.EXAMPLE
    pwsh -NoProfile -File tests\shell\Invoke-ShellHarness.ps1 -Case 86
#>

[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Port = 9333,
    [ValidateSet('86', 'tabs', 'forward')]
    [string[]]$Case
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\lib\Assert.ps1')
. (Join-Path $PSScriptRoot '..\lib\HirakeHost.ps1')
. (Join-Path $PSScriptRoot 'lib\Uia.ps1')
. (Join-Path $PSScriptRoot 'cases\Case-86-ToolbarOrder.ps1')
. (Join-Path $PSScriptRoot 'cases\Case-Tabs-SwitchAndClose.ps1')
. (Join-Path $PSScriptRoot 'cases\Case-SingleInstance-Forward.ps1')

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$testDataRoot = Join-Path $PSScriptRoot 'testdata'

# ケース定義。1 ファイル 1 観点にして、落ちた行がそのまま ISSUE 番号に対応するようにする。
$allCases = @(
    [pscustomobject]@{
        Id = '86'; Title = '#86 toolbar order'; Entry = 'shell01.md'
        Second = $null; Invoke = 'Invoke-Case86'
    }
    [pscustomobject]@{
        Id = 'tabs'; Title = 'tab switch/close'; Entry = 'shell01.md'
        Second = 'shell02.md'; Invoke = 'Invoke-CaseTabs'
    }
    [pscustomobject]@{
        Id = 'forward'; Title = 'single instance'; Entry = 'shell01.md'
        Second = 'shell02.md'; Invoke = 'Invoke-CaseForward'
    }
)

$targets = if ($Case) { @($allCases | Where-Object { $Case -contains $_.Id }) } else { $allCases }

Write-Host ''
Write-Host '  Hirake shell harness'

if (-not $ExePath) { $ExePath = Resolve-HirakeExe -RepoRoot $repoRoot }
if (-not $ExePath -or -not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    Write-Host '  Hirake.exe が見つかりません。先に発行してください:'
    Write-Host '    dotnet publish src/Hirake -c Release -r win-x64 --self-contained false -o publish'
    exit 2
}

$exeInfo = Get-Item -LiteralPath $ExePath
Write-Host ("  exe       : {0}" -f $ExePath)
Write-Host ("  built     : {0:yyyy-MM-dd HH:mm:ss}" -f $exeInfo.LastWriteTime)
Write-Host ("  pwsh      : {0}" -f $PSVersionTable.PSVersion)

# UI Automation が読めない環境（Windows 以外・GUI の無い SKU）では、
# 「失敗」ではなく「回せなかった」として区別する。
try {
    Initialize-Uia
} catch {
    Write-Host ''
    Write-Host "  中止: UI Automation を読み込めません: $($_.Exception.Message)"
    exit 2
}

# 起動する前に「その exe が隔離を知っているか」を確かめる。知らないバイナリは
# 起動した瞬間に実利用の %LocalAppData%\Hirake を作りに行くため、後追いでは遅い（#94）。
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

foreach ($name in @('shell01.md', 'shell02.md')) {
    $path = Join-Path $testDataRoot $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { continue }
    Write-Host ''
    Write-Host "  中止: testdata が見つかりません: $path"
    exit 2
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$caseFailures = 0
$index = 0

foreach ($c in $targets) {
    $index++
    Write-Host ''
    Write-Host ("  [{0}/{1}] {2,-22} ({3})" -f $index, $targets.Count, $c.Title, $c.Entry)

    $hostInfo = $null

    try {
        # 前のケースの後始末が終わらないまま次を起動すると、2 番目のプロセスになって
        # 起動が吸われる。ケースごとに前提を見直し、崩れていたらそのケースを失敗させる。
        $problem = Test-HirakeHarnessPrecondition -Port $Port
        if ($problem) { throw "前提が崩れました: $problem" }

        $entryFile = Join-Path $testDataRoot $c.Entry
        $hostInfo = Start-HirakeHost -ExePath $ExePath -OpenFile $entryFile -Port $Port
        Write-Host ("        data root : {0}" -f $hostInfo.DataRoot)

        $window = Wait-HirakeMainWindow -HostInfo $hostInfo -TimeoutSec 30

        $context = [pscustomobject]@{
            HostInfo   = $hostInfo
            EntryFile  = $entryFile
            SecondFile = if ($c.Second) { Join-Path $testDataRoot $c.Second } else { $null }
        }

        $ok = & $c.Invoke -Window $window -Context $context
        if (-not $ok) { $caseFailures++ }
    } catch {
        $caseFailures++
        Write-Host ("        NG   ケースが異常終了: {0}" -f $_.Exception.Message)
    } finally {
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
