#requires -Version 5.1
<#
.SYNOPSIS
    #89 の回帰: 全体俯瞰で全ノードがビューポートに収まる。

.DESCRIPTION
    #89 は「端のノードのラベルが見切れる」不具合だった。ラベルまで含めた実寸で
    判定するため、g.cv-node の getBoundingClientRect() を #cv-stage の矩形と比べる
    （ラベルは g の子なので、円だけを見る判定では見切れを検出できない）。

    testdata の wide60 は端に長いファイル名のノードが来るようにしてある。
#>

Set-StrictMode -Version Latest

function Invoke-Case89 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)]$Context
    )

    Invoke-CanvasFit -Session $Session
    $m = Get-CanvasFitMeasurement -Session $Session

    $expected = [int]$Context.ExpectedNodes
    Assert-Ok -Label 'ノードが testdata の件数どおり描かれている' `
        -Condition ([int]$m.counted -eq $expected) `
        -Detail ("実測 {0} 件 / 期待 {1} 件" -f $m.counted, $expected) | Out-Null

    # はみ出しの許容は 0.5px。サブピクセルの丸めだけを吸収する。
    $contained = Test-CanvasContained -Measurement $m
    Assert-Ok -Label '全ノードがビューポート内' -Condition $contained.Fits -Detail $contained.Detail | Out-Null

    return $contained.Fits
}
