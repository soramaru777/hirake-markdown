/*
 * Hirake - canvas.js
 *
 * 無限キャンバス・モード（ADR-0001 案B: 単一 WebView2 の HTML キャンバス方式）。
 * d3-zoom / d3-force / ピン留めを基礎に、セマンティックズームを提供する:
 *   - 遠景（k < CARD_THRESHOLD）: ノード + エッジ（旧グラフビュー相当・Ctrl+G の着地点）
 *   - 中間（k >= CARD_THRESHOLD）: カード（ファイル名 + サムネイル）。エッジは薄く継続
 *   - 近景（k >= PREVIEW_THRESHOLD）: 中央に近い最大 MAX_PREVIEWS 枚が実文書の
 *     インラインプレビュー（sandbox iframe）。遠ざかったら即破棄しカードへ戻す
 *
 * データ（ホストが canvas-template.html の {{DATA}} に埋め込む）:
 *   window.__canvasData = {
 *     root, folderLabel, truncated, overview,
 *     nodes: [{ id(絶対パス), rel(相対パス・保存キー), pid(不透明 ID), label, degree,
 *               thumb(ファイル名?クエリ or null), x?, y?, pinned }],
 *     edges: [{ source, target }],
 *     view: { x, y, k } | null
 *   }
 *
 * ホスト（WPF）との契約:
 *   WebView→ホスト postMessage:
 *     { type:'openFile', path:<string> }                ダブルクリックで通常タブへ
 *     { type:'canvasLayout', layout:{ Version, Files:{rel:{X,Y,Pinned}}, View:{X,Y,K} } }
 *                                                       配置・ビューポートの保存（デバウンス）
 *     { type:'canvasPreview', pid:<string>, theme:'light'|'dark', token:<string> }
 *                                                       近景プレビュー HTML の要求
 *                                                       （token は応答照合用。ホストは解釈せず返すだけ）
 *     { type:'shortcut', action:... }                   viewer.js と同じショートカット転送
 *   ホスト→WebView 公開関数:
 *     window.__mdvSetTheme('light'|'dark')  テーマ切替（配色は CSS 変数追従）
 *     window.__cvSetPreview(pid, url, token) 要求したプレビュー HTML の URL 通知
 *     window.__cvFitToContent()             全体俯瞰（遠景）へズームアウト（Ctrl+G）
 *     window.__cvSetView(x, y, k)           ビューポートの適用（ワークスペース復元）
 *
 * 信頼境界:
 *   iframe の src はホストが払い出す previews.hirake の不透明ファイル名のみで、
 *   実ファイルパスは JS 側に存在しない。iframe は allow-scripts なしの sandbox で
 *   埋め込むため、プレビュー内のスクリプトは実行されない（viewer.js も動かない）。
 *
 * DOM 仮想化（ADR-0001 成功条件 2）:
 *   カード DOM はビューポート内（+マージン）のノード分だけ生成し、
 *   画面外へ出たカードは破棄する。遠景のノード円は SVG で軽量なため全件描画。
 */
(function () {
  'use strict';

  function postToHost(message) {
    try {
      if (window.chrome && window.chrome.webview &&
          typeof window.chrome.webview.postMessage === 'function') {
        window.chrome.webview.postMessage(message);
      }
    } catch (err) {
      // ホスト未接続（単体ブラウザでのテスト等）は無視する。
    }
  }

  function safeRun(fn) {
    try {
      fn();
    } catch (err) {
      if (window.console && console.error) {
        console.error('[Hirake]', err);
      }
    }
  }

  // カード表示へ切り替えるズーム率と、カードの世界座標サイズ。
  // 既定ズーム（k=1）でカード表示になるよう 1 未満にする（開いた直後から
  // カードが見え、ズームアウトでグラフ俯瞰へ切り替わる）。
  var CARD_THRESHOLD = 0.75;
  var CARD_W = 180;
  var CARD_H = 130;
  var VIEW_MARGIN = 200; // カード仮想化のビューポートマージン（世界座標）

  // 近景（インラインプレビュー）へ切り替えるズーム率と、プレビューカードのサイズ。
  // iframe は重いので枚数を厳しく絞り、ビューポート中心に近いものだけを生かす。
  var PREVIEW_THRESHOLD = 2.5;
  var MAX_PREVIEWS = 3;
  var PREVIEW_W = 420;
  var PREVIEW_H = 320;

  // カードの重なり順。#cv-cards は transform を持つため独立した stacking context を
  // 作る。ここの値は その内側でしか効かないので、情報バー（#cv-info, z-index:10）は
  // 常にカードより前のまま。
  var Z_CARD = 1;          // 通常カード
  var Z_PREVIEW_BASE = 10; // プレビュー（ビューポート中心に近いほど上に積む）
  var Z_HOVER = 100;       // ポインタが乗っているカード
  var Z_FRONT = 200;       // クリックで最前面に固定したカード

  // 全体俯瞰（fitToContent）の余白とラベルの寸法。
  // 余白は画面 px で持つ（ワールド単位だと k に比例して痩せ、ズームアウトするほど
  // 実質の余白が無くなる）。ラベル分はノード描画（`y = nodeRadius(d) + 14`）に合わせる。
  var FIT_PAD = 24;      // 画面 px。k に依存しない余白
  var FIT_GAP = 8;       // 画面 px。オーバーレイ（#cv-info / #cv-hint）との間隔
  var OVERLAY_INSET = 12; // 画面 px。オーバーレイの top / bottom（canvas-template.html）
  var LABEL_OFFSET = 14;  // ワールド単位。ラベルの baseline オフセットと一致させること
  var LABEL_DESCENT = 4;  // ワールド単位。11px フォントのディセンダ概算

  // テーマ切替時にキャンバス本体へ通知するフック（buildCanvas が差し込む）。
  var onThemeChanged = null;

  /* ---------- ラベル幅の計測（オフスクリーン canvas） ----------
   * DOM に挿入しないので、レイアウトを起こさず、body.mode-card で
   * .cv-node-label が display:none でも計測できる（getBBox は使えない）。 */

  var labelMeasureCtx = null;  // CanvasRenderingContext2D | null（null = 計測不可）
  var labelMeasureFont = null; // string。解決済みの font 文字列

  // .cv-node-label の実効フォントを canvas の font 文字列にする。
  // ハードコードするとテーマやフォント設定の変更で静かにずれるため CSS から引く。
  function resolveLabelFont() {
    if (labelMeasureFont !== null) return labelMeasureFont;
    var font = '';
    var svg = document.getElementById('cv-svg');
    var probe = null;
    try {
      if (svg) {
        probe = document.createElementNS('http://www.w3.org/2000/svg', 'text');
        probe.setAttribute('class', 'cv-node-label');
        // 文字は入れない（描画されない）。display:none の要素でも
        // font-size / font-family の計算値は取れるので mode-card でも計測できる。
        svg.appendChild(probe);
        var cs = window.getComputedStyle(probe);
        var size = cs.fontSize || '';
        var family = cs.fontFamily || '';
        var weight = cs.fontWeight || '';
        if (size && family) {
          font = (weight && weight !== '400' && weight !== 'normal' ? weight + ' ' : '') +
            size + ' ' + family;
        }
      }
    } catch (err) {
      font = '';
    } finally {
      // 例外が出ても計測用の要素を DOM に残さない。
      if (probe && probe.parentNode) probe.parentNode.removeChild(probe);
    }
    labelMeasureFont = font || '11px sans-serif';
    return labelMeasureFont;
  }

  // ラベル 1 件の描画幅（ワールド単位）。SVG の font-size はズーム変換の
  // 内側なので、11px はそのままワールド単位の 11 に相当する。
  // 計測できない場合は 0 を返す（呼び出し側は半径だけで矩形を作る）。
  function measureLabelWidth(text) {
    if (typeof text !== 'string' || text === '') return 0;
    if (labelMeasureCtx === null) {
      try {
        var el = document.createElement('canvas');
        labelMeasureCtx = el.getContext ? el.getContext('2d') : null;
      } catch (err) {
        labelMeasureCtx = null;
      }
      if (!labelMeasureCtx) {
        labelMeasureCtx = false; // 以後は再試行しない
        return 0;
      }
      labelMeasureCtx.font = resolveLabelFont();
    }
    if (!labelMeasureCtx) return 0;
    var w;
    try {
      w = labelMeasureCtx.measureText(text).width;
    } catch (err) {
      return 0;
    }
    if (typeof w !== 'number' || !isFinite(w) || w < 0) return 0;
    return w;
  }

  function currentThemeName() {
    return document.documentElement.getAttribute('data-theme') === 'dark' ? 'dark' : 'light';
  }

  /* ==========================================================
   * 情報バー・テーマ
   * ========================================================== */

  function updateInfoBar(data, nodeCount, edgeCount) {
    var folderEl = document.querySelector('#cv-info .cv-info-folder');
    var countsEl = document.querySelector('#cv-info .cv-info-counts');
    var noteEl = document.querySelector('#cv-info .cv-info-note');
    if (folderEl) folderEl.textContent = data.folderLabel || data.root || '';
    if (countsEl) {
      countsEl.textContent = ' — ' + nodeCount + ' ファイル / ' + edgeCount + ' リンク';
    }
    if (noteEl && data.truncated) {
      noteEl.textContent = '（上限到達のため一部未走査）';
    }
  }

  function setTheme(theme) {
    document.documentElement.setAttribute(
      'data-theme', theme === 'dark' ? 'dark' : 'light');
    // プレビュー HTML はテーマ別に生成されるため、切替後は作り直す。
    if (typeof onThemeChanged === 'function') {
      safeRun(onThemeChanged);
    }
  }

  window.__mdvSetTheme = setTheme;

  /* ==========================================================
   * キャンバス本体
   * ========================================================== */

  function zoomFilter(event) {
    if (event.button) return false;
    if (event.ctrlKey && event.type !== 'wheel') return false;
    var target = event.target;
    if (target && target.closest &&
        (target.closest('.cv-node') || target.closest('.cv-card'))) {
      return false;
    }
    return true;
  }

  function buildCanvas(data) {
    var nodes = data.nodes.slice();
    var edges = data.edges.slice();

    updateInfoBar(data, nodes.length, edges.length);

    if (nodes.length === 0) {
      document.getElementById('cv-empty').classList.add('visible');
      return;
    }

    var byId = Object.create(null);
    nodes.forEach(function (n) { byId[n.id] = n; });

    edges.forEach(function (e) {
      e._srcId = e.source;
      e._tgtId = e.target;
    });

    var maxDegree = d3.max(nodes, function (d) {
      return (typeof d.degree === 'number' && isFinite(d.degree)) ? d.degree : 0;
    }) || 0;
    var radiusScale = maxDegree > 0
      ? d3.scaleSqrt().domain([0, maxDegree]).range([6, 18]).clamp(true)
      : function () { return 8; };

    function nodeRadius(d) {
      return radiusScale((typeof d.degree === 'number' && isFinite(d.degree)) ? d.degree : 0);
    }

    var width = window.innerWidth;
    var height = window.innerHeight;

    var stageSel = d3.select('#cv-stage');
    var svgSel = d3.select('#cv-svg');
    var zoomLayer = svgSel.append('g').attr('class', 'cv-zoom-layer');
    var edgesLayer = zoomLayer.append('g');
    var nodesLayer = zoomLayer.append('g');
    var cardsEl = document.getElementById('cv-cards');

    // 保存済み配置の適用: ピン済みは fx/fy で固定（R4）、位置だけあるものは初期値に。
    nodes.forEach(function (n) {
      if (typeof n.x === 'number' && typeof n.y === 'number') {
        if (n.pinned) {
          n.fx = n.x;
          n.fy = n.y;
        }
      } else {
        n.x = undefined;
        n.y = undefined;
      }
    });

    /* ---------- ズーム（カメラ） ---------- */

    var currentTransform = d3.zoomIdentity;

    var zoomBehavior = d3.zoom()
      .scaleExtent([0.1, 4])
      .filter(zoomFilter)
      .on('zoom', function (event) {
        currentTransform = event.transform;
        applyTransform();
        updateMode();
        scheduleCardRefresh();
        scheduleSave();
      });
    stageSel.call(zoomBehavior).on('dblclick.zoom', null);

    function applyTransform() {
      var t = currentTransform;
      zoomLayer.attr('transform', t);
      cardsEl.style.transform =
        'translate(' + t.x + 'px,' + t.y + 'px) scale(' + t.k + ')';
    }

    function updateMode() {
      var k = currentTransform.k;
      var card = k >= CARD_THRESHOLD;
      document.body.classList.toggle('mode-card', card);
      document.body.classList.toggle('mode-preview', k >= PREVIEW_THRESHOLD);
      if (card) {
        refreshCards();
      } else {
        // カードが display:none になると mouseleave が来ないことがある。
        // ホバーを持ち越すと、カードモードへ戻った直後に前回のカードが
        // 最前面のまま復帰する。固定（frontId）は仕様として持ち越す。
        hoverId = null;
      }
      // 近景を抜けた場合の破棄もここで行うため、カードモード外でも必ず呼ぶ
      // （refreshPreviews の末尾で applyStacking も走る）。
      refreshPreviews();
    }

    /* ---------- 力学モデル（R4: 未ピンのみ作用） ---------- */

    var simulation = d3.forceSimulation(nodes)
      .force('link', d3.forceLink(edges).id(function (d) { return d.id; }).distance(120))
      .force('charge', d3.forceManyBody().strength(-300))
      .force('center', d3.forceCenter(width / 2, height / 2))
      .force('collide', d3.forceCollide(function (d) { return nodeRadius(d) + 8; }));

    // 保存済み配置が十分あるときは初期の揺れを抑える。
    var pinnedCount = nodes.filter(function (n) { return n.pinned; }).length;
    if (pinnedCount > 0 && pinnedCount >= nodes.length / 2) {
      simulation.alpha(0.15);
    }

    /* ---------- 遠景: エッジ + ノード ---------- */

    var edgeSel = edgesLayer.selectAll('line.cv-edge')
      .data(edges)
      .join('line')
      .attr('class', 'cv-edge');

    var dragBehavior = d3.drag()
      .clickDistance(4)
      .on('start', dragStarted)
      .on('drag', dragged)
      .on('end', dragEnded);

    var nodeSel = nodesLayer.selectAll('g.cv-node')
      .data(nodes, function (d) { return d.id; })
      .join('g')
      .attr('class', 'cv-node')
      .call(dragBehavior)
      .on('dblclick', nodeDblClicked)
      .on('contextmenu', nodeContextMenu)
      .on('mouseenter', nodeMouseEnter)
      .on('mouseleave', nodeMouseLeave);

    nodeSel.append('circle')
      .attr('class', 'cv-node-circle')
      .classed('is-pinned', function (d) { return !!d.pinned; })
      .attr('r', nodeRadius);

    nodeSel.append('text')
      .attr('class', 'cv-node-label')
      .attr('y', function (d) { return nodeRadius(d) + 14; })
      .text(function (d) { return d.label || d.id; });

    /* ---------- 中間: カード（DOM 仮想化） ---------- */

    var cardById = Object.create(null); // id -> DOM 要素
    var cardRefreshTimer = null;

    function scheduleCardRefresh() {
      if (cardRefreshTimer !== null) return;
      cardRefreshTimer = setTimeout(function () {
        cardRefreshTimer = null;
        if (document.body.classList.contains('mode-card')) {
          refreshCards();
        }
      }, 80);
    }

    // 現在のビューポートの世界座標範囲を計算する。
    function worldViewport() {
      var t = currentTransform;
      return {
        x0: (0 - t.x) / t.k - VIEW_MARGIN,
        y0: (0 - t.y) / t.k - VIEW_MARGIN,
        x1: (window.innerWidth - t.x) / t.k + VIEW_MARGIN,
        y1: (window.innerHeight - t.y) / t.k + VIEW_MARGIN,
      };
    }

    // ビューポート内のノードだけカード DOM を生成し、画面外は破棄する。
    function refreshCards() {
      if (!cardById) return; // 初期化順の保険
      var vp = worldViewport();
      var visible = Object.create(null);

      nodes.forEach(function (n) {
        if (typeof n.x !== 'number' || typeof n.y !== 'number') return;
        if (n.x < vp.x0 || n.x > vp.x1 || n.y < vp.y0 || n.y > vp.y1) return;
        visible[n.id] = true;
        var card = cardById[n.id];
        if (!card) {
          card = createCard(n);
          cardById[n.id] = card;
          cardsEl.appendChild(card);
        }
        positionCard(card, n);
      });

      Object.keys(cardById).forEach(function (id) {
        if (!visible[id]) {
          detachPreview(id);
          forgetStacking(id); // 消えたカードを hoverId / frontId が指し続けないようにする
          var el = cardById[id];
          if (el && el.parentNode) el.parentNode.removeChild(el);
          delete cardById[id];
        }
      });

      // カードは仮想化で作り直されるため、近景の割り当ても都度貼り直す。
      refreshPreviews();
    }

    /* ---------- 重なり順（カードは DOM 挿入順では前後が決まらない） ----------
     * 座標は動かさず「どれを手前に出すか」だけを制御する。z-index を書くのは
     * applyStacking() だけに保つ（CSS 側には z-index を置かない。ランクを
     * インライン style で与える以上、CSS の :hover 指定は負けて効かないため）。 */

    var previewRank = Object.create(null); // id -> 0..MAX_PREVIEWS-1（0 = 中心に最も近い）
    var hoverId = null;                    // ポインタが乗っているカード
    var frontId = null;                    // クリックで固定した最前面

    function applyStacking() {
      Object.keys(cardById).forEach(function (id) {
        var card = cardById[id];
        if (!card) return;
        var z = Z_CARD;
        if (card.classList.contains('is-preview')) {
          var rank = previewRank[id];
          if (typeof rank !== 'number' || !isFinite(rank)) rank = MAX_PREVIEWS - 1;
          z = Z_PREVIEW_BASE + (MAX_PREVIEWS - 1 - rank);
        }
        if (id === hoverId) z = Z_HOVER;
        if (id === frontId) z = Z_FRONT; // 固定はホバーより強い
        card.style.zIndex = String(z);
      });
    }

    function forgetStacking(id) {
      delete previewRank[id];
      if (hoverId === id) hoverId = null;
      if (frontId === id) frontId = null;
    }

    function createCard(n) {
      var card = document.createElement('div');
      card.className = 'cv-card' + (n.pinned ? ' is-pinned' : '');
      card.title = n.rel;

      if (n.thumb) {
        var img = document.createElement('img');
        img.className = 'cv-card-thumb';
        img.loading = 'lazy';
        img.draggable = false;
        img.src = 'https://thumbs.hirake/' + n.thumb;
        card.appendChild(img);
      } else {
        var empty = document.createElement('div');
        empty.className = 'cv-card-thumb-empty';
        empty.textContent = '📄';
        card.appendChild(empty);
      }

      var name = document.createElement('div');
      name.className = 'cv-card-name';
      name.textContent = n.label;
      card.appendChild(name);

      // 重なり順。ポインタが乗っている間は手前へ、クリックすると外しても手前のまま。
      // iframe はカードの子孫なので、本文へポインタを移しても mouseleave は起きない。
      card.addEventListener('mouseenter', function () {
        safeRun(function () {
          hoverId = n.id;
          applyStacking();
        });
      });
      card.addEventListener('mouseleave', function () {
        safeRun(function () {
          if (hoverId === n.id) hoverId = null;
          applyStacking();
        });
      });
      card.addEventListener('pointerdown', function (event) {
        safeRun(function () {
          // 主ボタンだけ。右クリックはピン解除（contextmenu）なので固定しない。
          // d3.drag の既定 filter も非主ボタンを除外しており、そこに揃える。
          if (event && typeof event.button === 'number' && event.button !== 0) return;
          frontId = n.id;
          applyStacking();
        });
      });

      card.addEventListener('dblclick', function (event) {
        event.stopPropagation();
        postToHost({ type: 'openFile', path: n.id });
      });
      card.addEventListener('contextmenu', function (event) {
        event.preventDefault();
        event.stopPropagation();
        unpinNode(n);
      });

      // カードのドラッグ。HTML レイヤーは CSS transform でズームしているため
      // d3 の event.x/y は world 座標にならない。clientX/Y から currentTransform の
      // 逆変換で world 座標を求める（SVG ノード側は d3 が CTM を解決するので不要）。
      var dragOffset = null;
      function toWorld(sourceEvent) {
        return {
          x: (sourceEvent.clientX - currentTransform.x) / currentTransform.k,
          y: (sourceEvent.clientY - currentTransform.y) / currentTransform.k,
        };
      }
      d3.select(card).call(
        d3.drag()
          .clickDistance(4)
          .on('start', function (event) {
            stageSel.classed('dragging', true);
            if (!event.active) simulation.alphaTarget(0.3).restart();
            var w = toWorld(event.sourceEvent);
            n.fx = n.x;
            n.fy = n.y;
            dragOffset = { dx: n.x - w.x, dy: n.y - w.y };
          })
          .on('drag', function (event) {
            var w = toWorld(event.sourceEvent);
            n.fx = w.x + (dragOffset ? dragOffset.dx : 0);
            n.fy = w.y + (dragOffset ? dragOffset.dy : 0);
          })
          .on('end', function (event) { dragEnded(event, n); }));

      return card;
    }

    function positionCard(card, n) {
      var isPreview = card.classList.contains('is-preview');
      var w = isPreview ? PREVIEW_W : CARD_W;
      var h = isPreview ? PREVIEW_H : CARD_H;
      card.style.left = (n.x - w / 2) + 'px';
      card.style.top = (n.y - h / 2) + 'px';
    }

    /* ---------- 近景: インラインプレビュー（sandbox iframe） ---------- */

    // pid -> プレビュー HTML の URL（ホストが払い出した不透明 URL）。
    var previewUrlByPid = Object.create(null);
    // pid|token -> 要求済み（二重要求の抑止）。
    var previewRequested = Object.create(null);
    // pid -> 読み込み失敗後に再試行済み（無限反復の抑止。テーマ切替でリセット）。
    var previewRetried = Object.create(null);

    // テーマ切替のたびに増える世代番号。応答の照合に使う。
    // テーマ名だけで照合すると light→dark→light と素早く切り替えたとき、
    // 最初の light の応答が現世代として誤って受理されるため、世代まで見る。
    var previewGeneration = 0;

    function previewToken() {
      return currentThemeName() + '#' + previewGeneration;
    }

    function requestPreview(n) {
      if (!n.pid) return;
      var key = n.pid + '|' + previewToken();
      if (previewRequested[key]) return;
      previewRequested[key] = true;
      postToHost({
        type: 'canvasPreview',
        pid: n.pid,
        theme: currentThemeName(),
        token: previewToken(),
      });
    }

    // 取得済み URL と要求済みフラグを捨てて、再取得できる状態に戻す。
    function forgetPreview(pid) {
      if (!pid) return;
      delete previewUrlByPid[pid];
      var suffix = '|' + previewToken();
      Object.keys(previewRequested).forEach(function (key) {
        if (key === pid + suffix) {
          delete previewRequested[key];
        }
      });
    }

    // カードへ iframe を貼る。URL 未取得ならホストへ要求だけ出して戻る
    // （到着時に __cvSetPreview → refreshPreviews で貼り直される）。
    function attachPreview(id, n) {
      var card = cardById[id];
      if (!card || !n.pid) return;

      var url = previewUrlByPid[n.pid];
      if (!url) {
        requestPreview(n);
        return;
      }
      if (card.__cvPreviewUrl === url) return; // 既に同じ内容を表示中。

      detachPreview(id);

      var frame = document.createElement('iframe');
      frame.className = 'cv-card-preview';
      // allow-scripts を付けない = プレビュー内のスクリプトは実行されない。
      // viewer.js が動かないため、プレビューから postMessage が飛ぶこともない。
      frame.setAttribute('sandbox', '');
      frame.setAttribute('scrolling', 'auto');
      frame.setAttribute('tabindex', '-1');
      frame.setAttribute('title', n.label || '');

      // 読み込みに失敗した場合は URL と要求済みフラグを捨てて作り直す。
      // 恒常的な失敗で再取得が無限反復しないよう、1 世代につき 1 回だけ再試行する。
      // （HTTP 404 は error ではなく load になるため、これは取りこぼしのない救済では
      //  ない。掃除との競合そのものは CleanupPreviews の猶予時間で防いでいる。）
      frame.addEventListener('error', function () {
        safeRun(function () {
          if (previewRetried[n.pid]) return;
          previewRetried[n.pid] = true;
          forgetPreview(n.pid);
          detachPreview(id);
          refreshPreviews();
        });
      });

      frame.src = url;

      card.insertBefore(frame, card.firstChild);
      card.classList.add('is-preview');
      card.__cvPreviewUrl = url;
      positionCard(card, n);
    }

    // iframe を破棄してカード表示へ戻す（DOM に残さない）。
    function detachPreview(id) {
      var card = cardById[id];
      if (!card) return;
      var frame = card.querySelector('.cv-card-preview');
      if (frame && frame.parentNode) {
        frame.parentNode.removeChild(frame);
      }
      if (card.classList.contains('is-preview')) {
        card.classList.remove('is-preview');
        var n = byId[id];
        if (n) positionCard(card, n);
      }
      card.__cvPreviewUrl = null;
    }

    // 近景対象（ビューポート中心に近い最大 MAX_PREVIEWS 枚）を選び直す。
    function refreshPreviews() {
      if (!document.body.classList.contains('mode-preview')) {
        Object.keys(cardById).forEach(detachPreview);
        previewRank = Object.create(null); // 近景を抜けたらランクを残さない
        applyStacking();
        return;
      }

      var t = currentTransform;
      var cx = (window.innerWidth / 2 - t.x) / t.k;
      var cy = (window.innerHeight / 2 - t.y) / t.k;

      var candidates = [];
      Object.keys(cardById).forEach(function (id) {
        var n = byId[id];
        if (!n || typeof n.x !== 'number' || typeof n.y !== 'number') return;
        var dx = n.x - cx;
        var dy = n.y - cy;
        candidates.push({ id: id, n: n, d: dx * dx + dy * dy });
      });
      candidates.sort(function (a, b) { return a.d - b.d; });

      var adopted = candidates.slice(0, MAX_PREVIEWS);
      var keep = Object.create(null);
      adopted.forEach(function (c) {
        keep[c.id] = true;
      });

      Object.keys(cardById).forEach(function (id) {
        if (!keep[id]) detachPreview(id);
      });
      // ランクは採用した時点で必ず入れる（iframe が貼れたかどうかとは独立。
      // URL 未到着で attachPreview が途中 return しても順位は変わらない）。
      previewRank = Object.create(null);
      adopted.forEach(function (c, i) {
        previewRank[c.id] = i;
        attachPreview(c.id, c.n);
      });
      applyStacking();
    }

    // ホストからのプレビュー URL 通知。
    // 応答は非同期のため、テーマを素早く切り替えると古いテーマの応答が後着し得る。
    // 現在のテーマと一致しない応答は捨てる（逆テーマで固定されるのを防ぐ）。
    window.__cvSetPreview = function (pid, url, token) {
      safeRun(function () {
        if (typeof pid !== 'string' || typeof url !== 'string') return;
        if (typeof token === 'string' && token !== previewToken()) {
          // 破棄した世代の応答。要求済みフラグごと捨てて、現世代で取り直せるようにする。
          delete previewRequested[pid + '|' + token];
          return;
        }
        previewUrlByPid[pid] = url;
        refreshPreviews();
      });
    };

    // テーマが変わるとプレビュー HTML も別物になるため、キャッシュごと作り直す。
    onThemeChanged = function () {
      previewGeneration++;
      previewUrlByPid = Object.create(null);
      previewRequested = Object.create(null);
      previewRetried = Object.create(null);
      Object.keys(cardById).forEach(detachPreview);
      refreshPreviews();
    };

    /* ---------- tick ---------- */

    simulation.on('tick', function () {
      edgeSel
        .attr('x1', function (d) { return d.source.x; })
        .attr('y1', function (d) { return d.source.y; })
        .attr('x2', function (d) { return d.target.x; })
        .attr('y2', function (d) { return d.target.y; });

      nodeSel.attr('transform', function (d) {
        return 'translate(' + d.x + ',' + d.y + ')';
      });

      if (document.body.classList.contains('mode-card')) {
        Object.keys(cardById).forEach(function (id) {
          var n = byId[id];
          if (n) positionCard(cardById[id], n);
        });
        scheduleCardRefresh();
      }
    });

    // 力学が落ち着いたら一度保存（初期レイアウトの確定）。
    simulation.on('end', function () {
      // 座標未確定のまま要求された俯瞰は、収束後のここで適用する
      // （var 巻き上げにより pendingFit / fitToContent は定義済み）。
      if (pendingFit) {
        safeRun(function () {
          if (fitToContent()) {
            pendingFit = false;
          }
        });
      }
      scheduleSave();
      scheduleCardRefresh();
    });

    /* ---------- ドラッグ = 手動配置（終了時にピン留め・保存。R4） ---------- */

    function dragStarted(event, d) {
      stageSel.classed('dragging', true);
      if (!event.active) simulation.alphaTarget(0.3).restart();
      d.fx = d.x;
      d.fy = d.y;
    }

    function dragged(event, d) {
      d.fx = event.x;
      d.fy = event.y;
    }

    function dragEnded(event, d) {
      stageSel.classed('dragging', false);
      if (!event.active) simulation.alphaTarget(0);
      d.pinned = true;
      refreshPinnedStyle(d);
      scheduleSave();
    }

    function unpinNode(d) {
      d.fx = null;
      d.fy = null;
      d.pinned = false;
      refreshPinnedStyle(d);
      simulation.alpha(0.3).restart();
      scheduleSave();
    }

    function refreshPinnedStyle(d) {
      nodeSel.filter(function (n) { return n.id === d.id; })
        .select('.cv-node-circle')
        .classed('is-pinned', !!d.pinned);
      var card = cardById[d.id];
      if (card) {
        card.classList.toggle('is-pinned', !!d.pinned);
      }
    }

    /* ---------- 遠景ノードの操作 ---------- */

    function nodeDblClicked(event, d) {
      event.stopPropagation();
      postToHost({ type: 'openFile', path: d.id });
    }

    function nodeContextMenu(event, d) {
      event.preventDefault();
      event.stopPropagation();
      unpinNode(d);
    }

    function nodeMouseEnter(event, d) {
      d3.select(this).select('.cv-node-circle').classed('is-hover', true);
      d3.select(this).select('.cv-node-label').classed('is-hover', true);
      edgeSel.classed('is-adjacent', function (e) {
        return e._srcId === d.id || e._tgtId === d.id;
      });
    }

    function nodeMouseLeave(event, d) {
      d3.select(this).select('.cv-node-circle').classed('is-hover', false);
      d3.select(this).select('.cv-node-label').classed('is-hover', false);
      edgeSel.classed('is-adjacent', false);
    }

    /* ---------- 配置の保存（デバウンス） ---------- */

    var saveTimer = null;

    function scheduleSave() {
      if (saveTimer !== null) {
        clearTimeout(saveTimer);
      }
      saveTimer = setTimeout(saveLayout, 800);
    }

    function saveLayout() {
      saveTimer = null;
      var files = {};
      nodes.forEach(function (n) {
        if (typeof n.x !== 'number' || typeof n.y !== 'number') return;
        if (!isFinite(n.x) || !isFinite(n.y)) return; // NaN/Infinity は保存しない
        files[n.rel] = {
          X: Math.round(n.x * 100) / 100,
          Y: Math.round(n.y * 100) / 100,
          Pinned: !!n.pinned,
        };
      });
      postToHost({
        type: 'canvasLayout',
        layout: {
          Version: 1,
          Files: files,
          View: {
            X: currentTransform.x,
            Y: currentTransform.y,
            K: currentTransform.k,
          },
        },
      });
    }

    /* ---------- リサイズ ---------- */

    window.addEventListener('resize', function () {
      safeRun(function () {
        width = window.innerWidth;
        height = window.innerHeight;
        scheduleCardRefresh();
      });
    });

    /* ---------- 全体俯瞰（Ctrl+G / グラフボタンの着地点） ---------- */

    // 全ノードの描画範囲（ワールド座標）。中心座標だけでなく、円の半径と
    // ラベルの実測幅を含めた「占有矩形」を集約する。ラベルは text-anchor: middle で
    // 円の下にだけ出るため、上下で式が非対称になる。
    // 座標未確定・ノード 0 件のときは null。
    function contentBounds() {
      var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
      nodes.forEach(function (n) {
        if (typeof n.x !== 'number' || typeof n.y !== 'number') return;
        if (!isFinite(n.x) || !isFinite(n.y)) return;
        var r = nodeRadius(n);
        if (typeof r !== 'number' || !isFinite(r) || r < 0) r = 0;
        // ラベルが円より狭いときは円がはみ出すので、広い方を採る。
        var halfW = Math.max(r, measureLabelWidth(n.label || n.id) / 2);
        var left = n.x - halfW;
        var right = n.x + halfW;
        var top = n.y - r;
        var bottom = n.y + r + LABEL_OFFSET + LABEL_DESCENT;
        if (left < minX) minX = left;
        if (right > maxX) maxX = right;
        if (top < minY) minY = top;
        if (bottom > maxY) maxY = bottom;
      });
      if (!isFinite(minX) || !isFinite(minY) || !isFinite(maxX) || !isFinite(maxY)) return null;
      return { minX: minX, maxX: maxX, minY: minY, maxY: maxY };
    }

    // オーバーレイ（#cv-info は左上・#cv-hint は左下に fixed）を避けた画面矩形（CSS px）。
    // 左右はオーバーレイが画面幅を占めないため余白だけで扱う。
    // 要素が無い場合も壊れないようにする（将来テンプレートから消えても安全）。
    function overlayHeight(id) {
      var el = document.getElementById(id);
      if (!el) return 0;
      var h = el.offsetHeight;
      if (typeof h !== 'number' || !isFinite(h) || h < 0) return 0;
      // 非表示（display:none）の要素は offsetHeight が 0 になるので自然に無視される。
      return h;
    }

    function usableViewport() {
      var vw = window.innerWidth;
      var vh = window.innerHeight;
      var top = Math.max(FIT_PAD, overlayHeight('cv-info') + OVERLAY_INSET + FIT_GAP);
      var hint = overlayHeight('cv-hint');
      var bottom = Math.min(vh - FIT_PAD, vh - (hint + OVERLAY_INSET + FIT_GAP));
      return { left: FIT_PAD, top: top, right: vw - FIT_PAD, bottom: bottom };
    }

    // 全ノードが収まる遠景へズームアウトする。座標が未確定（力学モデルが
    // まだ動いていない）場合は何もしない（simulation 終了時に再試行される）。
    function fitToContent() {
      var b = contentBounds();
      if (!b) return false; // 座標未確定・ノード 0 件。再試行に委ねる。

      var vp = usableViewport();
      var availW = vp.right - vp.left;
      var availH = vp.bottom - vp.top;
      if (availW <= 0 || availH <= 0) {
        // ウィンドウが極端に小さい／オーバーレイが画面を埋めた。
        // オーバーレイの控除をやめ、余白だけで確保し直す。
        availW = window.innerWidth - FIT_PAD * 2;
        availH = window.innerHeight - FIT_PAD * 2;
        vp = {
          left: FIT_PAD,
          top: FIT_PAD,
          right: window.innerWidth - FIT_PAD,
          bottom: window.innerHeight - FIT_PAD,
        };
        if (availW <= 0 || availH <= 0) return false;
      }

      var w = Math.max(b.maxX - b.minX, 1);
      var h = Math.max(b.maxY - b.minY, 1);
      var k = Math.min(availW / w, availH / h);
      if (typeof k !== 'number' || !isFinite(k) || k <= 0) return false; // 壊れた transform は適用しない

      if (k < 0.1 && window.console && console.info) {
        // ズーム下限に当たると全体は収まらない。切り分けできるよう記録だけ残す
        // （scaleExtent は変更しない。別 ISSUE の判断材料）。
        console.info('[Hirake] fitToContent: ズーム下限 0.1 に到達（全体が収まりません）');
      }
      // 俯瞰は必ず遠景（ノード + エッジ）で見せる。ズーム下限は scaleExtent に合わせる。
      k = Math.max(0.1, Math.min(k, CARD_THRESHOLD - 0.05));

      // 画面中央ではなく「使える矩形の中央」に合わせる（オーバーレイの分だけ下/上へ寄る）。
      var tx = (vp.left + vp.right) / 2 - k * (b.minX + b.maxX) / 2;
      var ty = (vp.top + vp.bottom) / 2 - k * (b.minY + b.maxY) / 2;
      stageSel.call(zoomBehavior.transform, d3.zoomIdentity.translate(tx, ty).scale(k));
      return true;
    }

    var pendingFit = !!data.overview;

    window.__cvFitToContent = function () {
      safeRun(function () {
        if (!fitToContent()) {
          pendingFit = true; // 座標未確定。力学モデルの収束後に適用する。
        }
      });
    };

    // ワークスペース復元時に、既に開いているキャンバスへビューポートを適用する。
    // 新規に開く場合は data.view で初期化されるため、この関数は使わない。
    window.__cvSetView = function (x, y, k) {
      safeRun(function () {
        if (typeof k !== 'number' || !isFinite(k) || k <= 0) return;
        if (typeof x !== 'number' || !isFinite(x)) return;
        if (typeof y !== 'number' || !isFinite(y)) return;
        pendingFit = false;
        stageSel.call(zoomBehavior.transform, d3.zoomIdentity.translate(x, y).scale(k));
      });
    };

    // 初期モード反映。保存済みビューポートはここで復元する
    // （zoomBehavior.transform は同期的に zoom ハンドラを発火させるため、
    //  refreshCards 等が参照する状態がすべて定義された後でなければならない）。
    if (pendingFit) {
      applyTransform();
      updateMode();
      if (fitToContent()) {
        pendingFit = false;
      }
    } else if (data.view && typeof data.view.k === 'number' && data.view.k > 0) {
      stageSel.call(zoomBehavior.transform,
        d3.zoomIdentity.translate(data.view.x || 0, data.view.y || 0).scale(data.view.k));
    } else {
      applyTransform();
      updateMode();
    }
  }

  /* ==========================================================
   * ショートカット転送（graph.js と同じ契約 + 自ビュー切替）
   * ========================================================== */

  function handleGlobalKeydown(event) {
    if (!event.ctrlKey || event.altKey || event.metaKey) return;

    if (event.key === 'Tab') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: event.shiftKey ? 'prevTab' : 'nextTab' });
      return;
    }

    var key = (event.key || '').toLowerCase();

    if (event.shiftKey) {
      if (key === 'v') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'quickPaste' });
        return;
      }
      if (key === 'f') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'globalSearch' });
        return;
      }
      if (key === 'e') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'exportPdf' });
        return;
      }
      if (key === 'd') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'cycleTheme' });
        return;
      }
      if (key === 's') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'toggleStructureView' });
        return;
      }
      if (key === 'l') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'toggleLinkCheckView' });
        return;
      }
      if (key === 'r') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'toggleFingerprintView' });
        return;
      }
      if (key === 't') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'toggleStatsView' });
        return;
      }
      if (key === 'c') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'toggleCanvasView' });
        return;
      }
      // 未対応の Ctrl+Shift+* はブラウザ標準に委ねる。
      return;
    }

    if (key === 'b') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'toggleSidebar' });
      return;
    }

    if (key === 'w') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'closeTab' });
      return;
    }

    if (key === 'o') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'openFile' });
      return;
    }

    if (key === 'g') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'toggleGraphView' });
      return;
    }
  }

  function setupShortcuts() {
    document.addEventListener('keydown', handleGlobalKeydown);
  }

  /* ==========================================================
   * 初期化
   * ========================================================== */

  function init() {
    safeRun(function () {
      if (typeof d3 === 'undefined') {
        if (window.console && console.error) {
          console.error('[Hirake] d3 が読み込まれていないため、キャンバスを初期化できません。');
        }
        return;
      }

      var data = window.__canvasData;
      if (!data || typeof data !== 'object'
          || !Array.isArray(data.nodes) || !Array.isArray(data.edges)) {
        if (window.console && console.error) {
          console.error('[Hirake] キャンバスデータが不正です。', data);
        }
        return;
      }

      buildCanvas(data);
    });

    safeRun(setupShortcuts);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
