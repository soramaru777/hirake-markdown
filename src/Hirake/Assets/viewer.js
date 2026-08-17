/*
 * Hirake - viewer.js
 *
 * 役割:
 *   - 目次サイドバーの生成・開閉・現在地ハイライト
 *   - ページ内検索（Ctrl+F）
 *   - シンタックスハイライト（hljs）・Mermaid 図・KaTeX 数式の描画
 *   - 文書内 #アンカーのジャンプ制御（本文リンク / Mermaid click 記法の両方）
 *   - ライト／ダークテーマ切替（hljs CSS 切替 + Mermaid 再描画）
 *   - スクロール位置のホストへの通知（デバウンス）と復元
 *   - WPF 側へのショートカット転送
 *
 * ホスト（WPF）との契約:
 *   WebView→ホスト postMessage:
 *     { type:'scroll', y:<number> }                 スクロール位置（300ms デバウンス）
 *     { type:'shortcut', action:'closeTab' }         Ctrl+W
 *     { type:'shortcut', action:'nextTab' }          Ctrl+Tab
 *     { type:'shortcut', action:'prevTab' }          Ctrl+Shift+Tab
 *     { type:'shortcut', action:'openFile' }         Ctrl+O
 *     { type:'shortcut', action:'openInEditor' }     Ctrl+E
 *     { type:'shortcut', action:'quickPaste' }       Ctrl+Shift+V
 *     { type:'shortcut', action:'globalSearch' }     Ctrl+Shift+F
 *     { type:'shortcut', action:'toggleSidebar' }    Ctrl+B
 *     { type:'shortcut', action:'exportPdf' }        Ctrl+Shift+E
 *     { type:'shortcut', action:'cycleTheme' }       Ctrl+Shift+D
 *     { type:'shortcut', action:'toggleGraphView' }  Ctrl+G
 *     { type:'shortcut', action:'toggleStructureView' } Ctrl+Shift+S
 *     { type:'shortcut', action:'toggleFingerprintView' } Ctrl+Shift+R
 *     { type:'shortcut', action:'toggleStatsView' } Ctrl+Shift+T
 *     { type:'shortcut', action:'toggleCanvasView' } Ctrl+Shift+C
 *     { type:'shortcut', action:'toggleWorkspaceMenu' } Ctrl+Shift+W
 *     { type:'shortcut', action:'newWindow' }        Ctrl+N
 *   ホスト→WebView 公開関数:
 *     window.__mdvSetTheme('light'|'dark')  テーマ切替（スクロールは動かさない）
 *     window.__mdvRestoreScroll(y)          スクロール位置の復元（Mermaid 描画後にも再適用）
 *     window.__mdvPrint()                   印刷（UI 退避つき）
 *     window.__mdvSetBacklinks([{path,name}])         このページを参照する文書一覧
 *     window.__mdvSetRecommendations([{path,name,score}])  次に読む（関連文書レコメンド）
 *     window.__mdvShowZoomBadge(percent)    ズーム倍率バッジの一時表示（右下・自動で消える）
 *
 *   公開関数は例外を握りつぶさないこと（ISSUE #48）。ホスト側が
 *   { ok:true } / { ok:false, error } を返すラッパーで包んで呼び、失敗を
 *   診断ログへ記録する。ここで握るとホストからは常に成功に見えてしまう。
 *
 * 注意:
 *   - vendor（hljs / mermaid / KaTeX）が読み込めていなくても全体が死なないよう、
 *     各機能は typeof チェック + try/catch でガードしてある。これは維持する
 *     （機能単位のガードであり、上の「公開関数で握らない」とは別の話）。
 *   - ページ読み込み時に勝手にスクロール位置を変更しない
 *     （URL ハッシュがある場合のみブラウザ標準の挙動に任せる）。
 *   - グローバル汚染を避けるため IIFE で包む。
 */
(function () {
  'use strict';

  /* ==========================================================
   * 共通ユーティリティ
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
   * 要素参照
   * ========================================================== */

  var article = document.querySelector('.markdown-body');
  var tocToggle = document.getElementById('toc-toggle');
  var tocSidebar = document.getElementById('toc-sidebar');
  var tocContent = document.getElementById('toc-content');
  var searchBar = document.getElementById('search-bar');
  var searchInput = document.getElementById('search-input');
  var searchCountEl = document.getElementById('search-count');
  var searchPrevBtn = document.getElementById('search-prev');
  var searchNextBtn = document.getElementById('search-next');
  var searchCloseBtn = document.getElementById('search-close');

  /* ==========================================================
   * ソースマップ（Markdown ソース行 ↔ DOM の双方向対応）
   *
   * C# 側（MarkdownRenderer）が各ブロック要素に付与した data-src-line
   * （フロントマター込み原文の 1 始まり行番号）を収集し、
   * sourceToDom / domToSource の双方向 API を提供する。
   * 目次の現在位置判定・スクロール位置の保存復元もこの基盤に乗る。
   * ========================================================== */

  // {el, line} を line 昇順で保持する。行とドキュメント内位置は
  // どちらも文書の下に向かって単調増加するため、二分探索できる。
  var srcMapEntries = [];

  function collectSourceMap() {
    srcMapEntries = [];
    if (!article) return;
    var nodes = article.querySelectorAll('[data-src-line]');
    Array.prototype.forEach.call(nodes, function (el) {
      var line = parseInt(el.getAttribute('data-src-line'), 10);
      if (isFinite(line)) {
        srcMapEntries.push({ el: el, line: line });
      }
    });
    srcMapEntries.sort(function (a, b) { return a.line - b.line; });
  }

  // 要素のドキュメント座標での上端。offsetTop は offsetParent 基準のため
  // 入れ子要素（テーブルセル等）で狂う。rect ベースで統一する。
  function docTop(el) {
    return el.getBoundingClientRect().top + window.scrollY;
  }

  // items のうち topOf(item) <= probe を満たす最後のインデックスを返す（無ければ -1）。
  // 目次の現在位置判定と domToSource が共用する二分探索。
  function lastIndexAbove(items, probe, topOf) {
    var lo = 0;
    var hi = items.length - 1;
    var found = -1;
    while (lo <= hi) {
      var mid = (lo + hi) >> 1;
      if (topOf(items[mid]) <= probe) {
        found = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    return found;
  }

  // ソース行 → その行を含む（＝その行以前で最も近い）ブロック要素。
  function sourceToDom(line) {
    if (!srcMapEntries.length || !isFinite(line)) return null;
    var lo = 0;
    var hi = srcMapEntries.length - 1;
    var found = 0; // line が先頭ブロックより前なら先頭を返す
    while (lo <= hi) {
      var mid = (lo + hi) >> 1;
      if (srcMapEntries[mid].line <= line) {
        found = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    return srcMapEntries[found].el;
  }

  // DOM → ソース行。要素を渡すと自身または祖先の data-src-line、
  // 数値（ドキュメントY座標）を渡すとその位置に表示中のブロックの行を返す。
  function domToSource(target) {
    if (target && target.nodeType === 1) {
      var holder = target.closest ? target.closest('[data-src-line]') : null;
      if (!holder) return null;
      var line = parseInt(holder.getAttribute('data-src-line'), 10);
      return isFinite(line) ? line : null;
    }
    if (typeof target === 'number' && isFinite(target)) {
      var idx = lastIndexAbove(srcMapEntries, target + 12, function (entry) {
        return docTop(entry.el);
      });
      return idx >= 0 ? srcMapEntries[idx].line : null;
    }
    return null;
  }

  // 現在のスクロール位置を「ソース行 + ブロック内オフセット」で表す。
  // y はソースマップが使えない場合のフォールバック。
  function getScrollState() {
    var y = window.scrollY;
    var idx = lastIndexAbove(srcMapEntries, y + 12, function (entry) {
      return docTop(entry.el);
    });
    if (idx < 0) return { y: y, line: null, offset: 0 };
    var entry = srcMapEntries[idx];
    return { y: y, line: entry.line, offset: y - docTop(entry.el) };
  }

  // getScrollState の状態からスクロール先 Y を再計算する。
  // 呼び出し時点のレイアウトで毎回計算し直すのが肝
  // （mermaid 描画等でレイアウトが変わっても行位置に追従できる）。
  function computeStateTarget(state) {
    if (state && typeof state.line === 'number' && isFinite(state.line)) {
      var el = sourceToDom(state.line);
      if (el) {
        var offset = (typeof state.offset === 'number' && isFinite(state.offset))
          ? state.offset : 0;
        return Math.max(0, docTop(el) + offset);
      }
    }
    return (state && typeof state.y === 'number' && isFinite(state.y)) ? state.y : 0;
  }

  window.sourceToDom = sourceToDom;
  window.domToSource = domToSource;
  window.__mdvGetScrollState = getScrollState;

  /* ==========================================================
   * 目次サイドバー
   * ========================================================== */

  var tocHeadings = [];
  var tocLinkByHeadingId = Object.create(null);
  var lastActiveHeadingId = null;
  var scrollTicking = false;

  // 見出しの表示テキストを取得する。KaTeX 描画済みの見出しは .katex-mathml 内に
  // LaTeX ソース（MathML）を持つため、そのまま textContent を取ると目次が二重化けする。
  // クローンから .katex-mathml を除去し、描画済みグリフ側のテキストだけを拾う。
  function headingText(heading) {
    try {
      var clone = heading.cloneNode(true);
      var mathml = clone.querySelectorAll ? clone.querySelectorAll('.katex-mathml') : [];
      Array.prototype.forEach.call(mathml, function (el) {
        if (el.parentNode) el.parentNode.removeChild(el);
      });
      return clone.textContent;
    } catch (err) {
      return heading.textContent;
    }
  }

  function buildToc() {
    if (!article || !tocContent || !tocToggle) return;

    var headings = Array.prototype.slice.call(
      article.querySelectorAll('h1, h2, h3')
    );

    if (headings.length === 0) {
      // 見出しが無い場合はサイドバーを自動的に閉じ、トグル自体も隠す。
      document.body.classList.remove('toc-open');
      tocToggle.style.display = 'none';
      tocContent.innerHTML = '<div class="toc-empty">目次はありません</div>';
      return;
    }

    tocHeadings = headings;

    var root = document.createElement('ul');
    var stack = [{ level: 0, ul: root }];

    headings.forEach(function (heading) {
      var level = parseInt(heading.tagName.substring(1), 10);

      while (stack.length > 1 && stack[stack.length - 1].level >= level) {
        stack.pop();
      }

      var parentEntry = stack[stack.length - 1];

      var li = document.createElement('li');
      var a = document.createElement('a');
      a.textContent = headingText(heading);

      if (heading.id) {
        a.href = '#' + heading.id;
        a.dataset.target = heading.id;
        tocLinkByHeadingId[heading.id] = a;
      } else {
        a.href = 'javascript:void(0)';
      }

      li.appendChild(a);
      parentEntry.ul.appendChild(li);

      var childUl = document.createElement('ul');
      li.appendChild(childUl);
      stack.push({ level: level, ul: childUl });
    });

    tocContent.innerHTML = '';
    tocContent.appendChild(root);
  }

  // スムーススクロール中はスクロール連動の現在位置更新を止める。
  // 止めないと、移動途中の位置で判定されてクリック項目とズレる。
  var suppressScrollSync = false;
  var suppressTimer = null;

  function suppressScrollSyncDuringSmoothScroll() {
    suppressScrollSync = true;
    if (suppressTimer) clearTimeout(suppressTimer);
    suppressTimer = setTimeout(function () { suppressScrollSync = false; }, 1000);
    if ('onscrollend' in window) {
      var onEnd = function () {
        window.removeEventListener('scrollend', onEnd);
        clearTimeout(suppressTimer);
        suppressScrollSync = false;
      };
      window.addEventListener('scrollend', onEnd);
    }
  }

  function onTocClick(event) {
    var link = event.target.closest ? event.target.closest('a[data-target]') : null;
    if (!link) return;
    event.preventDefault();

    var targetId = link.dataset.target;
    var target = targetId ? document.getElementById(targetId) : null;
    if (!target) return;

    // クリックした項目を即座にアクティブ化し、スクロール完了までズレた再判定をさせない。
    setActiveHeading(targetId);
    suppressScrollSyncDuringSmoothScroll();

    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function setActiveHeading(headingId) {
    if (headingId === lastActiveHeadingId) return;

    if (lastActiveHeadingId && tocLinkByHeadingId[lastActiveHeadingId]) {
      tocLinkByHeadingId[lastActiveHeadingId].classList.remove('active');
    }

    if (headingId && tocLinkByHeadingId[headingId]) {
      tocLinkByHeadingId[headingId].classList.add('active');
    }

    lastActiveHeadingId = headingId;
  }

  function updateActiveHeadingFromScroll() {
    scrollTicking = false;
    if (!tocHeadings.length) return;
    if (suppressScrollSync) return;

    // オフセットが大きいと、短いセクションの先頭にスクロールした時に
    // 次の見出しが選ばれてしまうため、小さな余裕だけ持たせる。
    var idx = lastIndexAbove(tocHeadings, window.scrollY + 12, docTop);

    // id を持たない見出しはアクティブにできないため、直前の見出しへ遡る。
    while (idx > 0 && !tocHeadings[idx].id) idx--;

    var activeId = idx >= 0
      ? (tocHeadings[idx].id || null)
      : (tocHeadings[0].id || null);

    setActiveHeading(activeId);
  }

  function onScrollForToc() {
    if (scrollTicking) return;
    scrollTicking = true;
    window.requestAnimationFrame(updateActiveHeadingFromScroll);
  }

  function setupTocInteractions() {
    if (tocToggle) {
      tocToggle.addEventListener('click', function () {
        document.body.classList.toggle('toc-open');
      });
    }

    if (tocContent) {
      tocContent.addEventListener('click', onTocClick);
    }

    if (tocHeadings.length) {
      window.addEventListener('scroll', onScrollForToc, { passive: true });
      updateActiveHeadingFromScroll();
    }
  }

  /* ==========================================================
   * ページ内検索
   * ========================================================== */

  var searchMatches = [];
  var currentMatchIndex = -1;
  var searchDebounceTimer = null;

  function collectTextNodes(root) {
    var nodes = [];
    if (!root || !document.createTreeWalker) return nodes;

    var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        if (!node.nodeValue || !node.nodeValue.trim()) {
          return NodeFilter.FILTER_REJECT;
        }
        var parent = node.parentElement;
        if (!parent) return NodeFilter.FILTER_REJECT;
        if (parent.closest('script, style, mark.mdv-search-hit')) {
          return NodeFilter.FILTER_REJECT;
        }
        return NodeFilter.FILTER_ACCEPT;
      }
    });

    var current;
    while ((current = walker.nextNode())) {
      nodes.push(current);
    }
    return nodes;
  }

  function highlightMatchesInNode(node, lowerTerm) {
    var current = node;

    while (current) {
      var value = current.nodeValue;
      if (!value) break;

      var idx = value.toLowerCase().indexOf(lowerTerm);
      if (idx === -1) break;

      var matchNode = current.splitText(idx);
      var afterNode = matchNode.splitText(lowerTerm.length);

      var mark = document.createElement('mark');
      mark.className = 'mdv-search-hit';
      matchNode.parentNode.insertBefore(mark, matchNode);
      mark.appendChild(matchNode);

      searchMatches.push(mark);
      current = afterNode;
    }
  }

  function clearHighlights() {
    if (!article) return;

    var marks = article.querySelectorAll('mark.mdv-search-hit');
    marks.forEach(function (mark) {
      var parent = mark.parentNode;
      if (!parent) return;
      while (mark.firstChild) {
        parent.insertBefore(mark.firstChild, mark);
      }
      parent.removeChild(mark);
    });

    article.normalize();
  }

  function updateSearchCount() {
    if (!searchCountEl) return;
    if (searchMatches.length === 0) {
      searchCountEl.textContent = '0/0';
    } else {
      searchCountEl.textContent = (currentMatchIndex + 1) + '/' + searchMatches.length;
    }
  }

  function gotoMatch(newIndex) {
    if (!searchMatches.length) {
      updateSearchCount();
      return;
    }

    var idx = ((newIndex % searchMatches.length) + searchMatches.length) % searchMatches.length;

    if (currentMatchIndex >= 0 && searchMatches[currentMatchIndex]) {
      searchMatches[currentMatchIndex].classList.remove('current');
    }

    currentMatchIndex = idx;
    var el = searchMatches[currentMatchIndex];
    el.classList.add('current');
    el.scrollIntoView({ block: 'center', behavior: 'smooth' });

    updateSearchCount();
  }

  function performSearch(term) {
    clearHighlights();
    searchMatches = [];
    currentMatchIndex = -1;

    var trimmed = (term || '').trim();
    if (!trimmed || !article) {
      updateSearchCount();
      return;
    }

    var lowerTerm = trimmed.toLowerCase();
    var textNodes = collectTextNodes(article);
    textNodes.forEach(function (node) {
      highlightMatchesInNode(node, lowerTerm);
    });

    if (searchMatches.length) {
      gotoMatch(0);
    } else {
      updateSearchCount();
    }
  }

  function openSearchBar() {
    if (!searchBar || !searchInput) return;
    searchBar.classList.add('open');
    searchInput.focus();
    searchInput.select();
    if (searchInput.value) {
      performSearch(searchInput.value);
    }
  }

  function closeSearchBar() {
    if (!searchBar) return;
    searchBar.classList.remove('open');
    clearHighlights();
    searchMatches = [];
    currentMatchIndex = -1;
    updateSearchCount();
  }

  function isSearchBarOpen() {
    return !!(searchBar && searchBar.classList.contains('open'));
  }

  function setupSearchInteractions() {
    if (searchInput) {
      searchInput.addEventListener('input', function () {
        if (searchDebounceTimer) {
          clearTimeout(searchDebounceTimer);
        }
        var value = searchInput.value;
        searchDebounceTimer = setTimeout(function () {
          searchDebounceTimer = null;
          performSearch(value);
        }, 150);
      });

      searchInput.addEventListener('keydown', function (event) {
        if (event.key === 'Enter') {
          event.preventDefault();
          if (searchDebounceTimer) {
            clearTimeout(searchDebounceTimer);
            searchDebounceTimer = null;
            performSearch(searchInput.value);
          }
          if (event.shiftKey) {
            gotoMatch(currentMatchIndex - 1);
          } else {
            gotoMatch(currentMatchIndex + 1);
          }
        }
      });
    }

    if (searchPrevBtn) {
      searchPrevBtn.addEventListener('click', function () {
        gotoMatch(currentMatchIndex - 1);
      });
    }

    if (searchNextBtn) {
      searchNextBtn.addEventListener('click', function () {
        gotoMatch(currentMatchIndex + 1);
      });
    }

    if (searchCloseBtn) {
      searchCloseBtn.addEventListener('click', closeSearchBar);
    }
  }

  /* ==========================================================
   * シンタックスハイライト（highlight.js）
   * ========================================================== */

  function applyHighlighting() {
    if (typeof hljs === 'undefined' || !article) return;

    var blocks = article.querySelectorAll('pre code[class*="language-"]');
    blocks.forEach(function (block) {
      if (/language-mermaid/.test(block.className)) return;
      try {
        hljs.highlightElement(block);
      } catch (err) {
        // 個別ブロックの失敗は無視して他を継続する。
      }
    });
  }

  /* ==========================================================
   * Mermaid 図の描画
   * ========================================================== */

  // 変換済み Mermaid ノードを保持し、テーマ切替時に元ソースから再描画する。
  var mermaidNodes = [];
  var mermaidIdCounter = 0;
  var mermaidPending = 0;
  var mermaidSettledCallbacks = [];

  function isDarkTheme() {
    return document.documentElement.getAttribute('data-theme') === 'dark';
  }

  // Mermaid の全描画が完了（保留 0）したら、待機中のコールバックを呼ぶ。
  function notifyMermaidSettled() {
    var cbs = mermaidSettledCallbacks;
    mermaidSettledCallbacks = [];
    cbs.forEach(function (cb) {
      try { cb(); } catch (e) { /* 個別コールバックの失敗は無視 */ }
    });
  }

  // Mermaid の描画完了後（保留が無ければ次フレーム）に一度だけ cb を呼ぶ。
  function onMermaidSettled(cb) {
    if (mermaidPending <= 0) {
      window.requestAnimationFrame(cb);
    } else {
      mermaidSettledCallbacks.push(cb);
    }
  }

  function convertMermaidBlocks() {
    if (!article) return [];

    var pres = Array.prototype.slice.call(article.querySelectorAll('pre'));
    var mermaidDivs = [];

    pres.forEach(function (pre) {
      var code = pre.querySelector('code');
      if (!code) return;
      if (!/language-mermaid/.test(code.className || '')) return;

      var source = code.textContent;
      var div = document.createElement('div');
      div.className = 'mermaid';
      div.textContent = source;
      // テーマ切替時の再描画に備え、元ソースを保持しておく。
      div._mdvMermaidSource = source;
      // ソースマップ属性を引き継ぐ（pre 置換で失われるため）。
      // data-src-line は Markdig のコードブロックレンダラにより code 側に付く。
      var srcLine = code.getAttribute('data-src-line') || pre.getAttribute('data-src-line');
      if (srcLine) div.setAttribute('data-src-line', srcLine);
      pre.parentNode.replaceChild(div, pre);
      mermaidDivs.push(div);
    });

    // 描画対象をモジュール側にも記録する（テーマ切替で再利用）。
    mermaidNodes = mermaidDivs;
    return mermaidDivs;
  }

  function showMermaidError(node, err) {
    node.classList.remove('mermaid');
    node.classList.add('mermaid-error');
    var message = err && err.message ? err.message : String(err);
    node.textContent = 'Mermaid の描画に失敗しました: ' + message;
  }

  function renderMermaidNodes(nodes) {
    if (typeof mermaid === 'undefined' || !nodes || !nodes.length) return;

    try {
      // テーマに追従（dark / default）。initialize は再描画のたびに呼んでよい。
      mermaid.initialize({
        startOnLoad: false,
        securityLevel: 'loose',
        theme: isDarkTheme() ? 'dark' : 'default'
      });
    } catch (err) {
      // initialize に失敗しても個別描画は試みる。
    }

    nodes.forEach(function (node) {
      // 保持した元ソースを優先（再描画時は innerHTML が SVG に置換済みのため）。
      var source = node._mdvMermaidSource != null ? node._mdvMermaidSource : node.textContent;
      if (source == null) return;

      // 再描画に備えてエラー状態をリセットする。
      node.className = 'mermaid';

      var renderId = 'mdv-mermaid-' + (mermaidIdCounter++);
      mermaidPending++;
      var settled = false;
      var settle = function () {
        if (settled) return;
        settled = true;
        mermaidPending--;
        if (mermaidPending <= 0) {
          mermaidPending = 0;
          notifyMermaidSettled();
        }
      };

      try {
        var maybePromise = mermaid.render(renderId, source);

        if (maybePromise && typeof maybePromise.then === 'function') {
          maybePromise.then(
            function (result) {
              applyMermaidResult(node, result);
              settle();
            },
            function (err) {
              showMermaidError(node, err);
              settle();
            }
          );
        } else {
          // 古い同期 API のフォールバック。
          applyMermaidResult(node, maybePromise);
          settle();
        }
      } catch (err) {
        showMermaidError(node, err);
        settle();
      }
    });
  }

  function applyMermaidResult(node, result) {
    try {
      var svg = result && typeof result === 'object' && 'svg' in result ? result.svg : result;
      node.innerHTML = svg;
      if (result && typeof result.bindFunctions === 'function') {
        result.bindFunctions(node);
      }
    } catch (err) {
      showMermaidError(node, err);
    }
  }

  /* ==========================================================
   * 文書内 #アンカーのジャンプ制御（Mermaid click 記法を含む）
   * ========================================================== */

  // article 内の <a> クリックを委譲リスナーで捕捉する。mermaid の click 記法
  // （securityLevel: 'loose'）が SVG 内に生成する <a> も、本文の通常リンクも
  // 同じ経路を通る。テーマ切替の再描画で SVG が作り直されても委譲なので
  // バインドし直しは不要。
  //
  // 振り分け（「クリックで飛べる」文法を1つに揃える）:
  //   - "#見出しID" → 同一文書内スクロール。<base> がフォルダ URL を指すため、
  //     素のアンカー遷移だと doc.hirake のフォルダへページごと遷移してしまう。
  //     ここで横取りして scrollIntoView に置き換える。
  //   - それ以外（相対 .md / 外部 URL）→ 既定のナビゲーションに任せる。
  //     C# 側 OnNavigationStarting が .md は新タブ、外部 URL は既定ブラウザで開く。
  function onArticleAnchorClick(event) {
    var el = event.target;
    var anchor = el && el.closest ? el.closest('a') : null;
    if (!anchor) return;

    // SVG の <a> は href / xlink:href のどちらの属性でも表現されうる。
    var href = anchor.getAttribute('href') || anchor.getAttribute('xlink:href');
    if (!href || href.charAt(0) !== '#') return;

    event.preventDefault();

    var id = href.slice(1);
    try {
      id = decodeURIComponent(id);
    } catch (err) {
      // 不正なパーセントエンコードは生の文字列のまま検索する。
    }
    var target = id ? document.getElementById(id) : null;
    if (!target) return;

    // 目次ハイライトの挙動を目次クリック時と揃える。
    // キーはデコード済み文字列ではなく、実際に見つかった要素の id を使う
    // （href 側のパーセントエンコードと heading.id の表記ゆれを避ける）。
    if (target.id && tocLinkByHeadingId[target.id]) {
      setActiveHeading(target.id);
    }
    suppressScrollSyncDuringSmoothScroll();
    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function setupAnchorJump() {
    if (!article) return;
    article.addEventListener('click', onArticleAnchorClick);
  }

  /* ==========================================================
   * KaTeX 数式の描画
   * ========================================================== */

  // Markdig(math 拡張)は \(...\) / \[...\] を出力するため、
  // 対応するデリミタで本文全体を走査して描画する。
  // KaTeX は currentColor 基準なのでテーマ切替でも本文色を継承する。
  function renderMath() {
    if (typeof renderMathInElement === 'undefined' || !article) return;
    try {
      renderMathInElement(article, {
        delimiters: [
          { left: '$$', right: '$$', display: true },
          { left: '\\[', right: '\\]', display: true },
          { left: '\\(', right: '\\)', display: false }
        ],
        throwOnError: false,
        ignoredTags: ['script', 'noscript', 'style', 'textarea', 'pre', 'code']
      });
    } catch (err) {
      // 数式描画の失敗が他の初期化を止めないようにする。
    }
  }

  /* ==========================================================
   * テーマ切替（ホスト公開）
   * ========================================================== */

  function setHljsTheme(dark) {
    var light = document.getElementById('hljs-light');
    var darkCss = document.getElementById('hljs-dark');
    if (light) light.disabled = dark;
    if (darkCss) darkCss.disabled = !dark;
  }

  // ホストから呼ばれるテーマ切替。data-theme と hljs CSS を切替え、
  // Mermaid を全図再描画する。スクロール位置は動かさない。
  function setTheme(theme) {
    var dark = theme === 'dark';
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
    setHljsTheme(dark);
    safeRun(function () {
      renderMermaidNodes(mermaidNodes);
    });
    // KaTeX / hljs は CSS(currentColor / テーマ CSS)追従のため再描画不要。
  }

  window.__mdvSetTheme = setTheme;

  /* ==========================================================
   * バックリンク（ホスト連携）
   *
   * C# 側が依存グラフ基盤（LinkGraphService）の解析結果を
   * __mdvSetBacklinks([{path, name}]) で注入する。
   * クリックは openFile メッセージで該当文書をタブで開く。
   * ========================================================== */

  var backlinksSection = document.getElementById('backlinks-section');
  var backlinksContent = document.getElementById('backlinks-content');

  function setBacklinks(list) {
    if (!backlinksSection || !backlinksContent) return;

    backlinksContent.innerHTML = '';
    if (!list || !list.length) {
      backlinksSection.hidden = true;
      return;
    }

    var ul = document.createElement('ul');
    list.forEach(function (item) {
      if (!item || !item.path) return;
      var li = document.createElement('li');
      var a = document.createElement('a');
      a.textContent = item.name || item.path;
      a.title = item.path;
      a.href = 'javascript:void(0)';
      a.addEventListener('click', function (ev) {
        ev.preventDefault();
        postToHost({ type: 'openFile', path: item.path });
      });
      li.appendChild(a);
      ul.appendChild(li);
    });

    backlinksContent.appendChild(ul);
    backlinksSection.hidden = false;

    // 見出しの無い文書では buildToc がトグルを隠すが、
    // バックリンクがあるならサイドバーを開ける必要がある。
    if (tocToggle) tocToggle.style.display = '';
  }

  window.__mdvSetBacklinks = setBacklinks;

  /* ==========================================================
   * 次に読む（関連文書レコメンド。ホスト連携）
   *
   * C# 側が意味索引基盤（SemanticIndexService）で求めた類似文書を
   * __mdvSetRecommendations([{path, name, score}]) で注入する。
   * 0件・未提供時はセクションごと非表示。クリックはバックリンクと同じく
   * openFile メッセージで該当文書をタブで開く。
   * ========================================================== */

  var recommendSection = document.getElementById('recommend-section');
  var recommendContent = document.getElementById('recommend-content');

  function setRecommendations(list) {
    if (!recommendSection || !recommendContent) return;

    recommendContent.innerHTML = '';
    if (!list || !list.length) {
      recommendSection.hidden = true;
      return;
    }

    var ul = document.createElement('ul');
    list.forEach(function (item) {
      if (!item || !item.path) return;
      var li = document.createElement('li');
      var a = document.createElement('a');
      a.title = item.path;
      a.href = 'javascript:void(0)';

      var nameSpan = document.createElement('span');
      nameSpan.className = 'recommend-name';
      nameSpan.textContent = item.name || item.path;
      a.appendChild(nameSpan);

      if (typeof item.score === 'number' && isFinite(item.score)) {
        var scoreSpan = document.createElement('span');
        scoreSpan.className = 'recommend-score';
        scoreSpan.textContent = '類似度 ' + item.score.toFixed(2);
        a.appendChild(scoreSpan);
      }

      a.addEventListener('click', function (ev) {
        ev.preventDefault();
        postToHost({ type: 'openFile', path: item.path });
      });
      li.appendChild(a);
      ul.appendChild(li);
    });

    recommendContent.appendChild(ul);
    recommendSection.hidden = false;

    // 見出しの無い文書でもレコメンドがあればサイドバーを開けるようにする。
    if (tocToggle) tocToggle.style.display = '';
  }

  window.__mdvSetRecommendations = setRecommendations;

  /* ==========================================================
   * スクロール位置の通知・復元（ホスト連携）
   * ========================================================== */

  var scrollReportTimer = null;

  // スクロール位置を 300ms デバウンスしてホストへ通知する。
  function onScrollForHost() {
    if (scrollReportTimer) clearTimeout(scrollReportTimer);
    scrollReportTimer = setTimeout(function () {
      scrollReportTimer = null;
      postToHost({ type: 'scroll', y: window.scrollY });
    }, 300);
  }

  function setupScrollReporting() {
    window.addEventListener('scroll', onScrollForHost, { passive: true });
  }

  // ホストから呼ばれるスクロール復元。即座に scrollTo し、
  // Mermaid 描画完了（レイアウト変動後）にもう一度適用する。
  // ただしその間にユーザーが手動スクロールしたら再適用しない。
  // computeTarget はスクロール先 Y を返す関数。再適用時にも呼び直すことで、
  // レイアウト変動後の位置（ソース行ベース）に追従できる。
  function restoreScrollBy(computeTarget) {
    safeRun(function () {
      window.scrollTo(0, computeTarget());

      var cancelled = false;
      var done = false;
      var timeoutId = null;

      function cleanup() {
        window.removeEventListener('wheel', onUserScroll);
        window.removeEventListener('touchstart', onUserScroll);
        window.removeEventListener('mousedown', onUserScroll);
        window.removeEventListener('keydown', onUserKey);
        if (timeoutId !== null) {
          clearTimeout(timeoutId);
          timeoutId = null;
        }
      }
      function onUserScroll() {
        cancelled = true;
        cleanup();
      }
      function onUserKey(e) {
        var k = e.key;
        if (k === 'ArrowUp' || k === 'ArrowDown' || k === 'PageUp' ||
            k === 'PageDown' || k === 'Home' || k === 'End' ||
            k === ' ' || k === 'Spacebar') {
          onUserScroll();
        }
      }

      // 再適用 + cleanup を一度だけ実行する。onMermaidSettled とタイムアウトの
      // どちらか先着で発火する（mermaid の Promise が settle しない場合の保険）。
      function reapplyOnce() {
        if (done) return;
        done = true;
        if (!cancelled) {
          window.scrollTo(0, computeTarget());
        }
        cleanup();
      }

      window.addEventListener('wheel', onUserScroll, { passive: true });
      window.addEventListener('touchstart', onUserScroll, { passive: true });
      window.addEventListener('mousedown', onUserScroll);
      window.addEventListener('keydown', onUserKey);

      // Mermaid 描画後（レイアウト確定後）に一度だけ再適用する。
      onMermaidSettled(reapplyOnce);

      // mermaid の Promise が settle しない場合に備えた 5 秒の保険。
      timeoutId = setTimeout(reapplyOnce, 5000);
    });
  }

  // 旧来のピクセル位置ベースの復元（初回オープン時の永続化位置に使用）。
  function restoreScroll(y) {
    restoreScrollBy(function () {
      return (typeof y === 'number' && isFinite(y)) ? y : 0;
    });
  }

  // ソース行ベースの復元（自動リロード時に使用）。
  // state は __mdvGetScrollState が返した {y, line, offset}。
  function restoreScrollState(state) {
    restoreScrollBy(function () {
      return computeStateTarget(state);
    });
  }

  window.__mdvRestoreScroll = restoreScroll;
  window.__mdvRestoreScrollState = restoreScrollState;

  /* ==========================================================
   * 印刷
   * ========================================================== */

  // WebView2 の印刷プレビューはページのスクロールバー等を覆いきれず、
  // 背後のページが縁からはみ出して見える。プレビュー中だけ mdv-printing
  // クラスでスクロールバーと固定UIを隠し、閉じたら復元する。
  var printing = false;

  function endPrinting() {
    document.documentElement.classList.remove('mdv-printing');
    printing = false;
  }

  function printDocument() {
    if (printing) return;
    printing = true;

    document.documentElement.classList.add('mdv-printing');

    // クラス変更の描画反映を待ってからプレビューを開く（2フレーム待ち）。
    requestAnimationFrame(function () {
      requestAnimationFrame(function () {
        try {
          // Chromium ではプレビューを閉じるまでここでブロックする。
          window.print();
        } finally {
          endPrinting();
        }
      });
    });
  }

  // window.print() がブロックしない実装でも確実に復元するための保険。
  window.addEventListener('afterprint', endPrinting);

  // ホスト（WPF 側の印刷ボタン / Ctrl+P）から呼び出すための公開関数。
  window.__mdvPrint = printDocument;

  /* ==========================================================
   * ズーム倍率バッジ
   * ========================================================== */

  // WebView2（WPF）は HWND ベースの子ウィンドウで、WPF 要素を上に重ねられない
  // （airspace 制約）。そのため倍率表示はページ内の DOM として描画する。
  // ホストはユーザー操作由来のズーム変更時にだけ呼ぶ（起動時の倍率復元や
  // 他タブへの伝播では呼ばれない）ので、ここでは表示に専念する。

  var zoomBadgeEl = null;
  var zoomBadgeTimer = null;
  var zoomBadgeHideAt = 0;

  // バッジの表示時間（ms）。操作が続く間はタイマーを張り直す。
  var ZOOM_BADGE_DURATION = 1500;

  function ensureZoomBadge() {
    if (zoomBadgeEl && zoomBadgeEl.isConnected) {
      return zoomBadgeEl;
    }
    // body 未準備のタイミングで呼ばれても壊さない（そのズーム操作では出さない）。
    if (!document.body) {
      return null;
    }
    var el = document.createElement('div');
    el.className = 'mdv-zoom-badge';
    // 読み上げ対象にしない（.mdv-meta と同じ扱い）。
    el.setAttribute('aria-hidden', 'true');
    document.body.appendChild(el);
    zoomBadgeEl = el;
    return el;
  }

  function hideZoomBadge() {
    if (zoomBadgeTimer !== null) {
      clearTimeout(zoomBadgeTimer);
      zoomBadgeTimer = null;
    }
    zoomBadgeHideAt = 0;
    if (zoomBadgeEl) {
      zoomBadgeEl.classList.remove('is-visible');
    }
  }

  // 右下は .mdv-meta（文書メタ情報チップ）が占有しているため、その直上へ積む。
  // メタバーはウィンドウ幅で折り返して高さが変わるので、表示のたびに測り直す。
  function zoomBadgeBottom() {
    try {
      var meta = document.querySelector('.mdv-meta');
      if (meta) {
        var rect = meta.getBoundingClientRect();
        if (rect && rect.height > 0) {
          return Math.round(rect.height) + 20; // メタバー高さ + 下余白 12 + 間隔 8
        }
      }
    } catch (err) {
      // 測定できない場合は既定位置へ。
    }
    return 12;
  }

  function showZoomBadge(percent) {
    var value = Number(percent);
    if (!isFinite(value)) return;

    var el = ensureZoomBadge();
    if (!el) return;

    el.textContent = Math.round(value) + '%';
    el.style.bottom = zoomBadgeBottom() + 'px';

    // 連続操作では中身と位置だけ差し替え、再表示アニメーションを起こさない
    // （倍率を刻むたびに点滅して見えるのを防ぐ）。
    el.classList.add('is-visible');

    zoomBadgeHideAt = Date.now() + ZOOM_BADGE_DURATION;
    if (zoomBadgeTimer !== null) {
      clearTimeout(zoomBadgeTimer);
    }
    zoomBadgeTimer = setTimeout(hideZoomBadge, ZOOM_BADGE_DURATION);
  }

  // タブ切替（WebView が Collapsed）中は Chromium がタイマーを抑制することがあり、
  // 戻ってきたときに古い倍率が残って見える。再表示時に期限を検査して片付ける。
  function checkZoomBadgeExpiry() {
    if (!zoomBadgeHideAt) return;
    var remaining = zoomBadgeHideAt - Date.now();
    if (remaining <= 0) {
      hideZoomBadge();
      return;
    }
    // 期限前に戻ってきた場合は残り時間で張り直す。
    if (zoomBadgeTimer !== null) {
      clearTimeout(zoomBadgeTimer);
    }
    zoomBadgeTimer = setTimeout(hideZoomBadge, remaining);
  }

  document.addEventListener('visibilitychange', function () {
    if (!document.hidden) {
      safeRun(checkZoomBadgeExpiry);
    }
  });
  window.addEventListener('pageshow', function () {
    safeRun(checkZoomBadgeExpiry);
  });
  window.addEventListener('focus', function () {
    safeRun(checkZoomBadgeExpiry);
  });

  window.__mdvShowZoomBadge = showZoomBadge;

  /* ==========================================================
   * ショートカット転送
   * ========================================================== */

  function handleGlobalKeydown(event) {
    if (event.key === 'Escape') {
      if (isSearchBarOpen()) {
        event.preventDefault();
        closeSearchBar();
      }
      return;
    }

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
      if (key === 'c') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'toggleCanvasView' });
        return;
      }
      if (key === 'w') {
        event.preventDefault();
        postToHost({ type: 'shortcut', action: 'toggleWorkspaceMenu' });
        return;
      }
      // 未対応の Ctrl+Shift+* はブラウザ標準に委ねる。
      return;
    }

    // --- Ctrl+* （Shift なし）系 ---

    // Ctrl+B: 目次サイドバーの表示切替（実際の切替はホストが担当）。
    if (key === 'b') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'toggleSidebar' });
      return;
    }

    if (!event.shiftKey && key === 'w') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'closeTab' });
      return;
    }

    if (!event.shiftKey && key === 'o') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'openFile' });
      return;
    }

    // Ctrl+E: いま読んでいる行を外部エディタで開く（ISSUE #55）。
    // ここで転送しないと、本文にフォーカスがある通常の状態で効かない。
    // 押しっぱなしの自動リピートでは送らない（送った回数だけエディタが起動するため）。
    // preventDefault はリピートでも行う（ブラウザ既定へ漏らさない）。
    if (!event.shiftKey && key === 'e') {
      event.preventDefault();
      if (!event.repeat) {
        postToHost({ type: 'shortcut', action: 'openInEditor' });
      }
      return;
    }

    if (!event.shiftKey && key === 'n') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'newWindow' });
      return;
    }

    if (!event.shiftKey && key === 'g') {
      event.preventDefault();
      postToHost({ type: 'shortcut', action: 'toggleGraphView' });
      return;
    }

    if (!event.shiftKey && key === 'f') {
      event.preventDefault();
      openSearchBar();
      return;
    }

    // ブラウザ標準の Ctrl+P を横取りし、UI退避つきの印刷処理へ差し替える。
    if (!event.shiftKey && key === 'p') {
      event.preventDefault();
      printDocument();
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
    // 1. コードブロック中の mermaid 指定を div に変換する（構造変更）。
    //    変換結果はモジュール側 mermaidNodes に記録される（テーマ切替で再利用）。
    safeRun(function () {
      convertMermaidBlocks();
    });

    // 1.5 ソースマップ収集は DOM 構造変更（mermaid の pre→div 置換）の後に行う。
    safeRun(collectSourceMap);

    // 2. シンタックスハイライトと数式は mermaid 変換後・目次生成前に実施する。
    safeRun(applyHighlighting);
    safeRun(renderMath);
    safeRun(buildToc);
    safeRun(setupTocInteractions);

    // 3. Mermaid の実描画は非同期。他の初期化をブロックしない。
    safeRun(function () {
      renderMermaidNodes(mermaidNodes);
    });

    // 4. 検索UI・ショートカット転送・スクロール通知。
    safeRun(setupAnchorJump);
    safeRun(setupSearchInteractions);
    safeRun(setupShortcuts);
    safeRun(setupScrollReporting);

    // 注意: ここでスクロール位置を変更しない。
    // URL にハッシュが付いている場合はブラウザ標準の挙動に任せる。
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
