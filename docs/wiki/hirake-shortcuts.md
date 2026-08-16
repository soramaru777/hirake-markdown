---
title: キーボードショートカット一覧
type: entity
project: hirake
scope: shared
sources:
  - README.md
related: [[hirake-viewer-features]] [[hirake-knowledge-exploration]] [[hirake-canvas]]
confidence: high
updated: 2026-08-14
---

README の一覧をそのまま保持したもの。機能の説明は各ページを参照。

| キー | 動作 | 詳細 |
| --- | --- | --- |
| Ctrl+O | Markdown ファイルを開く | |
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
| Ctrl+Shift+F | フォルダ内横断検索 | [[hirake-knowledge-exploration]] |
| Ctrl+G | ナレッジグラフ（キャンバスの全体俯瞰）を開く | [[hirake-canvas]] |
| Ctrl+Shift+S | 構造クエリビューを開く | [[hirake-knowledge-exploration]] |
| Ctrl+Shift+R | 文書指紋・類似検出ビューを開く | [[hirake-knowledge-exploration]] |
| Ctrl+Shift+T | フォルダ統計ダッシュボードを開く | [[hirake-knowledge-exploration]] |
| Ctrl+Shift+C | 無限キャンバス・モードを開く | [[hirake-canvas]] |
| Ctrl+P | 印刷（プレビュー） | |
| Ctrl+ホイール / Ctrl+0 | ズーム（拡大縮小 / 100% に戻す） | [[hirake-viewer-features]] |

## 実装上の注意

Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+O は **WebView2 にフォーカスがあると WPF 側に届かない**。`viewer.js` が `postMessage` で C# 側へ転送している。出典は `CLAUDE.md` → [[hirake-architecture]]
