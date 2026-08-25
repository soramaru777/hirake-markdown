---
title: キャンバスの CDP 検証ハーネス
type: howto
project: hirake
scope: shared
sources:
  - tests/canvas/Invoke-CanvasHarness.ps1
  - https://github.com/soramaru777/hirake-markdown/issues/94
related: [[hirake-canvas]] [[hirake-data-paths]] [[hirake-build]]
confidence: medium
updated: 2026-08-25
---

WebView2 の中で起きる描画（[[hirake-canvas]]）を、目視ではなくページ内の実測値で判定する仕組み。`tests/canvas/` にある。

## なぜ要るか

#89（全体俯瞰の見切れ）と #90（カードの重なり）は、どちらも WebView2 内の描画が絡むため CLI では確認できず、PR のテスト欄に書けたのは `node --check` と `dotnet build` だけだった。しかも**確認条件（重なるフォルダ、俯瞰が収まらない規模）を毎回その場で用意していた**ので、直ったことは分かっても**次に壊れたときに気づけない**。

重要なのは、この 2 件は目視が必須だったわけではないこと。`getBoundingClientRect()` と `getComputedStyle().zIndex` を見れば機械的に判定できる。足りなかったのは「毎回組み直さずに済む形」と「固定された確認条件」だけだった。

## 使う

```powershell
# 500 ノードの testdata は生成物。初回と作り直しのときだけ
pwsh -NoProfile -File tests\canvas\tools\New-DenseTestdata.ps1

pwsh -NoProfile -File tests\canvas\Invoke-CanvasHarness.ps1
pwsh -NoProfile -File tests\canvas\Invoke-CanvasHarness.ps1 -Case 89   # 1 本だけ
```

**実行前に Hirake を終了しておくこと。** 起動していると二重起動でパスが転送され、ハーネスの起動が既存プロセスへ吸われる。ハーネスは起動前に検出して中止する（exit 2）。既存プロセスを勝手に kill はしない。

`Hirake.exe` は `publish\` を優先し、無ければ `src\Hirake\bin` 配下で最も新しいものを使う。`-ExePath` で明示もできる。

## 何を確かめているか

| ケース | testdata | 判定 |
|---|---|---|
| #90 カードの重なり | `overlap30`（固定・30 ファイル） | 重なる 2 枚で、ホバーしたカードが手前へ出る／クリック固定はホバーより強い |
| #89 全体俯瞰 | `wide60`（固定・60 ファイル。端に長い名前） | `__cvFitToContent()` 後、全 `g.cv-node` が `#cv-stage` に収まる（許容 0.5px） |
| #93 ズーム下限 | `dense500`（生成・500 ファイル） | `k` が 0.1 未満まで下がり、その状態でも全ノードが収まる |

判定はすべて `Runtime.evaluate` の戻り。ノードの矩形は**ラベルを含む `g.cv-node` 全体**で測る（円だけを見ると見切れを検出できない。#89 の要点そのもの）。

## 設計上の割り切り

- **依存を増やさない。** CDP は .NET 標準の `ClientWebSocket` を直接叩く。`package.json` も xUnit も持ち込まない（`tests/*.ps1` と同じ書き方に揃えている）
- **リクエスト / レスポンス同期しかしない。** イベント購読を持つと受信ループが状態を抱え、「たまに取りこぼす」形で不安定になる。待ちはすべてポーリングで表す
- **ターゲットの判別は能力で見る。** `title` / `url` ではなく、`#cv-stage` と `window.__cvFitToContent` があるかを実際に評価して見分ける。`NavigateToString` 経路の有無で URL が揺れても壊れない
- **製品コードにテスト専用の口を足さない。** ズーム率は d3 が要素へ書く `__zoom`（`d3.zoomTransform`）から読み、カードモードへの遷移は既存の `__cvSetView`（ワークスペース復元で使っている口）を使う
- **ショートカットは合成キーではなく postMessage。** `viewer.js` が Ctrl+G で送るのと同じ `{type:'shortcut', action:'toggleGraphView'}` を送る。確認したい経路（viewer.js → DocumentTab → MainWindow）はそのまま通る

## データを汚さないための隔離

`HIRAKE_DATA_ROOT` に `%TEMP%` の使い捨てフォルダを渡して起動し、終了時にフォルダごと消す（→ [[hirake-data-paths]]）。隔離先は実行のたびに画面へ出す。

**ここが一番危ない。** ピン留め（`canvas`）はワークスペースに属さず**アプリ全体で共有される**ため、隔離が効いていないと実利用の配置にテストの配置が混ざり、切り分けは手作業になる。

守りは 3 段にしてある。

| # | いつ | 何を見るか |
|---|---|---|
| 1 | CI で毎回 | **ソースの側**の解釈。`tests/test-apppaths-root.ps1` が絶対パスのみ受理・それ以外は既定へ倒すことを確認する |
| 2 | **起動する前** | **これから走るバイナリ**に `HIRAKE_DATA_ROOT` の文字列が焼き込まれているか。無ければ起動せずに中止する |
| 3 | 起動した後 | **動かした実物**が隔離先へ書いたか。隔離先に WebView2 プロファイルができなければ停止して中止する |

2 が要になる。`HIRAKE_DATA_ROOT` を知らない**古い `publish\Hirake.exe`** を掴むと、環境変数は黙って無視され、**起動した瞬間に**実利用の `%LocalAppData%\Hirake` を使い始める。後から気づいても遅い。ソースだけを見る 1 では防げず、起動後に見る 3 でも遅い。

2 が見るのは「実際に走る中身」。判定はこの順で、**言い切れないものは通さない**。

1. exe 自身にマーカーがある → それが走る中身（単一ファイル発行はここで通る）
2. 無くて、exe が 4MB を超える → 起動役（apphost）と言い切れない。**通さない**
3. 無くて、exe が小さい → 起動役と見なし、隣の `Hirake.dll` を見る

隣にどのファイルが在るかは根拠にしない。`dotnet publish` は出力先を空にしないので、**古い exe の隣に新しい publish の `Hirake.dll` と `runtimeconfig.json` が残る**配置があり得る。それを「apphost だ」と読むと、実際に走る古い exe を見ないまま通してしまう。

3 は環境変数の受け渡しが崩れた場合などに備えた網で、「効かないまま検証を続けない」ことを担保する。使った exe とその更新日時も毎回表示する。

## 既知の罠

| 罠 | 対処 |
|---|---|
| 非表示タブ（`Visibility=Collapsed`）は `setTimeout` がスロットリングされる | デバウンス 300ms を見込んで 2.5 秒待つ。評価は選択中（＝可視）のタブに限る |
| 非表示タブへの `Page.captureScreenshot` はハングする | 撮影は失敗時だけ・可視タブだけ。成功時は撮らない |
| 二重起動でパスが転送される | 起動前に既存プロセスを検出して中止。並列実行も不可 |
| ポートの取り違え | 起動前に `Listen` を確認する。`TIME_WAIT` の残骸は使用中と数えない |

## 開いている口について

`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333` は**ハーネスが起動する子プロセスにだけ**渡す。ハーネス自身のプロセス環境には残さない（設定して起動したら即座に元へ戻す）。

このポートが開いている間は、同じ PC の他プロセスからも WebView2 の中身へ届く（Chromium の DevTools は 127.0.0.1 で待つ）。開くのはハーネスの実行中だけで、対象は使い捨てのデータ領域と `tests/canvas/testdata` の文書しかない。**通常の起動でこの口が開くことはない。**

## CI

`build.yml` の発行ステップの後に 1 ステップとして走る。**当面は `continue-on-error: true`** で、必須ステータスチェックには入れない（`enforce-pr-source.yml` の `version-bumped` と同じ扱い）。ランナーの描画環境に左右されうるため、安定して緑になるのを見てから必須化を判断する。失敗時のスクリーンショットは `canvas-harness-artifacts` として残る。

> confidence: medium — CLI で完結する隔離の層（`tests/test-apppaths-root.ps1`）は実行して確認済み。ハーネス本体は GUI と WebView2 を起動するため、実走の確認は CI とローカルでの実機確認に委ねている。
