/*
 * Hirake - structure.js
 *
 * 構造クエリビューの描画・クエリ切替を担当する。
 * データはテンプレート埋め込みの window.__structureData
 * （{ root, folderLabel, skippedFiles, files:[{path, name, rel,
 *    items:[{kind, level, text, line, checked}]}] }）を用い、
 * クエリの切替（見出し / タスク / キーワード）はすべてブラウザ内で完結する。
 *
 * ホスト（WPF）との契約:
 *   WebView→ホスト postMessage:
 *     { type:'openFile', path:<string>, line:<number> }  項目クリック（該当行へジャンプ）
 *     { type:'shortcut', action:... }                    viewer.js と同じショートカット転送
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
      // ホスト未接続（ブラウザ単体表示など）では黙って無視する。
    }
  }

  var data = window.__structureData || { files: [], truncated: false };

  var modeSelect = document.getElementById('sq-mode');
  var headingLevelSelect = document.getElementById('sq-heading-level');
  var taskStateSelect = document.getElementById('sq-task-state');
  var keywordInput = document.getElementById('sq-keyword');
  var summaryEl = document.getElementById('sq-summary');
  var resultsEl = document.getElementById('sq-results');
  var emptyEl = document.getElementById('sq-empty');

  /* ==========================================================
   * フィルタ
   * ========================================================== */

  // 現在のクエリ設定で対象になる項目だけを返す。
  function filterItems(items) {
    var mode = modeSelect.value;

    if (mode === 'heading') {
      var maxLevel = headingLevelSelect.value === 'all'
        ? 6 : parseInt(headingLevelSelect.value, 10);
      return items.filter(function (item) {
        return item.kind === 'heading' && item.level <= maxLevel;
      });
    }

    if (mode === 'task') {
      var state = taskStateSelect.value;
      return items.filter(function (item) {
        if (item.kind !== 'task') return false;
        if (state === 'open') return !item.checked;
        if (state === 'done') return item.checked;
        return true;
      });
    }

    // キーワード: 箇条書き（タスク含む）・段落・見出しの平文に対する部分一致。
    var keyword = (keywordInput.value || '').trim().toLowerCase();
    if (keyword.length === 0) {
      return [];
    }
    return items.filter(function (item) {
      return item.text.toLowerCase().indexOf(keyword) >= 0;
    });
  }

  /* ==========================================================
   * 描画
   * ========================================================== */

  function buildItemRow(file, item) {
    var row = document.createElement('div');
    row.className = 'sq-item';
    row.setAttribute('data-kind', item.kind);
    if (item.kind === 'heading') {
      row.setAttribute('data-level', String(item.level));
    }
    if (item.kind === 'task' && item.checked) {
      row.className += ' sq-task-done';
    }

    var lineBadge = document.createElement('span');
    lineBadge.className = 'sq-item-line';
    lineBadge.textContent = 'L' + item.line;
    row.appendChild(lineBadge);

    if (item.kind === 'task') {
      var mark = document.createElement('span');
      mark.className = 'sq-task-mark';
      mark.textContent = item.checked ? '[x]' : '[ ]';
      row.appendChild(mark);
    }

    var text = document.createElement('span');
    text.className = 'sq-item-text';
    appendHighlightedText(text, buildDisplayText(item.text));
    row.appendChild(text);

    row.addEventListener('click', function () {
      postToHost({ type: 'openFile', path: file.path, line: item.line });
    });

    return row;
  }

  // 表示用プレビューを作る。長文はキーワードのヒット位置を中心に切り詰める
  // （照合はホストから渡された text 全体に対して行い、表示だけを短くする）。
  var DISPLAY_MAX = 240;
  function buildDisplayText(text) {
    if (text.length <= DISPLAY_MAX) {
      return text;
    }

    var start = 0;
    if (modeSelect.value === 'keyword') {
      var keyword = (keywordInput.value || '').trim();
      if (keyword.length > 0) {
        var found = text.toLowerCase().indexOf(keyword.toLowerCase());
        if (found > DISPLAY_MAX / 2) {
          start = Math.min(found - Math.floor(DISPLAY_MAX / 2), text.length - DISPLAY_MAX);
        }
      }
    }

    var slice = text.slice(start, start + DISPLAY_MAX);
    return (start > 0 ? '…' : '') + slice + (start + DISPLAY_MAX < text.length ? '…' : '');
  }

  // キーワードモードのときは一致箇所を <mark> で強調する（textContent ベースで安全に組む）。
  function appendHighlightedText(parent, text) {
    var keyword = modeSelect.value === 'keyword'
      ? (keywordInput.value || '').trim() : '';
    if (keyword.length === 0) {
      parent.textContent = text;
      return;
    }

    var lower = text.toLowerCase();
    var lowerKeyword = keyword.toLowerCase();

    // toLowerCase で長さが変わる文字（ İ 等）はインデックスがずれるため、
    // 強調をあきらめてプレーン表示にフォールバックする（フィルタ判定には影響しない）。
    if (lower.length !== text.length) {
      parent.textContent = text;
      return;
    }

    var pos = 0;
    while (pos < text.length) {
      var found = lower.indexOf(lowerKeyword, pos);
      if (found < 0) {
        parent.appendChild(document.createTextNode(text.slice(pos)));
        return;
      }
      if (found > pos) {
        parent.appendChild(document.createTextNode(text.slice(pos, found)));
      }
      var mark = document.createElement('mark');
      mark.textContent = text.slice(found, found + keyword.length);
      parent.appendChild(mark);
      pos = found + keyword.length;
    }
  }

  function render() {
    resultsEl.textContent = '';

    var totalItems = 0;
    var fileCount = 0;

    data.files.forEach(function (file) {
      var items = filterItems(file.items);
      if (items.length === 0) {
        return;
      }
      fileCount++;
      totalItems += items.length;

      var section = document.createElement('div');
      section.className = 'sq-file';

      var header = document.createElement('div');
      header.className = 'sq-file-header';

      var name = document.createElement('span');
      name.textContent = file.name;
      header.appendChild(name);

      if (file.rel !== file.name) {
        var rel = document.createElement('span');
        rel.className = 'sq-file-rel';
        rel.textContent = file.rel;
        rel.title = file.rel;
        header.appendChild(rel);
      }

      var count = document.createElement('span');
      count.className = 'sq-file-count';
      count.textContent = items.length + ' 件';
      header.appendChild(count);

      section.appendChild(header);

      items.forEach(function (item) {
        section.appendChild(buildItemRow(file, item));
      });

      resultsEl.appendChild(section);
    });

    // 要約と空状態。0件でも打ち切り時はその旨を必ず示す
    // （未走査領域に一致が残っている可能性を隠さない）。
    if (totalItems === 0) {
      summaryEl.textContent = data.truncated ? '上限到達のため一部未走査' : '';
      var emptyText = modeSelect.value === 'keyword'
        && (keywordInput.value || '').trim().length === 0
        ? 'キーワードを入力してください'
        : '一致する項目がありません';
      if (data.truncated) {
        emptyText += '（上限到達のため一部未走査）';
      }
      emptyEl.textContent = emptyText;
      emptyEl.className = 'visible';
    } else {
      var summary = totalItems + ' 件（' + fileCount + ' ファイル）';
      if (data.truncated) {
        summary += ' / 上限到達のため一部未走査';
      }
      summaryEl.textContent = summary;
      emptyEl.className = '';
    }
  }

  // クエリ種別に応じてサブオプションの表示を切り替える。
  function updateToolbar() {
    var mode = modeSelect.value;
    headingLevelSelect.classList.toggle('sq-hidden', mode !== 'heading');
    taskStateSelect.classList.toggle('sq-hidden', mode !== 'task');
    keywordInput.classList.toggle('sq-hidden', mode !== 'keyword');
    if (mode === 'keyword') {
      keywordInput.focus();
    }
  }

  function setupToolbar() {
    modeSelect.addEventListener('change', function () {
      updateToolbar();
      render();
    });
    headingLevelSelect.addEventListener('change', render);
    taskStateSelect.addEventListener('change', render);

    // キーワードは軽いデバウンスで随時再描画する。
    var timer = null;
    keywordInput.addEventListener('input', function () {
      if (timer !== null) {
        clearTimeout(timer);
      }
      timer = setTimeout(function () {
        timer = null;
        render();
      }, 200);
    });
  }

  /* ==========================================================
   * テーマ切替（ホストから呼ばれる）
   * ========================================================== */

  function setTheme(theme) {
    document.documentElement.setAttribute(
      'data-theme', theme === 'dark' ? 'dark' : 'light');
  }

  window.__mdvSetTheme = setTheme;

  /* ==========================================================
   * ショートカット転送（graph.js と同じ最小セット + 自ビュー切替）
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
    // 描画とショートカット転送は互いに独立させ、
    // 片方が失敗してももう片方が機能するようにする。
    safeRun(setupShortcuts);
    safeRun(function () {
      setupToolbar();
      updateToolbar();
      render();
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
