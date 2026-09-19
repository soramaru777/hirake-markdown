#requires -Version 5.1
<#
.SYNOPSIS
    検証ハーネス共通の assertion（ISSUE #94 / #96）。

.DESCRIPTION
    tests/*.ps1 と同じ「1 行 1 assertion・成功/失敗を数える」書き方に揃える。
    落ちた行がそのまま ISSUE 番号に対応するよう、ラベルは短く具体的に書くこと。
    キャンバス（CDP）とシェル（UI Automation）の両ハーネスが dot-source する。

    集計はスクリプトスコープの $script:HarnessPassed / $script:HarnessFailed に持つ。
    ケース側は Assert-Ok を呼ぶだけでよい。
#>

Set-StrictMode -Version Latest

$script:HarnessPassed = 0
$script:HarnessFailed = 0

function Reset-HarnessAssertions {
    $script:HarnessPassed = 0
    $script:HarnessFailed = 0
}

function Get-HarnessPassedCount { return $script:HarnessPassed }
function Get-HarnessFailedCount { return $script:HarnessFailed }

function Assert-Ok {
    <#
    .SYNOPSIS
        条件を 1 つ判定して結果を印字する。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][AllowNull()]$Condition,
        [string]$Detail
    )

    $suffix = if ($Detail) { " ($Detail)" } else { '' }
    if ($Condition -eq $true) {
        $script:HarnessPassed++
        Write-Host ("        ok   {0}{1}" -f $Label, $suffix)
        return $true
    }

    $script:HarnessFailed++
    Write-Host ("        NG   {0}{1}" -f $Label, $suffix)
    return $false
}
