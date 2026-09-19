---
title: 文書タブの CDP 検証ハーネス
type: howto
project: hirake
scope: shared
sources:
  - tests/viewer/Invoke-ViewerHarness.ps1
  - tests/viewer/cases/Case-105-MermaidTheme.ps1
  - https://github.com/soramaru777/hirake-markdown/issues/105
related: [[hirake-canvas-harness]] [[hirake-shell-harness]] [[hirake-viewer-features]] [[hirake-data-paths]]
confidence: medium
updated: 2026-09-06
---

通常の文書タブ（`viewer.js` が描く画面）の描画を、目視ではなくページ内の実測値で判定する仕組み。`tests/viewer/` にある。

俯瞰画面（`canvas.js`）は [[hirake-canvas-harness]]、WebView2 の外側は [[hirake-shell-harness]] の担当。隔離起動・前提チェック・CDP クライアント・assertion は共通層（`tests/lib/`）を 3 つで共有していて、このハーネスが足しているのは「文書タブに繋いでケースを呼ぶ」ランナーとケースだけ。

## なぜ要るか

#105（テーマ切替で mermaid 図が再描画されない）は、Markdig が出す HTML の形（`<pre class="mermaid">`）と `viewer.js` の受理条件（`<pre><code class="language-mermaid">`）の食い違いで起きた。図は mermaid 自身の自動描画が描いていたので**見た目は正常**で、テーマを切り替えて初めて分かる。この種の「経路が別物にすり替わっている」不具合は、`svg` の id（誰が描いたか）を見れば機械的に判定できる。

## 使う

```powershell
pwsh -NoProfile -File tests\viewer\Invoke-ViewerHarness.ps1
pwsh -NoProfile -File tests\viewer\Invoke-ViewerHarness.ps1 -Case 105
```

**実行前に Hirake を終了しておくこと。** 起動していると二重起動でパスが転送され、ハーネスの起動が既存プロセスへ吸われる。起動前に検出して中止する（exit 2）。既存プロセスを勝手に kill はしない。

`Hirake.exe` の探し方（`publish\` 優先）、`HIRAKE_DATA_ROOT` による隔離、隔離を知らない古いバイナリの拒否は [[hirake-canvas-harness]] と同じ。

## 何を確かめているか

| ケース | testdata | 判定 |
|---|---|---|
| #105 mermaid のテーマ追従 | `testdata/mermaid-click.md`（図 1 本・click 記法 4 本） | 下の 14 項目 |

Case-105 は `__mdvSetTheme()`（ホストが Ctrl+Shift+D で呼ぶのと同じ公開関数）で light → dark → light と切り替え、各段で次を見る。

1. `pre.mermaid` が残っていない（`viewer.js` が `div.mermaid` に正規化した）
2. `div.mermaid > svg` が図の数だけある
3. `svg.id` が `mdv-mermaid-N`（`viewer.js` 採番。mermaid の自動描画なら `mermaid-<epoch>`）
4. `svg` の中に click 記法の `<a>` が 4 本以上ある（`securityLevel: 'antiscript'` でもリンクは生成される）
5. 〜 8. その `href` が `#usage` / `sample.md` / `sub/page2.md` / `https://example.com` をそれぞれ保っている（本数だけでは、mermaid 側の書き換えや空文字化を見逃す。4 本は `onArticleAnchorClick` と C# 側 `OnNavigationStarting` の振り分け経路に対応）
9. `javascript:` の `href` が 0 本（`antiscript` の無害化。`loose` では残ることを実装時に headless Edge で実測済み）
10. `div.mermaid[data-src-line]` が図の数だけある（ソースマップ・スクロール復元の材料）
11. dark に切り替えると `svg.id` が変わる（作り直された）
12. 図形の `fill` が light と dark で異なる
13. dark 再描画後も `<a>` と `#usage` の `href` が残る
14. light に戻すと `fill` が最初の値に戻る（片道だけ効く実装を弾く）

描画完了は「`svg.id` が前回と変わった」で待つ。`mermaid.render` は非同期で、renderId は描画のたびに採番されるため、id の変化がそのまま完了の印になる。

## 設計上の割り切り

- **testdata は `testdata/` を直接使う。** 文書 1 本で足りるので、canvas のような生成物（`dense500`）は持たない
- **`Open-CanvasOverview` / `Wait-CanvasReady` を呼ばない**だけで、ランナーの流れ（前提チェック → 起動 → ターゲット待ち → ケース → 後始末）は canvas と同じ
- **テーマ切替は合成キーではなく公開関数。** `__mdvSetTheme` はホストが `Ctrl+Shift+D` で呼ぶ口そのもので、確認したい経路（`setTheme` → `renderMermaidNodes`）はそのまま通る
- 失敗時のスクリーンショットは `tests/viewer/_artifacts/`（追跡しない）

## 既知の罠

[[hirake-canvas-harness]] の表がそのまま当てはまる。#105 の実装中に見つけた pwsh 7.6 の `VoidTaskResult` 問題（`Connect-Cdp` の戻り値が配列になる）は共通層 `tests/lib/Cdp.ps1` 側で塞いだ。

> confidence: medium — Case-105 の判定式は、同じ DOM 形（`pre.mermaid`）を headless Edge に載せて修正前 FAIL / 修正後 14 項目 PASS を確認した。Hirake 実機での実走は、実装時に Hirake が起動中で二重起動の制約に掛かったため未実施（PR のテスト欄に手順を残している）。
