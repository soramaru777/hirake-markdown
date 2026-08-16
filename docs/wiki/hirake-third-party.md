---
title: ライセンスとサードパーティライブラリ
type: entity
project: hirake
scope: shared
sources:
  - README.md
related: [[hirake-build]] [[hirake-viewer-features]] [[hirake-distribution]]
confidence: high
updated: 2026-08-14
---

Hirake 本体は **MIT License**（Copyright (c) 2026 soramaru777）。

## 同梱・利用しているライブラリ

| ライブラリ | 用途 | ライセンス |
| --- | --- | --- |
| [Markdig](https://github.com/xoofx/markdig) | Markdown → HTML 変換 | BSD-2-Clause |
| [Microsoft.Web.WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) | HTML レンダリング（SDK） | Microsoft Software License Terms |
| [highlight.js](https://github.com/highlightjs/highlight.js)（github / github-dark テーマ含む） | シンタックスハイライト | BSD-3-Clause |
| [mermaid](https://github.com/mermaid-js/mermaid) | 図の描画 | MIT |
| [KaTeX](https://github.com/KaTeX/KaTeX)（同梱フォント含む） | 数式レンダリング | MIT |

**highlight.js / mermaid / KaTeX は `src/Hirake/Assets/vendor/` にミニファイ済みファイルを同梱している**（CDN から取得しない）。完全オフラインで動くという [[hirake-overview]] の前提と一貫している。

各ライセンスの全文は `THIRD-PARTY-NOTICES.md` を参照。

> MIT は撤回できない。既に公開したバージョンのライセンスを後から変更しても、公開済みのコードには遡及しない。配布形態を変える議論（→ [[hirake-distribution]]）でここが制約になる。
