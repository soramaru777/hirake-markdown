#requires -Version 5.1
<#
.SYNOPSIS
    #90 の回帰: 重なったカードの前後関係が操作どおりに入れ替わる。

.DESCRIPTION
    #90 は「近景でカードが重なると、どれが手前か決まらない」不具合だった。
    canvas.js は z-index を applyStacking() だけで書く方針にしてあり、
    ホバー（Z_HOVER）とクリック固定（Z_FRONT。ホバーより強い）で順が決まる。

    ここでは重なっている 2 枚を実測で選び、
      1. ホバーしたカードが手前へ出る
      2. クリックで固定したカードはホバーより強い
    の 2 点を getComputedStyle().zIndex で確かめる。

    倍率は CARD_THRESHOLD（0.75）をわずかに超える 0.8 を使う。カードの寸法は
    画面 px 固定なので、倍率が低いほど画面上で近づき、重なりが起きやすい。
#>

Set-StrictMode -Version Latest

function Invoke-Case90 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)]$Context
    )

    Invoke-CanvasFit -Session $Session
    if (-not (Set-CanvasScale -Session $Session -Scale 0.8)) {
        Assert-Ok -Label 'カードモードへ入れる' -Condition $false -Detail '__cvSetView が効かない' | Out-Null
        return $false
    }

    $z = Measure-CardStacking -Session $Session
    $hasPair = ($z -and $z.found -eq $true)
    Assert-Ok -Label '重なっているカードの組がある' -Condition $hasPair `
        -Detail ("カード {0} 枚" -f $(if ($z) { $z.cardCount } else { 0 })) | Out-Null
    if (-not $hasPair) { return $false }

    $hoverOk = ([double]$z.hover.b -gt [double]$z.hover.a)
    Assert-Ok -Label 'ホバーしたカードが手前へ出る' -Condition $hoverOk `
        -Detail ("{0} > {1}" -f $z.hover.b, $z.hover.a) | Out-Null

    $frontOk = ([double]$z.front.a -gt [double]$z.front.b)
    Assert-Ok -Label 'クリック固定はホバーより強い' -Condition $frontOk `
        -Detail ("{0} > {1}" -f $z.front.a, $z.front.b) | Out-Null

    return ($hoverOk -and $frontOk)
}
