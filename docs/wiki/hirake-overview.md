---
title: Hirake 概要（ハブ）
type: hub
project: hirake
scope: shared
sources:
  - README.md
  - https://github.com/soramaru777/hirake-markdown/issues/54
  - https://github.com/soramaru777/hirake-markdown/issues/55
related: [[hirake-viewer-features]] [[hirake-knowledge-exploration]] [[hirake-distribution]] [[hirake-directory-links]] [[hirake-editing-boundary]]
confidence: high
updated: 2026-08-17
---

Hirake は Windows 用の **表示専用** Markdown ビューア（WPF + WebView2）。このプロジェクト wiki の入口。

## 何を目指しているか

エディタでの編集を目的とせず、「素早く正確に Markdown を表示すること」に特化する。加えて、フォルダに溜まった Markdown を**知識ベースとして横断的に探索**できることを差別化点に置いている。

**すべてローカルで完結し、文書が外部へ送信されることはない。** 埋め込みモデルは ONNX でローカル実行、OCR は Windows 標準機能を使う。社内文書を扱えることが探索機能の前提になっている。

## ページの地図

| 主題 | ページ |
|---|---|
| ビューアとしての機能 | [[hirake-viewer-features]] |
| 編集の境界（書き換えない方針と、その代わりの経路） | [[hirake-editing-boundary]] |
| 知識ベース探索（意味検索・OCR・グラフ・構造クエリ・指紋） | [[hirake-knowledge-exploration]] |
| 無限キャンバス・モード | [[hirake-canvas]] |
| シンボリックリンクで束ねたフォルダの走査 | [[hirake-directory-links]] |
| ワークスペース | [[hirake-workspaces]] |
| マルチウィンドウとセッション復元 | [[hirake-multi-window]] |
| `hirake://` ディープリンク | [[hirake-uri-scheme]] |
| `.md` 関連付け（register / unregister） | [[hirake-file-association]] |
| 配布・インストール | [[hirake-distribution]] |
| ビルドと動作要件 | [[hirake-build]] |
| データの保存先 | [[hirake-data-paths]] |
| キーボードショートカット | [[hirake-shortcuts]] |
| サードパーティライブラリとライセンス | [[hirake-third-party]] |
| 内部構造（C# 側と Assets 側の分離、仮想ホスト） | [[hirake-architecture]] |

## リポジトリ

- GitHub: `soramaru777/hirake-markdown`（public）
- ライセンス: MIT（Copyright (c) 2026 soramaru777）
- UI・コメント・ドキュメントはすべて日本語

## 設計の芯

README 全体から読み取れる一貫した方針が 3 つと、実装を進める中で加わった方針が 2 つある。ここが他の判断の前提になる。

1. **管理者権限を要求しない。** インストール先も関連付けもユーザー単位（`%LocalAppData%` / HKCU）に閉じる → [[hirake-distribution]] [[hirake-file-association]]
2. **他アプリの設定を壊さない。** 既定プログラムが他アプリなら上書きしない、他アプリの `OpenWithProgIds` は削除しない → [[hirake-file-association]]
3. **外部からの入力は fail-closed。** `hirake://` は検証に落ちたら黙って無視し、「Markdown をタブで開く」以外は一切しない → [[hirake-uri-scheme]]
4. **走査はネットワークへ出ない。** シンボリックリンクが `\\server\share` を指していても辿らず、設定で有効にした場合もローカルに限る → [[hirake-directory-links]]
5. **ファイルを書き換えない。** 編集は外部エディタか AI に委ね、Hirake は「そこへ渡す」「壊れた箇所を見せる」までを担う → [[hirake-editing-boundary]]

> このページは当初 `README.md` のみから作成した。設計の芯の 4 番目は ISSUE #54 由来、5 番目は #55 / #56 由来で、どちらも README には書かれていない（2026-08-17 更新）。実装（`src/Hirake/`）に踏み込んだ記述は [[hirake-architecture]] に分ける。
