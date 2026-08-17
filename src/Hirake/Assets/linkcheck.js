/*
 * Hirake - linkcheck.js
 *
 * リンク切れ・孤立ページの検出ビューの描画と切替を担当する（ISSUE #56）。
 * データはテンプレート埋め込みの window.__linkCheckData
 * （{ root, folderLabel, fileCount, truncated, brokenCapped, unreadableCount,
 *    skippedDirectoryCount,
 *    broken:[{src, rel, line, target, resolved, reason}],
 *    orphans:[{path, rel, name}] }）を用い、切替はすべてブラウザ内で完結する。
 *
 * 走査の打ち切り（ファイル数上限・件数上限・読めなかったファイル／フォルダ）は
 * 0 件のときでも必ず表示する。「0 件」と「見ていないだけ」を混同させないため。
 *
 * ホスト（WPF）との契約:
 *   WebView→ホスト postMessage:
 *     { type:'openFile', path:<string>, line:<number> }  行クリック（該当行へジャンプ）
 *     { type:'copyText', text:<string> }                 一覧のコピー
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

  var data = window.__linkCheckData || {
    root: '', fileCount: 0, truncated: false, brokenCapped: false,
    unreadableCount: 0, skippedDirectoryCount: 0, broken: [], orphans: []
  };
  var brokenList = data.broken || [];
  var orphanList = data.orphans || [];

  // 入口になりやすいファイル。既定では孤立から除くが、隠したままにはしない
  // （チェックボックスで戻せる）。
  var ENTRY_NAMES = ['index.md', 'readme.md', 'index.markdown', 'readme.markdown'];

  var REASON_TEXT = {
    missing: 'リンク先が無い',
    outside: '走査範囲の外',
    wikiNotFound: '索引に無い',
    unverifiable: '確認できない'
  };

  var modeSelect = document.getElementById('lc-mode');
  var excludeCheck = document.getElementById('lc-exclude');
  var excludeLabel = document.getElementById('lc-exclude-label');
  var copyButton = document.getElementById('lc-copy');
  var summaryEl = document.getElementById('lc-summary');
  var scopeEl = document.getElementById('lc-scope');
  var notesEl = document.getElementById('lc-notes');
  var resultsEl = document.getElementById('lc-results');
  var emptyEl = document.getElementById('lc-empty');

  /* ==========================================================
   * 絞り込み
   * ========================================================== */

  function isEntryFile(name) {
    return ENTRY_NAMES.indexOf(String(name || '').toLowerCase()) >= 0;
  }

  function visibleOrphans() {
    if (!excludeCheck.checked) {
      return orphanList;
    }
    return orphanList.filter(function (o) {
      return !isEntryFile(o.name);
    });
  }

  // リンク切れを参照元ファイルごとにまとめる（元の順序を保つ）。
  function groupBrokenBySource() {
    var groups = [];
    // ファイルパスをキーにするため、プロトタイプ由来のキー（__proto__ 等）と
    // 衝突しない素の辞書を使う。
    var index = Object.create(null);
    brokenList.forEach(function (item) {
      var key = item.src;
      if (!Object.prototype.hasOwnProperty.call(index, key)) {
        index[key] = groups.length;
        groups.push({ src: item.src, rel: item.rel, items: [] });
      }
      groups[index[key]].items.push(item);
    });
    return groups;
  }

  /* ==========================================================
   * 描画
   * ========================================================== */

  function buildBrokenRow(group, item) {
    var row = document.createElement('div');
    row.className = 'lc-item';

    var lineBadge = document.createElement('span');
    lineBadge.className = 'lc-item-line';
    lineBadge.textContent = 'L' + item.line;
    row.appendChild(lineBadge);

    var target = document.createElement('span');
    target.className = 'lc-item-target';
    target.textContent = item.target;
    row.appendChild(target);

    var reason = document.createElement('span');
    reason.className = 'lc-item-reason';
    reason.textContent = REASON_TEXT[item.reason] || item.reason;
    if (item.resolved) {
      // 「範囲外」「無い」の判断根拠になるので、実際に探した場所を添える。
      reason.title = item.resolved;
    }
    row.appendChild(reason);

    row.addEventListener('click', function () {
      postToHost({ type: 'openFile', path: group.src, line: item.line });
    });

    return row;
  }

  function buildOrphanRow(orphan) {
    var row = document.createElement('div');
    row.className = 'lc-orphan';

    var rel = document.createElement('span');
    rel.className = 'lc-orphan-rel';
    rel.textContent = orphan.rel;
    row.appendChild(rel);

    var note = document.createElement('span');
    note.className = 'lc-orphan-note';
    note.textContent = '被リンク 0';
    row.appendChild(note);

    row.addEventListener('click', function () {
      postToHost({ type: 'openFile', path: orphan.path });
    });

    return row;
  }

  function renderBroken() {
    var groups = groupBrokenBySource();
    groups.forEach(function (group) {
      var section = document.createElement('div');
      section.className = 'lc-file';

      var header = document.createElement('div');
      header.className = 'lc-file-header';

      var relEl = document.createElement('span');
      relEl.textContent = group.rel;
      header.appendChild(relEl);

      var count = document.createElement('span');
      count.className = 'lc-file-count';
      count.textContent = group.items.length + ' 件';
      header.appendChild(count);

      section.appendChild(header);

      group.items.forEach(function (item) {
        section.appendChild(buildBrokenRow(group, item));
      });

      resultsEl.appendChild(section);
    });
    return brokenList.length;
  }

  function renderOrphans() {
    var orphans = visibleOrphans();
    if (orphans.length > 0) {
      var section = document.createElement('div');
      section.className = 'lc-file';
      orphans.forEach(function (orphan) {
        section.appendChild(buildOrphanRow(orphan));
      });
      resultsEl.appendChild(section);
    }
    return orphans.length;
  }

  // 走査範囲と打ち切りの注記。0 件でも必ず出す。
  function renderNotes() {
    // 走査条件そのものを出す。ルートだけを見せると「ルート内なのに範囲外」に見える。
    scopeEl.textContent = '走査範囲: ' + data.root
      + '（' + (data.fileCount || 0) + ' ファイル / 上限 ' + (data.maxFiles || 500) + ' ファイル・'
      + (data.maxDepth || 32) + ' 階層。隠しフォルダ・ドット始まり・node_modules は除外）';

    notesEl.textContent = '';

    function addNote(text) {
      var note = document.createElement('div');
      note.className = 'lc-note';
      note.textContent = text;
      notesEl.appendChild(note);
    }

    if (data.truncated) {
      addNote('⚠ ファイル数の上限に達したため、一部を走査していません。'
        + 'ここに出ていない壊れたリンクがある可能性があります。');
    }
    if (data.brokenCapped) {
      addNote('⚠ リンク切れの件数が上限に達したため、以降を記録していません。');
    }
    if (data.unreadableCount > 0) {
      addNote('⚠ ' + data.unreadableCount
        + ' ファイルは大きすぎる、または読めないため中身を見ていません。'
        + 'そのファイルからのリンクは検査対象外です。');
    }
    if (data.skippedDirectoryCount > 0) {
      addNote('⚠ ' + data.skippedDirectoryCount
        + ' 個のフォルダは読めないため中を見ていません。その中の文書は走査していません。');
    }
  }

  function render() {
    resultsEl.textContent = '';

    var mode = modeSelect.value;
    var shown = mode === 'orphan' ? renderOrphans() : renderBroken();

    // 除外チェックは孤立ページのときだけ意味を持つ。
    excludeLabel.className = mode === 'orphan' ? '' : 'lc-hidden';

    var incomplete = data.truncated || data.brokenCapped
      || data.unreadableCount > 0 || data.skippedDirectoryCount > 0;

    if (shown === 0) {
      var emptyText = mode === 'orphan'
        ? '孤立ページはありません'
        : 'リンク切れはありません';
      if (incomplete) {
        emptyText += '（ただし全部は見ていません。上の注意を確認してください）';
      }
      emptyEl.textContent = emptyText;
      emptyEl.className = 'visible';
      summaryEl.textContent = incomplete ? '一部未走査' : '';
    } else {
      emptyEl.className = '';
      summaryEl.textContent = shown + ' 件' + (incomplete ? ' / 一部未走査' : '');
    }
  }

  /* ==========================================================
   * コピー（hirake:// 付き）
   * ========================================================== */

  function buildOpenUri(path, line) {
    var uri = 'hirake://open?path=' + encodeURIComponent(path);
    if (typeof line === 'number' && line >= 1) {
      uri += '&line=' + line;
    }
    return uri;
  }

  // ホスト側の受け取り上限。これを超える分は行単位で削り、
  // 注意書き（未走査・未記録・未読）は必ず残す。文字の途中で切ると
  // 「上限に達したことを伝える」という約束のほうが先に消える。
  var MAX_COPY_CHARS = 1000000;

  // 壊れた箇所をそのまま外部エディタや AI へ渡せるよう、
  // 「開くための URI + 場所 + 内容」を 1 行 1 件の TSV にする。
  function buildCopyText() {
    var header;
    var rows = [];
    if (modeSelect.value === 'orphan') {
      // 「この走査条件のもとで被リンク 0」であることを、渡した先でも分かるようにする。
      header = '# 孤立ページ（被リンク 0）\t' + data.root
        + '\t上限 ' + (data.maxFiles || 500) + ' ファイル・' + (data.maxDepth || 32) + ' 階層';
      visibleOrphans().forEach(function (orphan) {
        rows.push(buildOpenUri(orphan.path) + '\t' + orphan.rel + '\t被リンク 0');
      });
    } else {
      header = '# リンク切れ\t' + data.root;
      brokenList.forEach(function (item) {
        rows.push(buildOpenUri(item.src, item.line)
          + '\t' + item.rel + ':' + item.line
          + '\t' + item.target
          + '\t' + (REASON_TEXT[item.reason] || item.reason));
      });
    }

    var notes = [];
    if (data.truncated) {
      notes.push('# 注意: ファイル数の上限に達したため一部未走査');
    }
    if (data.brokenCapped) {
      notes.push('# 注意: 件数の上限に達したため一部未記録');
    }
    if (data.unreadableCount > 0) {
      notes.push('# 注意: ' + data.unreadableCount + ' ファイルは中身を見ていません');
    }
    if (data.skippedDirectoryCount > 0) {
      notes.push('# 注意: ' + data.skippedDirectoryCount + ' フォルダは中を見ていません');
    }

    // 注意書きと「省略した」行のぶんを先に確保してから本文を詰める。
    var reserved = header.length + 2;
    notes.forEach(function (n) { reserved += n.length + 2; });
    reserved += 60; // 省略を伝える 1 行の見込み

    var kept = [];
    var used = 0;
    for (var i = 0; i < rows.length; i++) {
      if (used + rows[i].length + 2 > MAX_COPY_CHARS - reserved) {
        break;
      }
      kept.push(rows[i]);
      used += rows[i].length + 2;
    }

    var lines = [header].concat(kept);
    if (kept.length < rows.length) {
      lines.push('# 注意: 文字数の上限に達したため ' + (rows.length - kept.length) + ' 件を省略');
    }
    return lines.concat(notes).join('\r\n');
  }

  // 連打しても元の文言を見失わないよう、ラベルは最初に 1 度だけ覚える。
  var copyLabel = copyButton ? copyButton.textContent : '';
  var copyResetTimer = null;

  function showCopyState(text, holdMs) {
    copyButton.textContent = text;
    if (copyResetTimer !== null) {
      clearTimeout(copyResetTimer);
    }
    copyResetTimer = setTimeout(function () {
      copyResetTimer = null;
      copyButton.textContent = copyLabel;
    }, holdMs);
  }

  function copyList() {
    var text = buildCopyText();
    postToHost({ type: 'copyText', text: text });

    // 結果はホストが __mdvCopyResult で返す。返って来なかった場合に
    // 「コピー中…」のまま固まらないよう、時間で元へ戻す。
    showCopyState('コピー中…', 4000);
  }

  // ホスト → WebView。'ok' / 'truncated' / 'failed'。
  // 成功と決めつけて表示すると、貼り付けるまで失敗に気づけない。
  window.__mdvCopyResult = function (result) {
    if (result === 'failed') {
      showCopyState('コピーできませんでした', 2500);
    } else if (result === 'truncated') {
      showCopyState('一部だけコピーしました', 2500);
    } else {
      showCopyState('コピーしました', 1200);
    }
  };

  /* ==========================================================
   * ツールバー
   * ========================================================== */

  function setupToolbar() {
    modeSelect.addEventListener('change', render);
    excludeCheck.addEventListener('change', render);
    copyButton.addEventListener('click', copyList);
  }

  /* ==========================================================
   * テーマ
   * ========================================================== */

  function setTheme(theme) {
    document.documentElement.setAttribute(
      'data-theme', theme === 'dark' ? 'dark' : 'light');
  }

  window.__mdvSetTheme = setTheme;

  /* ==========================================================
   * ショートカット転送（他の仮想ビューと同じセット）
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
    // 描画とショートカット転送は互いに独立させ、
    // 片方が失敗してももう片方が機能するようにする。
    safeRun(setupShortcuts);
    safeRun(function () {
      setupToolbar();
      renderNotes();
      render();
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
