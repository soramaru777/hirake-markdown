---
title: エディタで開く（外部エディタ連携）
type: concept
project: hirake
scope: shared
sources:
  - https://github.com/soramaru777/hirake-markdown/issues/55
  - README.md
  - src/Hirake/EditorLauncher.cs
  - src/Hirake/EditorDetector.cs
related: [[hirake-viewer-features]] [[hirake-shortcuts]] [[hirake-overview]] [[hirake-data-paths]] [[hirake-uri-scheme]]
confidence: high
updated: 2026-08-18
---

読んでいる位置から外部エディタへ移るための経路（ISSUE #55）。**編集機能は入れない**という設計判断を保ったまま、「読んでいた場所を直す」ための往復コストだけを消す。

## なぜ必要だったか

「編集 → 表示」は既に成立していた。エディタで保存すれば `FileSystemWatcher` の自動リロード（300ms デバウンス + スクロール位置復元）で Hirake が追随する。欠けていたのは **「表示 → 編集」の一方向だけ**だった。

Hirake は読んでいる位置を知っている（`DocumentTab.GetCurrentSourceLineAsync()`。「この位置へのリンクをコピー」と同じ経路）のに、その情報がエディタへまったく渡っていなかった。

## 設計の芯: コマンドラインを組み立てない

exe と**引数配列**を持ち、`ProcessStartInfo.ArgumentList` へ 1 要素ずつ積む。

```
テンプレート  ["-g", "{file}:{line}"]
        ↓ 置換は「1 要素の中だけ」。要素の境界は動かさない
実引数        ["-g", "C:\a b & 日本語.md:42"]
        ↓ ArgumentList へ 1 要素ずつ
起動          エスケープは .NET が行う
```

1 本の文字列にすると Hirake 側でコマンドラインを分解することになり、パスの空白・`&`・引用符の処理を自前で持つ羽目になる。配列なら**その問題が発生しない**（＝コマンド注入の経路も存在しない）。

`{file}` / `{line}` / `{folder}` の 3 つだけを置換し、未知の `{...}` はそのまま通す（エディタ側の記法を壊さない）。

## `UseShellExecute` は必ず false

true にすると OS の関連付けが働く。`.md` の既定プログラムは Hirake であることが多いため（`register.ps1` は既定が未設定のときだけ Hirake を既定にする）、**自分自身がもう 1 タブ開くだけ**になる。ISSUE の成功条件で明確に禁じられている状態なので、true にする経路をコード上に作らない。

同じ理由で **`.cmd` / `.bat` は起動対象にしない**。`UseShellExecute = false` では直接起動できず、`cmd.exe` を噛ませる＝シェルを経由する経路を作ることになる。

## 検出は 3 経路

**App Paths レジストリだけでは足りない。** 開発機で実測したところ、App Paths の登録は 84 件あるのに、インストール済みの VS Code もサクラエディタも含まれていなかった（エディタとして使えるのは notepad / devenv / WinMerge 程度）。一方 PATH からは `code.cmd` が引けた。

| 順序 | 経路 | 位置づけ |
|---|---|---|
| 1 | プリセットの既知インストール先 | **主力。** 実測ではここでしか見つからないものが多い |
| 2 | 環境変数 PATH | `code.cmd` はここに出るが、`.cmd` は採らないので実体を既知パスで拾う |
| 3 | App Paths レジストリ | 補助。登録しているエディタもある |

同じ実行ファイルはフルパスで重複を除く。検出は**「エディタで開く」が初めて使われたときに 1 回だけ**走らせる（起動時には走らせない）。

## プリセットは同梱 JSON

`Assets/editors.json`。**利用者が直接編集できる**ことが目的で、プリセットが間違っていても自分で直せる逃げ道になる。csproj はワイルドカードで Assets を取り込むため、ファイルを足すだけで配布物に入る。

**同梱するのは実機で確認できたものだけ**（VS Code / サクラエディタ / メモ帳）。秀丸・Notepad++・EmEditor・Sublime・gVim は検証環境が無いため `unverified` 配列に置き、候補には出さない。**動かないプリセットは、動かないうえに原因が分からないため、無い方がまだ良い。**

選んだ内容は**プリセットを参照せずコピーして** `settings.json` の `Editor` に保存する。参照にすると、同梱 JSON を書き換えたときや Hirake を更新したときに黙って挙動が変わる。

## 行ジャンプが無いエディタ

`supportsLine: false` のときは `noLineArgs`（**別の配列**）を使う。「`{line}` を含む要素を落とす」方式にはしない。落とすと `-g {file}:{line}` が `-g` だけ残って壊れる。

行が取れなかったときも同じ経路を使う。**開けないより、先頭で開く方がよい。**

## Ctrl+E は 2 経路

本文（WebView2）にフォーカスがあるのが通常なので、`viewer.js` からの転送が要る。ホスト側だけに実装すると「たまに効かない」という最も分かりにくい壊れ方になる（→ [[hirake-shortcuts]]）。

```
Ctrl+E ─┬─ WPF に届く場合      → MainWindow.Window_PreviewKeyDown
        └─ 本文にある場合      → viewer.js → postMessage
                                  → DocumentTab.OnWebMessageReceived
                                  → IDocumentTabHost.ShortcutOpenInEditor()
```

## 失敗を黙らせない

起動に失敗したら、**実行しようとした内容（exe とパス）と理由**を表示し、`Diagnostics` にも残す。何も起きないと、設定が悪いのか・エディタが入っていないのか・パスが違うのかを切り分けられない。

投げっぱなしの非同期処理（`_ = ...`）は例外を必ず受ける。受けないと未観測の例外として消え、利用者から見れば「押しても何も起きない」になる。

## セキュリティ

設定に任意のコマンドラインを書けるということは、設定ファイルを書ける相手に任意コード実行を許すことになる。ただし `settings.json` は `%LocalAppData%`（→ [[hirake-data-paths]]）にあり元からユーザー権限で書き換え可能なので、**新たな権限昇格は生まれない**。そのうえで次を守る。

- **`hirake://` からこの設定を変更する経路を作らない。** URI で任意コマンドを仕込めると外部からのリモートコード実行になる（[[hirake-uri-scheme]] の「Markdown をタブで開く以外は行わない」という契約を維持する）
- 置換は**引数として**行い、シェル経由で文字列連結しない
- `{file}` に渡すのは現在開いているファイルの実パスのみ

## 非スコープ

Hirake 本体での Markdown 編集、新規ファイル作成、エディタ設定の GUI。タスクのチェックボックス トグル（`- [ ]` ↔ `- [x]`）は「表示専用」を崩す判断になるため、別途決める。
