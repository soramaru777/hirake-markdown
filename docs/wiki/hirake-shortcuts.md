---
title: キーボードショートカット一覧
type: entity
project: hirake
scope: shared
sources:
  - README.md
  - https://github.com/soramaru777/hirake-markdown/issues/84
related: [[hirake-external-editor]] [[hirake-viewer-features]] [[hirake-knowledge-exploration]] [[hirake-canvas]]
confidence: high
updated: 2026-08-21
---

README の一覧をそのまま保持したもの。機能の説明は各ページを参照。

| キー | 動作 | 詳細 |
| --- | --- | --- |
| Ctrl+O | Markdown ファイルを開く | |
| Ctrl+E | いま読んでいる行を外部エディタで開く | [[hirake-external-editor]] |
| Ctrl+N | 新しいウィンドウを開く | [[hirake-multi-window]] |
| Ctrl+Shift+W | ワークスペース一覧の開閉 | [[hirake-workspaces]] |
| Ctrl+W | 現在のタブを閉じる | |
| Ctrl+Tab | 次のタブへ切り替え | |
| Ctrl+Shift+Tab | 前のタブへ切り替え | |
| Ctrl+F | ページ内検索を開く | [[hirake-viewer-features]] |
| Esc | 検索を閉じる | |
| Ctrl+Shift+E | PDF にエクスポート | [[hirake-viewer-features]] |
| Ctrl+Shift+V | クリップボードのテキストを表示 | |
| Ctrl+Shift+D | テーマ切替（自動 → ライト → ダーク） | |
| Ctrl+B | ファイルツリー サイドバーの表示切替 | |
| Alt+← | 1 つ前に表示していたファイルへ戻る | [[hirake-viewer-features]] |
| Alt+↑ | ファイルツリーのルートを 1 つ上のフォルダへ | [[hirake-viewer-features]] |
| Ctrl+Shift+F | フォルダ内横断検索 | [[hirake-knowledge-exploration]] |
| Ctrl+G | ナレッジグラフ（キャンバスの全体俯瞰）を開く | [[hirake-canvas]] |
| Ctrl+Shift+S | 構造クエリビューを開く | [[hirake-knowledge-exploration]] |
| Ctrl+Shift+L | リンク切れ・孤立ページの検出ビューを開く | [[hirake-link-check]] |
| Ctrl+Shift+R | 文書指紋・類似検出ビューを開く | [[hirake-knowledge-exploration]] |
| Ctrl+Shift+T | フォルダ統計ダッシュボードを開く | [[hirake-knowledge-exploration]] |
| Ctrl+Shift+C | 無限キャンバス・モードを開く | [[hirake-canvas]] |
| Ctrl+P | 印刷（プレビュー） | |
| Ctrl+ホイール / Ctrl+0 | ズーム（拡大縮小 / 100% に戻す） | [[hirake-viewer-features]] |

## 実装上の注意

Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+O / Ctrl+E は **WebView2 にフォーカスがあると WPF 側に届かない**。`viewer.js` が `postMessage` で C# 側へ転送している。出典は `CLAUDE.md` → [[hirake-architecture]]

`Alt+←` / `Alt+↑` も同じく転送しているが、こちらは**もう一段の事情**がある。WebView2 は
既定で `Alt+←` を「自分の履歴の戻る」として扱うため、`viewer.js` 側で `preventDefault()`
してから転送している。本文は `NavigateToString` で表示していて WebView2 の履歴はほぼ空だが、
見出しへのアンカーリンクを踏むと履歴が積まれるので、この抑止が要る（ISSUE #84）。

WPF 側で `Alt` を扱うときは `KeyEventArgs.Key` が `Key.System` になり、実際のキーは
`SystemKey` に入る。`Key` だけを見ると `Left` / `Up` が取れない。
