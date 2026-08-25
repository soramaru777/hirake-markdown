---
title: WPF シェルの UI Automation 検証ハーネス
type: howto
project: hirake
scope: shared
sources:
  - tests/shell/Invoke-ShellHarness.ps1
  - https://github.com/soramaru777/hirake-markdown/issues/96
  - https://github.com/soramaru777/hirake-markdown/issues/86
related: [[hirake-canvas-harness]] [[hirake-data-paths]] [[hirake-shortcuts]] [[hirake-build]]
confidence: medium
updated: 2026-08-26
---

WebView2 の外側（ツールバー・タブ・二重起動）を、目視ではなく UI Automation のツリーで判定する仕組み。`tests/shell/` にある。中身を見る [[hirake-canvas-harness]] と対になる。

## なぜ要るか

WebView2 の中は CDP で覗ける（→ [[hirake-canvas-harness]]）が、**WPF シェル側は CDP から一切触れない。** ここは全部目視で、実際に手戻りが出ている。

**#86 がその例。** #84 で足した「戻る」「上の階層へ」の 2 ボタンに対してハンバーガーが右へ来ており、publish 版を触って初めて気づいた。ツールバーの子要素の順序は UI Automation のツリーに素直に出るので、**このハーネスがあれば PR の時点で分かった。**

## 使う

```powershell
pwsh -NoProfile -File tests\shell\Invoke-ShellHarness.ps1
pwsh -NoProfile -File tests\shell\Invoke-ShellHarness.ps1 -Case 86   # 1 本だけ
```

**実行前に Hirake を終了しておくこと。** 起動していると二重起動でパスが転送され、ハーネスの起動が既存プロセスへ吸われる。起動前に検出して中止する（exit 2）。既存プロセスを勝手に kill はしない。

終了コードは 3 つに分けてある。`0` = 全て成功 / `1` = 判定に失敗 / `2` = そもそも回せなかった（exe が無い・Hirake が起動中・UI Automation が読めない）。**「回せなかった」を成功にも失敗にも混ぜない。**

## 何を確かめているか

| ケース | 判定 |
|---|---|
| `86` ツールバーの並び順 | 左 3 個（`SidebarToggleButton` → `BackButton` → `UpButton`）と右 10 個（`GraphButton` … `ThemeButton`）の AutomationId 列が期待どおり |
| `tabs` タブの切替と閉じる | 2 つ目を開くとタブが 2 になる／`SelectionItemPattern` で選択が移る／✕ でタブが 1 に戻る |
| `forward` 二重起動のパス転送 | 2 番目のプロセスが 15 秒以内に終了コード 0 で終わる／1 番目のタブが 1 → 2 に増える |

**並びは配列で固定してある。ボタンを 1 つ足すと `86` は落ちる。** これは意図した動作で、「並びを変えた」と「うっかり動いた」を区別できないため、期待値の更新をレビューで通す。直すのは `cases/Case-86-ToolbarOrder.ps1` 冒頭の 2 つの配列だけでよい。

## UI Automation で見える物／見えない物

**見えない物をはっきりさせておくのが、このページの一番の役目。**

| 見たいもの | UIA | 担当 |
|---|---|---|
| ツールバーのボタン配置・順序 | ✅ ツリーの出現順 | このハーネス |
| タブの追加・切替・閉じる | ✅ `SelectionItemPattern` / `InvokePattern` | このハーネス |
| 二重起動時のパス転送 | ✅ 2 プロセス起動して観測 | このハーネス |
| **カードの重なり（#90 の z-index）** | ❌ **UIA は描画順（paint order）を公開しない** | [[hirake-canvas-harness]]（CDP） |
| **キャンバスのノード位置（#89）** | ❌ SVG 図形はツリーにまともに出ない | [[hirake-canvas-harness]]（CDP） |

実際の使い方は **「シェルで状態を作り、CDP で中を見る」の組み合わせ**になる。そのため CDP クライアント（`tests/lib/Cdp.ps1`）は共通層に置いてあるが、**今のケースは 1 本も CDP を使わない**（UIA だけで完結する範囲に絞っている）。

## 要素の見つけ方

- **判定は AutomationId で書く。** WPF は `AutomationProperties.AutomationId` が未設定なら `x:Name` を AutomationId として公開する。ツールバーのボタン 13 個はすべて `x:Name` を持つので、**製品コードには手を入れていない**
- **Name は使わない。** `Content` が Segoe MDL2 のグリフなので、UIA の Name はグリフ 1 文字になる。日本語は `ToolTip`（UIA では **HelpText**）にある
- **タブ 1 つ = `ListBoxItem`。** 中身は `DataTemplate` なので AutomationId が無い。表示名は子の `TextBlock`（`ControlType.Text`）の Name から読み、✕ は子孫の `Button` を HelpText「タブを閉じる」で裏取りして押す
- **並び順は「同じ親か」で見ない。** WPF の Panel はツリーに出ないことがあるため、木を先行順に歩いた出現順で判定する（→ 下の「既知の罠」）
- **キー入力は合成しない。** 切り替えは `SelectionItemPattern.Select()`。フォーカスの所在で結果が変わる要素を持ち込まない（[[hirake-canvas-harness]] と同じ方針）

## 構成

```
tests/
├── lib/                      共通層（両ハーネスが dot-source する）
│   ├── Assert.ps1            Assert-Ok と集計
│   ├── Cdp.ps1               CDP 最小クライアント（シェル側は未使用）
│   └── HirakeHost.ps1        隔離起動・前提チェック・後始末
├── canvas/                   → hirake-canvas-harness
└── shell/
    ├── Invoke-ShellHarness.ps1   前提チェック → ケース → 集計 → 終了コード
    ├── lib/Uia.ps1               UIA の薄い層。Hirake を知らない
    ├── cases/                    1 ファイル 1 観点
    └── testdata/                 shell01.md / shell02.md（固定・追跡する）
```

**ハーネスが 2 つになった時点で `lib` を上へ出した。** シェル側から `..\canvas\lib\HirakeHost.ps1` を読むのは依存の向きとして逆になる。

`Uia.ps1` は **Hirake を知らない**（ProcessId からウィンドウを取る・AutomationId で探す・押す・選ぶ、だけ）。「`SidebarToggleButton` の隣は `BackButton`」のような製品知識は `cases/` に置く。例外は `Wait-HirakeMainWindow` で、これは「起動が完了したか」の判定なので `HirakeHost.ps1` にある。共通層が shell 側のファイルへ暗黙に依存しないよう、`Uia.ps1` が未読込なら理由を出して落ちる。

## データを汚さないための隔離

[[hirake-canvas-harness]] と**同じ仕組みをそのまま使う**。`HIRAKE_DATA_ROOT` に `%TEMP%` の使い捨てフォルダを渡して起動し、終了時にフォルダごと消す（→ [[hirake-data-paths]]）。起動前のバイナリ走査（`HIRAKE_DATA_ROOT` を知らない古い `publish\Hirake.exe` を通さない）と、起動後の実物確認（隔離先に WebView2 プロファイルができたか）も共通層のものが効く。

## 既知の罠

| 罠 | 対処 |
|---|---|
| **二重起動判定は隔離できない** | Mutex（`Hirake_SingleInstance_Mutex`）とパイプ（`Hirake_Pipe`）の名前が固定で、`HIRAKE_DATA_ROOT` とは無関係。`forward` ケースが成立する根拠であると同時に、**キャンバスハーネスと並列に走らせられない**制約でもある |
| **WPF の Panel は UIA ツリーに現れないことがある** | `StackPanel` / `Grid` は AutomationPeer を持たないため、子が上位へ繰り上がる。「同じ StackPanel の子である」ことを前提にしない。並び順は木を先行順に歩いた**出現順**（`Get-UiaOrderedIds`）で見て、兄弟の並びは「隣り合っているか」の確認に使う |
| タブの増減は非同期 | 別プロセス → パイプ → `Dispatcher.BeginInvoke` と渡るので、1 回数えるだけでは早すぎる。件数が期待になるまでポーリングする |
| 掴んだ要素は消えうる | タブを閉じた直後など。プロパティ読みは `Get-UiaProperty` に通し、消えていれば `$null` を返す。閉じた後は掴み直す |
| 起動直後の未描画ウィンドウ | `IsOffscreen` のウィンドウは数えない |
| 失敗時のスクリーンショットが無い | UIA に撮影の口は無い。撮るなら別途 Win32 が要るため、初回スコープには入れていない（失敗時の情報はコンソールのみ） |

## スコープ外

| 項目 | 理由 |
|---|---|
| ファイルツリー・ワークスペース ポップアップ・ダイアログ | 深いツリーを触り始めると生 UIA の記述量が効いてくる。**そこへ広げる段で FlaUI 導入を再検討する**、という順序を保つ（#96 の案2） |
| Ctrl+O / Ctrl+W などのショートカット | ファイルダイアログが開くと止まる。タブ操作は ✕ と `SelectionItemPattern` で代替できる（→ [[hirake-shortcuts]]） |
| 描画順・SVG の座標 | 原理的に UIA から見えない。[[hirake-canvas-harness]] の担当 |

## CI

`build.yml` の**キャンバスハーネスの後ろに**逐次で 1 ステップ走る。**当面は `continue-on-error: true`** で、必須ステータスチェックには入れない（`enforce-pr-source.yml` の `version-bumped` と同じ扱い）。

**外す条件は先に決めてある: PR 3 本ぶん連続で緑になったら `continue-on-error` を外す。** 期限を切らずに置くと「赤くても通る」が既定になり、回帰防止として機能しなくなる。それまでの間は、このステップが赤いこと自体が情報として残る。

並列にしないのは上の「既知の罠」のとおり。同時に走ると片方が転送側になって死ぬ。

> confidence: medium — `tests/shell/lib/Uia.ps1` の各関数は実ウィンドウ（`charmap.exe`）で素通しを確認済み。ハーネス本体は Hirake の GUI を起動するため、実走の確認は CI とローカルでの実機確認に委ねている。
