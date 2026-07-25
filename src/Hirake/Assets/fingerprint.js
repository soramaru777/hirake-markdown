/*
 * Hirake - fingerprint.js
 *
 * 文書指紋・類似検出ビューの描画・フィルタ切替を担当する。
 * データはテンプレート埋め込みの window.__fingerprintData
 * （{ root, folderLabel, truncated, groups:[{exact, preview,
 *    members:[{path, name, rel, line}]}] }）を用い、
 * フィルタ（類似含む / 完全一致のみ）はすべてブラウザ内で完結する。
 *
 * ホスト（WPF）との契約:
 *   WebView→ホスト postMessage:
 *     { type:'openFile', path:<string>, line:<number> }  メンバークリック（該当行へジャンプ）
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

  var data = window.__fingerprintData || { groups: [], truncated: false };

  var filterSelect = document.getElementById('fp-filter');
  var summaryEl = document.getElementById('fp-summary');
  var resultsEl = document.getElementById('fp-results');
  var emptyEl = document.getElementById('fp-empty');

  var PREVIEW_MAX = 240;

  function buildGroupCard(group, members, exactView) {
    var card = document.createElement('div');
    card.className = 'fp-group';

    var header = document.createElement('div');
    header.className = 'fp-group-header';

    // 「完全一致のみ」表示では完全一致対のメンバーだけを出すためバッジも完全一致。
    var isExact = exactView || group.exact;
    var badge = document.createElement('span');
    badge.className = 'fp-badge ' + (isExact ? 'exact' : 'similar');
    badge.textContent = isExact ? '完全一致' : '類似';
    header.appendChild(badge);

    var count = document.createElement('span');
    count.className = 'fp-group-count';
    count.textContent = members.length + ' 箇所';
    header.appendChild(count);

    card.appendChild(header);

    var preview = document.createElement('div');
    preview.className = 'fp-group-preview';
    preview.textContent = group.preview.length > PREVIEW_MAX
      ? group.preview.slice(0, PREVIEW_MAX) + '…'
      : group.preview;
    card.appendChild(preview);

    members.forEach(function (member) {
      var row = document.createElement('div');
      row.className = 'fp-member';
      row.title = 'クリックで該当行へジャンプ';

      var line = document.createElement('span');
      line.className = 'fp-member-line';
      line.textContent = 'L' + member.line;
      row.appendChild(line);

      var name = document.createElement('span');
      name.className = 'fp-member-name';
      name.textContent = member.name;
      row.appendChild(name);

      if (member.rel !== member.name) {
        var rel = document.createElement('span');
        rel.className = 'fp-member-rel';
        rel.textContent = member.rel;
        rel.title = member.rel;
        row.appendChild(rel);
      }

      row.addEventListener('click', function () {
        postToHost({ type: 'openFile', path: member.path, line: member.line });
      });

      card.appendChild(row);
    });

    return card;
  }

  function render() {
    resultsEl.textContent = '';

    var exactOnly = filterSelect.value === 'exact';
    var shown = 0;
    var totalPlaces = 0;

    data.groups.forEach(function (group) {
      // 「完全一致のみ」では、類似連鎖で exact でなくなったグループでも
      // 完全一致対（exactDup メンバー）を含めば、その対だけを表示する
      // （真のコピペが類似に吸収されて見えなくなるのを防ぐ）。
      var members = group.members;
      if (exactOnly) {
        if (!group.hasExactPair) {
          return;
        }
        members = group.members.filter(function (m) { return m.exactDup; });
        if (members.length < 2) {
          return;
        }
      }
      shown++;
      totalPlaces += members.length;
      resultsEl.appendChild(buildGroupCard(group, members, exactOnly));
    });

    // 要約と空状態。0 件でも打ち切り時はその旨を必ず示す。
    var truncatedNote = '上限到達のため一部未走査';
    if (shown === 0) {
      summaryEl.textContent = data.truncated ? truncatedNote : '';
      emptyEl.textContent = data.truncated
        ? '重複は見つかりませんでした（' + truncatedNote + '）'
        : '重複は見つかりませんでした';
      emptyEl.className = 'visible';
    } else {
      var summary = shown + ' グループ（' + totalPlaces + ' 箇所）';
      if (data.truncated) {
        summary += ' / ' + truncatedNote;
      }
      summaryEl.textContent = summary;
      emptyEl.className = '';
    }
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
   * ショートカット転送（structure.js と同じセット + 自ビュー切替）
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
      filterSelect.addEventListener('change', render);
      render();
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
