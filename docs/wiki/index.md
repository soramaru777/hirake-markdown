# Hirake wiki — Index

このリポジトリの wiki の目次。**ページを追加したら必ず 1 行足すこと。**
ページの規約（frontmatter・命名・置き場所）は `~/wiki/SCHEMA.md` が正。

入口は **[hirake-overview.md](hirake-overview.md)**。

## ページ

| ページ | 概要 |
|---|---|
| [hirake-overview.md](hirake-overview.md) | ハブ。Hirake が何か、設計の芯 3 点、全ページの地図 |
| [hirake-viewer-features.md](hirake-viewer-features.md) | 表示・操作・出力などビューアとしての機能 |
| [hirake-editing-boundary.md](hirake-editing-boundary.md) | **なぜ**書き換えないか。外部エディタへ渡す・壊れた箇所を見せる・AI へ受け渡す（#56 のみ未実装） |
| [hirake-knowledge-exploration.md](hirake-knowledge-exploration.md) | 意味検索・OCR・グラフ・構造クエリ・指紋・統計。完全オフラインの前提 |
| [hirake-external-editor.md](hirake-external-editor.md) | 「エディタで開く」。読んでいる行を外部エディタへ渡す仕組み、検出の 3 経路、コマンドラインを組み立てない理由 |
| [hirake-link-check.md](hirake-link-check.md) | リンク切れ・孤立ページの検出。捨てていた未解決リンクを残す設計、壊れた wikilink の拾い方、黙って 0 件と言わない |
| [hirake-wikilinks.md](hirake-wikilinks.md) | `[[wikilink]]` 記法。LinkInline に載せる設計、名前解決と同名衝突、誤爆させない受理条件 |
| [hirake-directory-links.md](hirake-directory-links.md) | シンボリックリンクで束ねたフォルダの走査。既定で辿らない理由、安全側の作り、脅威モデルの範囲 |
| [hirake-canvas.md](hirake-canvas.md) | 無限キャンバス・モード（Ctrl+Shift+C）とナレッジグラフの関係 |
| [hirake-workspaces.md](hirake-workspaces.md) | 1 ワークスペース = 1 ウィンドウ。保存されるもの／されないものと理由 |
| [hirake-multi-window.md](hirake-multi-window.md) | ウィンドウ単位とアプリ全体の切り分け、セッション復元 |
| [hirake-uri-scheme.md](hirake-uri-scheme.md) | `hirake://open` / `hirake://workspace` の文法と fail-closed 方針 |
| [hirake-file-association.md](hirake-file-association.md) | register / unregister の契約、他アプリを壊さない規約、テスト |
| [hirake-distribution.md](hirake-distribution.md) | インストーラ、管理者権限不要、自己完結発行の理由、SmartScreen、リリース手順とタグの決まり |
| [hirake-build.md](hirake-build.md) | ビルドコマンドと動作要件 |
| [hirake-data-paths.md](hirake-data-paths.md) | `%LocalAppData%` 配下のデータ配置 |
| [hirake-shortcuts.md](hirake-shortcuts.md) | キーボードショートカット一覧 |
| [hirake-third-party.md](hirake-third-party.md) | MIT と同梱ライブラリのライセンス |

## まだ書いていないページ（リンク切れ = 次に書く候補）

- `hirake-architecture.md` — C# 側（ホスト）と Assets 側（ブラウザ内）の分離、仮想ホスト（`assets.hirake` / `doc.hirake` / `temp.hirake`）、タブ = WebView2 の構造。出典は `CLAUDE.md`
