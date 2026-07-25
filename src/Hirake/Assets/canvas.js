/*
 * Hirake - canvas.js
 *
 * 無限キャンバス・モード（ADR-0001 案B: 単一 WebView2 の HTML キャンバス方式）。
 * graph.js の d3-zoom / d3-force / ピン留め資産を基礎に、セマンティックズームを追加する:
 *   - 遠景（k < CARD_THRESHOLD）: ノード + エッジ（グラフビュー相当）
 *   - 中間（k >= CARD_THRESHOLD）: カード（ファイル名 + サムネイル）。エッジは薄く継続
 *   - 近景 iframe は 3-2 で追加予定（本段階ではダブルクリックで通常タブへ）
 *
 * データ（ホストが canvas-template.html の {{DATA}} に埋め込む）:
 *   window.__canvasData = {
 *     root, folderLabel, truncated,
 *     nodes: [{ id(絶対パス), rel(相対パス・保存キー), label, degree,
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
 *     { type:'shortcut', action:... }                   viewer.js と同じショートカット転送
 *   ホスト→WebView 公開関数:
 *     window.__mdvSetTheme('light'|'dark')  テーマ切替（配色は CSS 変数追従）
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
  var CARD_THRESHOLD = 1.5;
  var CARD_W = 180;
  var CARD_H = 130;
  var VIEW_MARGIN = 200; // カード仮想化のビューポートマージン（世界座標）

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
      var card = currentTransform.k >= CARD_THRESHOLD;
      document.body.classList.toggle('mode-card', card);
      if (card) {
        refreshCards();
      }
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
          var el = cardById[id];
          if (el && el.parentNode) el.parentNode.removeChild(el);
          delete cardById[id];
        }
      });
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
      card.style.left = (n.x - CARD_W / 2) + 'px';
      card.style.top = (n.y - CARD_H / 2) + 'px';
    }

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

    // 初期モード反映。保存済みビューポートはここで復元する
    // （zoomBehavior.transform は同期的に zoom ハンドラを発火させるため、
    //  refreshCards 等が参照する状態がすべて定義された後でなければならない）。
    if (data.view && typeof data.view.k === 'number' && data.view.k > 0) {
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
