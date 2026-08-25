#requires -Version 5.1
<#
.SYNOPSIS
    キャンバスのページから判定材料を取り出す（ISSUE #94）。

.DESCRIPTION
    判定の根拠はすべてページ内の実測値にする。ここに置くのは
    「何を測るか」だけで、合否は cases/*.ps1 が決める。

    canvas.js が公開している契約だけを使う:
      #cv-stage / g.cv-node / .cv-node-label / .cv-card / window.__cvFitToContent /
      window.__cvSetView
    ズーム率は d3 が要素へ書く __zoom（d3.zoomTransform）から読む。製品コードに
    テスト用の口を足さないため。
#>

Set-StrictMode -Version Latest

# 非表示タブでは setTimeout がスロットリングされる。デバウンス 300ms を見込んで
# 十分に待つ（既知の罠）。評価対象は可視タブだが、待ちの根拠はここに集約する。
$script:CanvasSettleMs = 2500

function Invoke-CanvasFit {
    <#
    .SYNOPSIS
        全体俯瞰へ寄せる。力学モデルの収束を待ってからもう一度当てる。
    .DESCRIPTION
        fitToContent() は座標が未確定なら何もせず、収束後に再試行される作り。
        1 回目で効かなかった場合に備えて待ってから 2 回目を送る。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [int]$SettleMs = $script:CanvasSettleMs
    )

    Invoke-CdpEvaluate -Session $Session -Expression 'window.__cvFitToContent(); true' | Out-Null
    Start-Sleep -Milliseconds $SettleMs
    Invoke-CdpEvaluate -Session $Session -Expression 'window.__cvFitToContent(); true' | Out-Null
    Start-Sleep -Milliseconds 500
}

function Get-CanvasFitMeasurement {
    <#
    .SYNOPSIS
        全ノードの矩形と #cv-stage の矩形を比べ、最大はみ出し量を返す。
    .OUTPUTS
        counted / worstOver / worstSide / worstLabel / k / stageW / stageH
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Session)

    $js = @'
(function () {
  var stage = document.getElementById('cv-stage');
  if (!stage) return { counted: 0, worstOver: null };
  var r = stage.getBoundingClientRect();
  var nodes = document.querySelectorAll('g.cv-node');
  var counted = 0;
  var worstOver = -99999;
  var worstSide = null;
  var worstLabel = null;
  for (var i = 0; i < nodes.length; i++) {
    var b = nodes[i].getBoundingClientRect();
    if (b.width === 0 && b.height === 0) continue; // 未配置。収束待ちで拾う
    counted++;
    var sides = [
      ['left',   r.left - b.left],
      ['top',    r.top - b.top],
      ['right',  b.right - r.right],
      ['bottom', b.bottom - r.bottom]
    ];
    for (var j = 0; j < sides.length; j++) {
      if (sides[j][1] > worstOver) {
        worstOver = sides[j][1];
        worstSide = sides[j][0];
        var label = nodes[i].querySelector('.cv-node-label');
        worstLabel = label ? label.textContent : '';
      }
    }
  }
  var k = null;
  try {
    if (window.d3 && d3.zoomTransform) { k = d3.zoomTransform(stage).k; }
    else if (stage.__zoom) { k = stage.__zoom.k; }
  } catch (e) { k = null; }
  return {
    counted: counted,
    worstOver: worstOver,
    worstSide: worstSide,
    worstLabel: worstLabel,
    k: k,
    stageW: r.width,
    stageH: r.height
  };
})()
'@

    return Invoke-CdpEvaluate -Session $Session -Expression $js -TimeoutSec 30
}

function Test-CanvasContained {
    <#
    .SYNOPSIS
        測定結果から「全ノードが収まっているか」と、人が読める理由を作る。
    .DESCRIPTION
        測れなかった場合（ノード 0 件・#cv-stage 不在）を「収まっている」と
        取り違えないよう、ここで明示的に不合格へ倒す。
    .OUTPUTS
        Fits / Detail
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]$Measurement,
        [double]$TolerancePx = 0.5
    )

    if ($null -eq $Measurement -or
        $null -eq $Measurement.worstOver -or
        [int]$Measurement.counted -le 0) {
        return [pscustomobject]@{ Fits = $false; Detail = '矩形を測れない（ノード 0 件）' }
    }

    $over = [double]$Measurement.worstOver
    if ($over -le $TolerancePx) {
        return [pscustomobject]@{
            Fits   = $true
            Detail = "最大はみ出し {0:F1}px" -f ([Math]::Max($over, 0))
        }
    }
    return [pscustomobject]@{
        Fits   = $false
        Detail = "'{0}' が {1} に {2:F1}px はみ出し" -f $Measurement.worstLabel, $Measurement.worstSide, $over
    }
}

function Set-CanvasScale {
    <#
    .SYNOPSIS
        画面中心を保ったままズーム率だけを変える（カードモードへ入るのに使う）。
    .DESCRIPTION
        __cvSetView は製品がワークスペース復元で使っている口。テスト専用の
        入口を製品側へ足さずに済む。
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)][double]$Scale
    )

    # 数値は必ず不変カルチャで埋め込む。小数点がカンマになるロケールでは
    # そのまま JS の構文エラーになる。
    $scaleText = $Scale.ToString([System.Globalization.CultureInfo]::InvariantCulture)

    $js = @"
(function () {
  var stage = document.getElementById('cv-stage');
  var t = (window.d3 && d3.zoomTransform) ? d3.zoomTransform(stage) : stage.__zoom;
  if (!t) return false;
  var cx = window.innerWidth / 2;
  var cy = window.innerHeight / 2;
  var wx = (cx - t.x) / t.k;
  var wy = (cy - t.y) / t.k;
  var k2 = $scaleText;
  window.__cvSetView(cx - k2 * wx, cy - k2 * wy, k2);
  return true;
})()
"@

    $ok = Invoke-CdpEvaluate -Session $Session -Expression $js -TimeoutSec 20
    Start-Sleep -Milliseconds 800
    return ($ok -eq $true)
}

function Measure-CardStacking {
    <#
    .SYNOPSIS
        重なっているカード 2 枚を選び、ホバー / クリック固定での z-index を実測する。
    .DESCRIPTION
        canvas.js は mouseenter で hoverId、pointerdown（主ボタン）で frontId を
        更新し、applyStacking() が z-index を書く。ここではその 2 経路を
        合成イベントで叩いて、重なり順が入れ替わることを実測する。

        探索と実測を 1 回の評価に閉じているのは、間に力学モデルのティックが挟まると
        カードが作り直され（refreshCards の仮想化）、選んだ 2 枚が消え得るため。

        d3.drag が拾うのは mousedown / touchstart なので、pointerdown の合成で
        ドラッグが始まって座標が壊れることはない。
    .OUTPUTS
        found / cardCount /（found のとき）a / b / overlapX / overlapY / base / hover / front
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Session,
        [double]$MinOverlapPx = 8
    )

    # 数値は必ず不変カルチャで埋め込む（小数点がカンマになるロケール対策）。
    $minText = $MinOverlapPx.ToString([System.Globalization.CultureInfo]::InvariantCulture)

    $js = @"
(function () {
  var cards = Array.prototype.slice.call(document.querySelectorAll('.cv-card'));
  var rects = cards.map(function (c) { return c.getBoundingClientRect(); });
  var min = $minText;
  var a = null, b = null, ox = 0, oy = 0;

  for (var i = 0; i < cards.length && !a; i++) {
    for (var j = i + 1; j < cards.length; j++) {
      var ra = rects[i], rb = rects[j];
      var dx = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
      var dy = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
      if (dx > min && dy > min) {
        a = cards[i]; b = cards[j]; ox = dx; oy = dy;
        break;
      }
    }
  }
  if (!a || !b) return { found: false, cardCount: cards.length };

  function z(el) { var v = Number(getComputedStyle(el).zIndex); return isFinite(v) ? v : 0; }

  var base = { a: z(a), b: z(b) };
  b.dispatchEvent(new MouseEvent('mouseenter'));
  var hover = { a: z(a), b: z(b) };
  a.dispatchEvent(new MouseEvent('pointerdown', { button: 0, bubbles: false }));
  var front = { a: z(a), b: z(b) };
  b.dispatchEvent(new MouseEvent('mouseleave'));

  return {
    found: true, cardCount: cards.length,
    a: a.title, b: b.title, overlapX: ox, overlapY: oy,
    base: base, hover: hover, front: front
  };
})()
"@

    return Invoke-CdpEvaluate -Session $Session -Expression $js -TimeoutSec 30
}
