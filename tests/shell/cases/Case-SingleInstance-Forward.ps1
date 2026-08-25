#requires -Version 5.1
<#
.SYNOPSIS
    二重起動時にパスが 1 番目のプロセスへ転送される（ISSUE #96）。

.DESCRIPTION
    SingleInstanceManager は固定名の Mutex（Hirake_SingleInstance_Mutex）で初回を
    判定し、2 番目以降は名前付きパイプ（Hirake_Pipe）でパスを送って終了する。
    ここが壊れると「.md をダブルクリックしても何も起きない」「ウィンドウが増える」
    のどちらかになるが、どちらも CLI では確認できず目視に頼っていた。

    見るのは 2 つ。
      - 2 番目のプロセスが**自分で終了する**（居座らない）
      - 1 番目のタブが増える（＝送ったパスが実際に開かれた）

    名前が固定なので、このケースは HIRAKE_DATA_ROOT を変えても隔離できない。
    ハーネスを並列に走らせられない理由でもある（→ docs/wiki/hirake-shell-harness.md）。
#>

Set-StrictMode -Version Latest

function Invoke-CaseForward {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Window,
        [Parameter(Mandatory)]$Context
    )

    $results = @()
    $itemType = [System.Windows.Automation.ControlType]::ListItem

    $tabList = Find-UiaById -Root $Window -AutomationId 'TabList' -TimeoutSec 10
    if (-not $tabList) {
        return (Assert-Ok -Label 'タブ一覧（TabList）を掴める' -Condition $false `
                -Detail 'AutomationId=TabList が見つかりません')
    }

    $count = Wait-UiaDescendantCount -Root $tabList -ControlType $itemType -Expected 1 -TimeoutSec 30
    $results += Assert-Ok -Label '起動直後のタブは 1 つ' -Condition ($count -eq 1) -Detail "実測 $count 件"

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $second = Start-HirakeSecondInstance -HostInfo $Context.HostInfo -OpenFile $Context.SecondFile -TimeoutSec 15
    $stopwatch.Stop()

    $results += Assert-Ok -Label '2 番目のプロセスは 15 秒以内に終了する' -Condition $second.Exited `
        -Detail ("{0:F1}s" -f $stopwatch.Elapsed.TotalSeconds)
    if (-not $second.Exited) { return $false }

    $results += Assert-Ok -Label '2 番目の終了コードは 0' -Condition ($second.ExitCode -eq 0) `
        -Detail ("終了コード {0}" -f $second.ExitCode)

    $count = Wait-UiaDescendantCount -Root $tabList -ControlType $itemType -Expected 2 -TimeoutSec 30
    $results += Assert-Ok -Label '1 番目のタブが 1 -> 2 に増える' -Condition ($count -eq 2) -Detail "実測 $count 件"

    if ($count -eq 2) {
        $tabs = @(Get-UiaDescendants -Root $tabList -ControlType $itemType)
        $texts = @($tabs | ForEach-Object { Get-UiaText -Element $_ })
        $results += Assert-Ok -Label '増えたタブは転送したファイル' `
            -Condition ([bool]($texts | Where-Object { $_ -like '*shell02*' })) `
            -Detail ("実際: {0}" -f ($texts -join ' / '))
    }

    return (-not ($results -contains $false))
}
