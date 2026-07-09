/*
 * MdViewer - viewer.js
 *
 * 役割:
 *   - 目次サイドバーの生成・開閉・現在地ハイライト
 *   - ページ内検索（Ctrl+F）
 *   - シンタックスハイライト（hljs）と Mermaid 図の描画
 *   - WPF 側へのショートカット転送（Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+O）
 *
 * 注意:
 *   - vendor（hljs / mermaid）が読み込めていなくても全体が死なないよう、
 *     各機能は typeof チェック + try/catch でガードしてある。
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
        console.error('[MdViewer]', err);
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
   * 目次サイドバー
   * ========================================================== */

  var tocHeadings = [];
  var tocLinkByHeadingId = Object.create(null);
  var lastActiveHeadingId = null;
  var scrollTicking = false;

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
      a.textContent = heading.textContent;

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

  function onTocClick(event) {
    var link = event.target.closest ? event.target.closest('a[data-target]') : null;
    if (!link) return;
    event.preventDefault();

    var targetId = link.dataset.target;
    var target = targetId ? document.getElementById(targetId) : null;
    if (!target) return;

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

    var probe = window.scrollY + 96; // 見出し検出のオフセット
    var activeId = tocHeadings[0].id || null;

    for (var i = 0; i < tocHeadings.length; i++) {
      var heading = tocHeadings[i];
      if (heading.offsetTop <= probe) {
        activeId = heading.id || activeId;
      } else {
        break;
      }
    }

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

  function convertMermaidBlocks() {
    if (!article) return [];

    var pres = Array.prototype.slice.call(article.querySelectorAll('pre'));
    var mermaidDivs = [];

    pres.forEach(function (pre) {
      var code = pre.querySelector('code');
      if (!code) return;
      if (!/language-mermaid/.test(code.className || '')) return;

      var div = document.createElement('div');
      div.className = 'mermaid';
      div.textContent = code.textContent;
      pre.parentNode.replaceChild(div, pre);
      mermaidDivs.push(div);
    });

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
      mermaid.initialize({ startOnLoad: false, securityLevel: 'loose' });
    } catch (err) {
      // initialize に失敗しても個別描画は試みる。
    }

    var idCounter = 0;

    nodes.forEach(function (node) {
      var source = node.textContent;
      var renderId = 'mdv-mermaid-' + (idCounter++);

      try {
        var maybePromise = mermaid.render(renderId, source);

        if (maybePromise && typeof maybePromise.then === 'function') {
          maybePromise.then(
            function (result) {
              applyMermaidResult(node, result);
            },
            function (err) {
              showMermaidError(node, err);
            }
          );
        } else {
          // 古い同期 API のフォールバック。
          applyMermaidResult(node, maybePromise);
        }
      } catch (err) {
        showMermaidError(node, err);
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

    if (!event.shiftKey && key === 'f') {
      event.preventDefault();
      openSearchBar();
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
    var mermaidNodes = [];
    safeRun(function () {
      mermaidNodes = convertMermaidBlocks();
    });

    // 2. 目次生成・シンタックスハイライトは mermaid 変換後に実施する。
    safeRun(applyHighlighting);
    safeRun(buildToc);
    safeRun(setupTocInteractions);

    // 3. Mermaid の実描画は非同期。他の初期化をブロックしない。
    safeRun(function () {
      renderMermaidNodes(mermaidNodes);
    });

    // 4. 検索UIとショートカット転送。
    safeRun(setupSearchInteractions);
    safeRun(setupShortcuts);

    // 注意: ここでスクロール位置を変更しない。
    // URL にハッシュが付いている場合はブラウザ標準の挙動に任せる。
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
