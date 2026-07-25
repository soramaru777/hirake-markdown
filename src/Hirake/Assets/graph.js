/*
 * Hirake - graph.js
 *
 * 役割:
 *   - フォルダ内 Markdown ファイルの参照関係を d3-force でグラフ表示する
 *     （ナレッジグラフ・ビュー）。
 *   - ズーム／パン、ノードのドラッグ＆ピン留め、ホバー強調を提供する。
 *   - ノードクリックでホストへファイルを開くよう依頼する。
 *   - viewer.js と同じくショートカットをホストへ転送する。
 *
 * データ（ホストが graph-template.html の {{DATA}} に埋め込む）:
 *   window.__graphData = {
 *     root: "C:\\path\\to\\folder",
 *     folderLabel: "folder",
 *     nodes: [{ id: "C:\\path\\a.md", label: "a.md", degree: 3 }, ...],
 *     edges: [{ source: "C:\\path\\a.md", target: "C:\\path\\b.md" }, ...]
 *   }
 *
 * ホスト（WPF）との契約:
 *   WebView→ホスト postMessage:
 *     { type:'openFile', path:<string> }              ノードクリック
 *     { type:'shortcut', action:'closeTab' }           Ctrl+W
 *     { type:'shortcut', action:'nextTab' }            Ctrl+Tab
 *     { type:'shortcut', action:'prevTab' }            Ctrl+Shift+Tab
 *     { type:'shortcut', action:'openFile' }           Ctrl+O
 *     { type:'shortcut', action:'toggleSidebar' }      Ctrl+B
 *     { type:'shortcut', action:'globalSearch' }       Ctrl+Shift+F
 *     { type:'shortcut', action:'cycleTheme' }         Ctrl+Shift+D
 *     { type:'shortcut', action:'quickPaste' }         Ctrl+Shift+V
 *     { type:'shortcut', action:'toggleGraphView' }    Ctrl+G
 *   ホスト→WebView 公開関数:
 *     window.__mdvSetTheme('light'|'dark')  テーマ切替（配色は CSS 変数追従のため
 *                                            data-theme を切替えるだけでよい）
 *
 * 注意:
 *   - d3 が読み込めていない、またはデータが不正でも全体が死なないよう、
 *     各機能は typeof チェック + try/catch でガードしてある。
 *   - ノードの色は CSS クラス経由でのみ変更する（JS から色値を直接 attr しない）。
 *   - グローバル汚染を避けるため IIFE で包む。
 */
(function () {
  'use strict';

  /* ==========================================================
   * 共通ユーティリティ（viewer.js と同じパターン）
   * ========================================================== */

  function postToHost(message) {
    try {
      if (
        window.chrome &&
        window.chrome.webview &&
        typeof window.chrome.webview.postMessage === 'function'
      ) {
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
      // 個別機能の失敗が他の初期化処理を止めないようにする。
      if (window.console && console.error) {
        console.error('[Hirake]', err);
      }
    }
  }

  /* ==========================================================
   * 情報バー・空状態表示
   * ========================================================== */

  function updateInfoBar(folderLabel, nodeCount, edgeCount) {
    var folderEl = document.querySelector('#graph-info .graph-info-folder');
    var countsEl = document.querySelector('#graph-info .graph-info-counts');
    if (folderEl) folderEl.textContent = folderLabel || '';
    if (countsEl) {
      countsEl.textContent = ' — ' + nodeCount + ' ファイル / ' + edgeCount + ' リンク';
    }
  }

  function showEmptyState() {
    var el = document.getElementById('graph-empty');
    if (el) el.classList.add('visible');
  }

  /* ==========================================================
   * テーマ切替（ホスト公開）
   *
   * 配色はすべて CSS カスタムプロパティ（--graph-*）経由のため、
   * data-theme 属性を切替えるだけで SVG の見た目も追従する。
   * ========================================================== */

  function setTheme(theme) {
    var dark = theme === 'dark';
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  }

  window.__mdvSetTheme = setTheme;

  /* ==========================================================
   * ズーム時にノード上のドラッグ開始と競合しないようにするフィルタ
   * （d3.zoom の既定フィルタに「ノード要素上は除外」を追加したもの）
   * ========================================================== */

  function zoomFilter(event) {
    if (event.button) return false;
    if (event.ctrlKey && event.type !== 'wheel') return false;
    var target = event.target;
    if (target && target.closest && target.closest('.graph-node')) return false;
    return true;
  }

  /* ==========================================================
   * グラフ本体の構築
   * ========================================================== */

  function buildGraph(data) {
    var nodes = data.nodes.slice();
    var edges = data.edges.slice();

    updateInfoBar(data.folderLabel || data.root || '', nodes.length, edges.length);

    if (nodes.length === 0) {
      showEmptyState();
      return;
    }

    // d3.forceLink が edges[].source / target をノード参照に書き換えてしまう前に、
    // 隣接判定・DOM紐付け用に元の ID（文字列）を保持しておく。
    edges.forEach(function (e) {
      e._srcId = e.source;
      e._tgtId = e.target;
    });

    // ノード半径は degree（次数）に応じて 6〜18px の範囲でスケールする。
    // 全ノードの degree が 0 の場合は scaleSqrt のドメインが潰れるため固定半径にする。
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

    var svgSel = d3.select('#graph-svg');
    var zoomLayer = svgSel.append('g').attr('class', 'graph-zoom-layer');
    var edgesLayer = zoomLayer.append('g').attr('class', 'graph-edges-layer');
    var nodesLayer = zoomLayer.append('g').attr('class', 'graph-nodes-layer');

    var zoomBehavior = d3.zoom()
      .scaleExtent([0.2, 4])
      .filter(zoomFilter)
      .on('zoom', function (event) {
        zoomLayer.attr('transform', event.transform);
      });
    svgSel.call(zoomBehavior);

    var simulation = d3.forceSimulation(nodes)
      .force('link', d3.forceLink(edges).id(function (d) { return d.id; }).distance(80))
      .force('charge', d3.forceManyBody().strength(-220))
      .force('center', d3.forceCenter(width / 2, height / 2))
      .force('collide', d3.forceCollide(function (d) { return nodeRadius(d) + 4; }));

    var edgeSel = edgesLayer.selectAll('line.graph-edge')
      .data(edges)
      .join('line')
      .attr('class', 'graph-edge');

    var dragBehavior = d3.drag()
      .clickDistance(4)
      .on('start', dragStarted)
      .on('drag', dragged)
      .on('end', dragEnded);

    var nodeSel = nodesLayer.selectAll('g.graph-node')
      .data(nodes, function (d) { return d.id; })
      .join('g')
      .attr('class', 'graph-node')
      .call(dragBehavior)
      .on('click', nodeClicked)
      .on('dblclick', nodeDblClicked)
      .on('mouseenter', nodeMouseEnter)
      .on('mouseleave', nodeMouseLeave);

    nodeSel.append('circle')
      .attr('class', 'graph-node-circle')
      .attr('r', nodeRadius);

    nodeSel.append('text')
      .attr('class', 'graph-node-label')
      .attr('y', function (d) { return nodeRadius(d) + 14; })
      .text(function (d) { return d.label || d.id; });

    simulation.on('tick', function () {
      edgeSel
        .attr('x1', function (d) { return d.source.x; })
        .attr('y1', function (d) { return d.source.y; })
        .attr('x2', function (d) { return d.target.x; })
        .attr('y2', function (d) { return d.target.y; });

      nodeSel.attr('transform', function (d) {
        return 'translate(' + d.x + ',' + d.y + ')';
      });
    });

    // ---- ドラッグ：終了時に fx/fy を保持してその場にピン留めする ----

    function dragStarted(event, d) {
      svgSel.classed('dragging', true);
      if (!event.active) simulation.alphaTarget(0.3).restart();
      d.fx = d.x;
      d.fy = d.y;
    }

    function dragged(event, d) {
      d.fx = event.x;
      d.fy = event.y;
    }

    function dragEnded(event, d) {
      svgSel.classed('dragging', false);
      if (!event.active) simulation.alphaTarget(0);
      // ここで fx/fy を null にせず保持する＝ドラッグ後の位置にピン留めする。
      d3.select(this).select('.graph-node-circle').classed('is-pinned', true);
    }

    // ---- クリック／ダブルクリック ----

    function nodeClicked(event, d) {
      // ドラッグ由来のクリックは clickDistance により抑止されるため、
      // ここに届くのは実際のクリックのみ。
      event.stopPropagation();
      postToHost({ type: 'openFile', path: d.id });
    }

    function nodeDblClicked(event, d) {
      // svg 側の zoom（ダブルクリックでの拡大）へ伝播させない。
      event.stopPropagation();
      d.fx = null;
      d.fy = null;
      d3.select(this).select('.graph-node-circle').classed('is-pinned', false);
      simulation.alpha(0.3).restart();
    }

    // ---- ホバー強調 ----

    function nodeMouseEnter(event, d) {
      d3.select(this).select('.graph-node-circle').classed('is-hover', true);
      d3.select(this).select('.graph-node-label').classed('is-hover', true);
      edgeSel.classed('is-adjacent', function (e) {
        return e._srcId === d.id || e._tgtId === d.id;
      });
    }

    function nodeMouseLeave(event, d) {
      d3.select(this).select('.graph-node-circle').classed('is-hover', false);
      d3.select(this).select('.graph-node-label').classed('is-hover', false);
      edgeSel.classed('is-adjacent', false);
    }

    // ---- リサイズ：中心力を画面中央に追従させる ----

    window.addEventListener('resize', function () {
      safeRun(function () {
        width = window.innerWidth;
        height = window.innerHeight;
        simulation.force('center', d3.forceCenter(width / 2, height / 2));
        simulation.alpha(0.3).restart();
      });
    });
  }

  /* ==========================================================
   * ショートカット転送（viewer.js の handleGlobalKeydown と同じ契約）
   * ========================================================== */

  function handleGlobalKeydown(event) {
    if (!event.ctrlKey || event.altKey || event.metaKey) return;

    // Ctrl+Tab / Ctrl+Shift+Tab
    if (event.key === 'Tab') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: event.shiftKey ? 'prevTab' : 'nextTab' });
      return;
    }

    var key = (event.key || '').toLowerCase();

    // --- Ctrl+Shift+* 系（ホストへ転送） ---
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
      // 未対応の Ctrl+Shift+* はブラウザ標準に委ねる。
      return;
    }

    // --- Ctrl+* （Shift なし）系 ---

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
    // グラフ構築とショートカット転送は互いに独立させ、
    // 片方が失敗してももう片方が機能するようにする。
    safeRun(function () {
      if (typeof d3 === 'undefined') {
        if (window.console && console.error) {
          console.error('[Hirake] d3 が読み込まれていないため、グラフを初期化できません。');
        }
        return;
      }

      var data = window.__graphData;
      if (!data || typeof data !== 'object' || !Array.isArray(data.nodes) || !Array.isArray(data.edges)) {
        if (window.console && console.error) {
          console.error('[Hirake] グラフデータが不正です。', data);
        }
        return;
      }

      buildGraph(data);
    });

    safeRun(setupShortcuts);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
