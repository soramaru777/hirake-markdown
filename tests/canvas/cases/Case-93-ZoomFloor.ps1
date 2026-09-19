#requires -Version 5.1
<#
.SYNOPSIS
    #93 の回帰: ズーム下限に当たる規模でも全体俯瞰が全ノードを収める。

.DESCRIPTION
    #93 は「ノードが多いと必要倍率が既定の下限 0.1 を下回り、俯瞰に収まらない」件。
    fitToContent() は必要な倍率まで下限そのものを下げる（lowerMinScale）作りになった。

    ここでは 500 ノードの生成 testdata を使い、
      1. 実際に k が 0.1 未満まで下がっている（下限に当たる規模である）
      2. その状態で全ノードが収まっている
    の 2 点を確かめる。1 が満たされないと 2 は「たまたま収まっただけ」になるため、
    規模の確認を先に置く。
#>

Set-StrictMode -Version Latest

function Invoke-Case93 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)]$Context
    )

    # 500 ノードは力学モデルの収束に時間がかかる。待ちを長めに取る。
    Invoke-CanvasFit -Session $Session -SettleMs 6000
    $m = Get-CanvasFitMeasurement -Session $Session

    $expected = [int]$Context.ExpectedNodes
    Assert-Ok -Label 'ノードが testdata の件数どおり描かれている' `
        -Condition ([int]$m.counted -eq $expected) `
        -Detail ("実測 {0} 件 / 期待 {1} 件" -f $m.counted, $expected) | Out-Null

    $k = if ($null -eq $m.k) { $null } else { [double]$m.k }
    $belowFloor = ($null -ne $k -and $k -lt 0.1)
    Assert-Ok -Label '既定のズーム下限 0.1 を下回るまで縮む' -Condition $belowFloor `
        -Detail $(if ($null -eq $k) { 'k を取得できない' } else { "k = {0:F3}" -f $k }) | Out-Null

    $contained = Test-CanvasContained -Measurement $m
    Assert-Ok -Label '下限を下げた状態でも全ノードがビューポート内' `
        -Condition $contained.Fits -Detail $contained.Detail | Out-Null

    return ($belowFloor -and $contained.Fits)
}
