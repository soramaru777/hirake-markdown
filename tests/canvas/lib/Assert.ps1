#requires -Version 5.1
<#
.SYNOPSIS
    キャンバス検証ハーネスの assertion（ISSUE #94）。

.DESCRIPTION
    tests/*.ps1 と同じ「1 行 1 assertion・成功/失敗を数える」書き方に揃える。
    落ちた行がそのまま ISSUE 番号に対応するよう、ラベルは短く具体的に書くこと。

    集計はスクリプトスコープの $script:CanvasPassed / $script:CanvasFailed に持つ。
    ケース側は Assert-Ok を呼ぶだけでよい。
#>

Set-StrictMode -Version Latest

$script:CanvasPassed = 0
$script:CanvasFailed = 0

function Reset-CanvasAssertions {
    $script:CanvasPassed = 0
    $script:CanvasFailed = 0
}

function Get-CanvasPassedCount { return $script:CanvasPassed }
function Get-CanvasFailedCount { return $script:CanvasFailed }

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
        $script:CanvasPassed++
        Write-Host ("        ok   {0}{1}" -f $Label, $suffix)
        return $true
    }

    $script:CanvasFailed++
    Write-Host ("        NG   {0}{1}" -f $Label, $suffix)
    return $false
}
