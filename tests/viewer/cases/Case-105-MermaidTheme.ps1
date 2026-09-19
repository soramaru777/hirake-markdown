#requires -Version 5.1
<#
.SYNOPSIS
    #105 の回帰: テーマ切替で mermaid 図が再描画される。

.DESCRIPTION
    #105 は「Markdig Diagrams 拡張が出す <pre class="mermaid"> を viewer.js が拾わず、
    mermaid の自動描画（startOnLoad）が初回テーマ固定で描いていた」不具合。
    壊れているかは DOM の実測値で分かる:

      - pre.mermaid が残っていない（viewer.js が div.mermaid へ正規化した）
      - svg の id が mdv-mermaid-N（viewer.js 採番。自動描画なら mermaid-<epoch>）
      - __mdvSetTheme() のたびに svg が作り直され（id が変わる）、図形の fill が変わる
      - click 記法の <a> が生成されている（securityLevel: 'antiscript' でリンクが有効）
      - data-src-line が div へ引き継がれている（ソースマップ・スクロール復元の材料）

    testdata/mermaid-click.md は図 1 本・click 記法 4 本の固定条件。
#>

Set-StrictMode -Version Latest

# ページ内の観測式。判定の根拠はすべてこの戻り値。
$script:MermaidStateExpression = @'
(function () {
  var article = document.querySelector('article') || document.body;
  var svg = article.querySelector('div.mermaid > svg');
  var shape = svg ? svg.querySelector('.node rect, .node polygon, .node circle, .node path') : null;
  var hrefOf = function (a) { return a.getAttribute('href') || a.getAttribute('xlink:href') || ''; };
  var anchors = svg ? Array.prototype.slice.call(svg.querySelectorAll('a')) : [];
  return {
    hrefs: anchors.map(hrefOf),
    jsHrefCount: anchors.filter(function (a) { return /^\s*javascript:/i.test(hrefOf(a)); }).length,
    theme: document.documentElement.getAttribute('data-theme'),
    preCount: article.querySelectorAll('pre.mermaid').length,
    codeCount: article.querySelectorAll('pre code.language-mermaid').length,
    svgCount: article.querySelectorAll('div.mermaid > svg').length,
    errorCount: article.querySelectorAll('.mermaid-error').length,
    svgId: svg ? svg.id : null,
    anchorCount: svg ? svg.querySelectorAll('a').length : 0,
    fill: shape ? getComputedStyle(shape).fill : null,
    srcLineCount: article.querySelectorAll('div.mermaid[data-src-line]').length
  };
})()
'@

function Get-MermaidState {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Session)
    return Invoke-CdpEvaluate -Session $Session -Expression $script:MermaidStateExpression -TimeoutSec 10
}

function Wait-MermaidRendered {
    <#
    .SYNOPSIS
        図が描かれるまで（svg が期待数そろい、かつ id が前回と変わるまで）ポーリングする。
    .DESCRIPTION
        mermaid.render は非同期。テーマ切替の再描画も同じ経路なので、「id が変わった」を
        完了の印にする（renderId は描画のたびに採番される）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][int]$ExpectedDiagrams,
        [AllowNull()][string]$PreviousSvgId,
        [int]$TimeoutSec = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Get-MermaidState -Session $Session
        if ($last -and [int]$last.svgCount -ge $ExpectedDiagrams -and $last.svgId -and $last.svgId -ne $PreviousSvgId) {
            return $last
        }
        Start-Sleep -Milliseconds 300
    }
    $detail = if ($last) { $last | ConvertTo-Json -Compress } else { 'no state' }
    throw "mermaid の描画が完了しません（$TimeoutSec s）: $detail"
}

function Set-ViewerTheme {
    <#
    .SYNOPSIS
        ホストが呼ぶのと同じ公開関数でテーマを切り替える（viewer.js → 再描画の経路をそのまま通す）。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][ValidateSet('light', 'dark')][string]$Theme
    )
    $expr = "(function(){ window.__mdvSetTheme('$Theme'); return document.documentElement.getAttribute('data-theme'); })()"
    return Invoke-CdpEvaluate -Session $Session -Expression $expr -TimeoutSec 10
}

function Invoke-Case105 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)]$Context
    )

    $expected = [int]$Context.ExpectedDiagrams
    $allOk = $true

    # 初回描画。OS テーマで初期テーマが揺れるので、まず light に揃えてから測る。
    $initial = Wait-MermaidRendered -Session $Session -ExpectedDiagrams $expected -PreviousSvgId $null
    Set-ViewerTheme -Session $Session -Theme 'light' | Out-Null
    $light = Wait-MermaidRendered -Session $Session -ExpectedDiagrams $expected -PreviousSvgId $initial.svgId

    $allOk = (Assert-Ok -Label 'pre.mermaid が残っていない（viewer.js が変換した）' `
        -Condition ([int]$light.preCount -eq 0) `
        -Detail ("pre.mermaid {0} 件 / pre>code.language-mermaid {1} 件" -f $light.preCount, $light.codeCount)) -and $allOk

    $allOk = (Assert-Ok -Label '図が期待数どおり描かれている' `
        -Condition ([int]$light.svgCount -eq $expected) `
        -Detail ("svg {0} 件 / 期待 {1} 件 / error {2} 件" -f $light.svgCount, $expected, $light.errorCount)) -and $allOk

    $allOk = (Assert-Ok -Label 'svg の id が viewer.js 採番（mdv-mermaid-N）' `
        -Condition ([string]$light.svgId -like 'mdv-mermaid-*') `
        -Detail ("id={0}" -f $light.svgId)) -and $allOk

    $allOk = (Assert-Ok -Label 'click 記法の <a> が生成されている（securityLevel: antiscript）' `
        -Condition ([int]$light.anchorCount -ge 4) `
        -Detail ("a {0} 件 / 期待 4 件以上" -f $light.anchorCount)) -and $allOk

    # href が本数だけでなく中身も保たれていること（mermaid 側の書き換え・空文字化を弾く）。
    # 同一文書 #anchor / 相対 .md / サブフォルダの .md / 外部 URL の 4 経路が
    # onArticleAnchorClick と C# 側 OnNavigationStarting の振り分け対象。
    $hrefs = @($light.hrefs | ForEach-Object { "$_" })
    foreach ($expectedHref in @('#usage', 'sample.md', 'sub/page2.md', 'https://example.com')) {
        $allOk = (Assert-Ok -Label ("click 記法の href が保たれている: {0}" -f $expectedHref) `
            -Condition ($hrefs -contains $expectedHref) `
            -Detail ("実測: {0}" -f ($hrefs -join ' | '))) -and $allOk
    }
    $allOk = (Assert-Ok -Label 'javascript: の href が無い（antiscript の無害化）' `
        -Condition ([int]$light.jsHrefCount -eq 0) `
        -Detail ("{0} 件" -f $light.jsHrefCount)) -and $allOk

    $allOk = (Assert-Ok -Label 'data-src-line が div.mermaid へ引き継がれている' `
        -Condition ([int]$light.srcLineCount -eq $expected) `
        -Detail ("{0} 件" -f $light.srcLineCount)) -and $allOk

    # ダークへ切替。svg が作り直され、図形の fill が変わること。
    Set-ViewerTheme -Session $Session -Theme 'dark' | Out-Null
    $dark = Wait-MermaidRendered -Session $Session -ExpectedDiagrams $expected -PreviousSvgId $light.svgId

    $allOk = (Assert-Ok -Label 'ダーク切替で svg が作り直される（id が変わる）' `
        -Condition ($dark.svgId -ne $light.svgId) `
        -Detail ("{0} -> {1}" -f $light.svgId, $dark.svgId)) -and $allOk

    $allOk = (Assert-Ok -Label 'ダーク切替で図形の fill が変わる' `
        -Condition ($null -ne $dark.fill -and $dark.fill -ne $light.fill) `
        -Detail ("light={0} dark={1}" -f $light.fill, $dark.fill)) -and $allOk

    $allOk = (Assert-Ok -Label 'ダーク再描画後も click 記法の <a> と href が残る' `
        -Condition ([int]$dark.anchorCount -ge 4 -and (@($dark.hrefs | ForEach-Object { "$_" }) -contains '#usage')) `
        -Detail ("a {0} 件 / {1}" -f $dark.anchorCount, (@($dark.hrefs | ForEach-Object { "$_" }) -join ' | '))) -and $allOk

    # ライトへ戻す。fill が最初の値に戻ること（片道だけ効く実装を弾く）。
    Set-ViewerTheme -Session $Session -Theme 'light' | Out-Null
    $back = Wait-MermaidRendered -Session $Session -ExpectedDiagrams $expected -PreviousSvgId $dark.svgId

    $allOk = (Assert-Ok -Label 'ライトへ戻すと fill が元の値に戻る' `
        -Condition ($back.fill -eq $light.fill) `
        -Detail ("light={0} back={1}" -f $light.fill, $back.fill)) -and $allOk

    return $allOk
}
