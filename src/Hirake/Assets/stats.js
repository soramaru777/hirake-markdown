/*
 * Hirake - stats.js
 *
 * フォルダ統計ダッシュボードの描画を担当する。
 * データはテンプレート埋め込みの window.__statsData
 * （{ root, folderLabel, truncated, summary:{files, chars, headings, openTasks,
 *    doneTasks, imageRefs}, heatmap:[{d,c}], history:[{d,files,chars,openTasks}],
 *    stale/largest/taskHeavy:[{path,name,rel,value}] }）。
 * チャートは同梱の d3.v7（vendor）で描画し、d3 が読めない環境でも
 * サマリ・ランキングは表示される（チャート部のみ注記にフォールバック）。
 *
 * ホスト（WPF）との契約:
 *   WebView→ホスト postMessage:
 *     { type:'openFile', path:<string> }   ランキング行クリック（文書を開く）
 *     { type:'shortcut', action:... }      viewer.js と同じショートカット転送
 *   ホスト→WebView 公開関数:
 *     window.__mdvSetTheme('light'|'dark')  テーマ切替（配色は CSS 変数追従）
 */
(function () {
  'use strict';

  function safeRun(fn) {
    try {
      fn();
    } catch (e) {
      // 一部機能の失敗で全体を止めない。
    }
  }

  function postToHost(message) {
    try {
      if (window.chrome && window.chrome.webview &&
          typeof window.chrome.webview.postMessage === 'function') {
        window.chrome.webview.postMessage(message);
      }
    } catch (e) {
      // ホスト未接続では黙って無視する。
    }
  }

  var data = window.__statsData || {
    folderLabel: '', truncated: false,
    summary: { files: 0, chars: 0, headings: 0, openTasks: 0, doneTasks: 0, imageRefs: 0 },
    heatmap: [], history: [], stale: [], largest: [], taskHeavy: [],
  };

  function fmt(n) {
    try {
      return Number(n).toLocaleString('ja-JP');
    } catch (e) {
      return String(n);
    }
  }

  function cssVar(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }

  /* ==========================================================
   * サマリ
   * ========================================================== */

  function renderHeader() {
    document.getElementById('st-title').textContent = '統計: ' + data.folderLabel;
    document.getElementById('st-note').textContent =
      data.truncated ? '上限到達のため一部未走査' : '';
  }

  function renderSummary() {
    var host = document.getElementById('st-summary');
    host.textContent = '';
    var cards = [
      { label: 'ファイル', value: fmt(data.summary.files) },
      { label: '総文字数', value: fmt(data.summary.chars) },
      { label: '見出し', value: fmt(data.summary.headings) },
      { label: '未完了タスク', value: fmt(data.summary.openTasks) },
      { label: '完了タスク', value: fmt(data.summary.doneTasks) },
      { label: '画像参照', value: fmt(data.summary.imageRefs) },
    ];
    cards.forEach(function (card) {
      var el = document.createElement('div');
      el.className = 'st-card';
      var value = document.createElement('div');
      value.className = 'st-card-value';
      value.textContent = card.value;
      var label = document.createElement('div');
      label.className = 'st-card-label';
      label.textContent = card.label;
      el.appendChild(value);
      el.appendChild(label);
      host.appendChild(el);
    });
  }

  /* ==========================================================
   * 更新ヒートマップ（GitHub 風カレンダー）
   * ========================================================== */

  var CELL = 11;
  var GAP = 2;

  function heatColor(count, max) {
    if (count <= 0) return cssVar('--st-heat-empty');
    var ratio = max <= 1 ? 1 : count / max;
    if (ratio <= 0.25) return cssVar('--st-heat-1');
    if (ratio <= 0.5) return cssVar('--st-heat-2');
    if (ratio <= 0.75) return cssVar('--st-heat-3');
    return cssVar('--st-heat-4');
  }

  function renderHeatmap() {
    var host = document.getElementById('st-heatmap');
    host.textContent = '';
    if (typeof d3 === 'undefined') {
      var note = document.createElement('div');
      note.className = 'st-empty';
      note.textContent = 'チャートライブラリを読み込めないため表示できません';
      host.appendChild(note);
      return;
    }

    var byDate = Object.create(null);
    var max = 0;
    data.heatmap.forEach(function (day) {
      byDate[day.d] = day.c;
      if (day.c > max) max = day.c;
    });

    // 今日を含む過去 365 日を、週（日曜はじまり）×曜日のグリッドに並べる。
    var today = new Date();
    today.setHours(0, 0, 0, 0);
    var days = [];
    for (var i = 364; i >= 0; i--) {
      var date = new Date(today);
      date.setDate(today.getDate() - i);
      days.push(date);
    }
    var firstDow = days[0].getDay();
    var weeks = Math.ceil((firstDow + days.length) / 7);

    var width = weeks * (CELL + GAP) + 30;
    var height = 7 * (CELL + GAP) + 20;
    var svg = d3.select(host).append('svg')
      .attr('width', width)
      .attr('height', height);

    var monthShown = Object.create(null);
    days.forEach(function (date, index) {
      var slot = firstDow + index;
      var week = Math.floor(slot / 7);
      var dow = slot % 7;
      var key = date.getFullYear() + '-'
        + String(date.getMonth() + 1).padStart(2, '0') + '-'
        + String(date.getDate()).padStart(2, '0');
      var count = byDate[key] || 0;

      svg.append('rect')
        .attr('class', 'st-heat-cell')
        .attr('x', 28 + week * (CELL + GAP))
        .attr('y', 14 + dow * (CELL + GAP))
        .attr('width', CELL)
        .attr('height', CELL)
        .attr('rx', 2)
        .attr('fill', heatColor(count, max))
        .append('title')
        .text(key + ': ' + count + ' ファイル更新');

      // 月ラベル（各月の最初のセルの週に 1 回だけ）。
      var monthKey = date.getFullYear() + '-' + date.getMonth();
      if (date.getDate() === 1 && !monthShown[monthKey]) {
        monthShown[monthKey] = true;
        svg.append('text')
          .attr('class', 'st-axis-label')
          .attr('x', 28 + week * (CELL + GAP))
          .attr('y', 9)
          .text((date.getMonth() + 1) + '月');
      }
    });

    // 曜日ラベル（月・水・金）。
    [['月', 1], ['水', 3], ['金', 5]].forEach(function (pair) {
      svg.append('text')
        .attr('class', 'st-axis-label')
        .attr('x', 2)
        .attr('y', 14 + pair[1] * (CELL + GAP) + CELL - 2)
        .text(pair[0]);
    });
  }

  /* ==========================================================
   * 推移（履歴蓄積型の折れ線）
   * ========================================================== */

  function renderLineChart(hostId, points, valueOf, label, colorVar) {
    var host = document.getElementById(hostId);
    host.textContent = '';
    if (typeof d3 === 'undefined') {
      return;
    }

    var width = 640;
    var height = 120;
    var margin = { top: 14, right: 12, bottom: 20, left: 64 };

    var svg = d3.select(host).append('svg')
      .attr('width', width)
      .attr('height', height);

    svg.append('text')
      .attr('class', 'st-axis-label')
      .attr('x', margin.left)
      .attr('y', 10)
      .text(label);

    var parse = d3.timeParse('%Y-%m-%d');
    var series = points.map(function (p) {
      return { date: parse(p.d), value: valueOf(p) };
    }).filter(function (p) { return p.date !== null; });
    if (series.length === 0) {
      return;
    }

    var x = d3.scaleTime()
      .domain(d3.extent(series, function (p) { return p.date; }))
      .range([margin.left, width - margin.right]);
    var yMax = d3.max(series, function (p) { return p.value; }) || 1;
    var y = d3.scaleLinear()
      .domain([0, yMax * 1.1])
      .range([height - margin.bottom, margin.top]);

    var color = cssVar(colorVar);

    if (series.length > 1) {
      svg.append('path')
        .attr('class', 'st-line-path')
        .attr('stroke', color)
        .attr('d', d3.line()
          .x(function (p) { return x(p.date); })
          .y(function (p) { return y(p.value); })(series));
    }

    svg.selectAll('.st-line-dot')
      .data(series)
      .enter()
      .append('circle')
      .attr('class', 'st-line-dot')
      .attr('fill', color)
      .attr('cx', function (p) { return x(p.date); })
      .attr('cy', function (p) { return y(p.value); })
      .attr('r', 3)
      .append('title')
      .text(function (p) {
        return d3.timeFormat('%Y-%m-%d')(p.date) + ': ' + fmt(p.value);
      });

    // 軸ラベル（最小限: 両端の日付と最大値）。
    var first = series[0];
    var last = series[series.length - 1];
    svg.append('text')
      .attr('class', 'st-axis-label')
      .attr('x', margin.left)
      .attr('y', height - 6)
      .text(d3.timeFormat('%Y-%m-%d')(first.date));
    if (series.length > 1) {
      svg.append('text')
        .attr('class', 'st-axis-label')
        .attr('x', width - margin.right)
        .attr('y', height - 6)
        .attr('text-anchor', 'end')
        .text(d3.timeFormat('%Y-%m-%d')(last.date));
    }
    svg.append('text')
      .attr('class', 'st-axis-label')
      .attr('x', margin.left - 4)
      .attr('y', y(yMax) + 3)
      .attr('text-anchor', 'end')
      .text(fmt(yMax));
  }

  function renderHistory() {
    var note = document.getElementById('st-history-note');
    if (data.history.length <= 1) {
      note.textContent =
        'まだ ' + data.history.length + ' 回分の記録です。ダッシュボードを開くたびに記録され、推移グラフが育ちます';
    } else {
      note.textContent = data.history.length + ' 回分の記録';
    }
    renderLineChart('st-chart-chars', data.history,
      function (p) { return p.chars; }, '総文字数', '--st-line');
    renderLineChart('st-chart-files', data.history,
      function (p) { return p.files; }, 'ファイル数', '--st-line2');
  }

  /* ==========================================================
   * ランキング
   * ========================================================== */

  function renderRanking(hostId, items, formatValue, emptyText) {
    var host = document.getElementById(hostId);
    host.textContent = '';

    if (items.length === 0) {
      var empty = document.createElement('div');
      empty.className = 'st-empty';
      empty.textContent = emptyText;
      host.appendChild(empty);
      return;
    }

    items.forEach(function (item) {
      var row = document.createElement('div');
      row.className = 'st-rank-row';
      row.title = 'クリックで開く';

      var value = document.createElement('span');
      value.className = 'st-rank-value';
      value.textContent = formatValue(item.value);
      row.appendChild(value);

      var name = document.createElement('span');
      name.className = 'st-rank-name';
      name.textContent = item.name;
      row.appendChild(name);

      if (item.rel !== item.name) {
        var rel = document.createElement('span');
        rel.className = 'st-rank-rel';
        rel.textContent = item.rel;
        rel.title = item.rel;
        row.appendChild(rel);
      }

      row.addEventListener('click', function () {
        postToHost({ type: 'openFile', path: item.path });
      });

      host.appendChild(row);
    });
  }

  function renderRankings() {
    renderRanking('st-stale', data.stale,
      function (v) { return fmt(v) + ' 日前'; }, '文書がありません');
    renderRanking('st-largest', data.largest,
      function (v) { return fmt(v) + ' 文字'; }, '文書がありません');
    renderRanking('st-taskheavy', data.taskHeavy,
      function (v) { return '未完了 ' + fmt(v); }, '未完了タスクはありません');
  }

  /* ==========================================================
   * テーマ切替（ホストから呼ばれる）
   * ========================================================== */

  function setTheme(theme) {
    document.documentElement.setAttribute(
      'data-theme', theme === 'dark' ? 'dark' : 'light');
    // チャートは CSS 変数の解決値を属性に焼き込んでいるため描き直す。
    safeRun(renderHeatmap);
    safeRun(renderHistory);
  }

  window.__mdvSetTheme = setTheme;

  /* ==========================================================
   * ショートカット転送（fingerprint.js と同じセット + 自ビュー切替）
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
    // 各セクションは独立に描画し、どれかの失敗で全体を止めない。
    safeRun(setupShortcuts);
    safeRun(renderHeader);
    safeRun(renderSummary);
    safeRun(renderHeatmap);
    safeRun(renderHistory);
    safeRun(renderRankings);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
