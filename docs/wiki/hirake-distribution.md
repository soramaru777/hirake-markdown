---
title: 配布とインストール
type: howto
project: hirake
scope: shared
sources:
  - README.md
  - https://github.com/soramaru777/hirake-markdown/issues/2#issuecomment-5290707888
  - https://github.com/soramaru777/hirake-markdown/issues/57
  - .github/workflows/release.yml
  - https://github.com/soramaru777/hirake-markdown/issues/66
  - https://github.com/soramaru777/hirake-markdown/issues/70
  - https://github.com/soramaru777/hirake-markdown/issues/76
  - https://github.com/soramaru777/hirake-markdown/issues/80
related: [[hirake-build]] [[hirake-file-association]] [[hirake-data-paths]]
confidence: high
updated: 2026-08-20
---

利用者向けの配布形態は **GitHub Releases に置く単一の `HirakeSetup-x.y.z.exe`**（Inno Setup 製）。

**2026-08-16 に v1.0.0 を公開した**（初回リリース）。#40 で作った経路が実際に通ったのはこれが最初。

## インストール

[Releases](https://github.com/soramaru777/hirake-markdown/releases) から `HirakeSetup-x.y.z.exe` をダウンロードして実行する。

- **管理者権限は不要。** `%LocalAppData%\Programs\Hirake` にユーザー単位でインストールする
- **.NET ランタイムの事前導入も不要。** インストーラに同梱している
- `.md` の関連付けとスタートメニューのショートカットは、ウィザードのチェックボックスで選べる

アンインストールは「設定 > アプリ > インストールされているアプリ」から。

### なぜ .NET を同梱する（自己完結発行）か

**.NET 10 Desktop Runtime の公式インストーラが管理者権限を要求する**ため。フレームワーク依存のままでは「ダウンロードして実行するだけ」が成立せず、管理者権限不要という前提が崩れる。同梱の代償としてインストーラのサイズが増える。

（この理由は README には書かれていない。出典は ISSUE #2 の進捗コメント）

### 関連付けはインストーラが自前で書かない

インストーラはレジストリを直接書かず、既存の `register.ps1` / `unregister.ps1` を呼ぶ。関連付けのロジックを二重実装しないため。→ [[hirake-file-association]]

## 初回起動時の SmartScreen 警告

**インストーラにコード署名がない**ため、「Windows によって PC が保護されました」という青い画面が出る。**「詳細情報」→「実行」** で続行できる。

**この表示は署名証明書を取得するまで消せない。** 回避策ではなく、現時点の既知の制約として扱う。

## リリースの手順と決まりごと

### PR の段階で分かること

develop / main への PR では `pr-build.yml` が走り、**インストーラができるところまで**確かめる（ISSUE #70）。所要は約 2 分半。

- 関連付けスクリプトのテスト / 配布スクリプトの 5.1 検査
- 自己完結発行と成果物の検証
- **Inno Setup の取得（固定 URL + SHA-256 照合）とコンパイル**

定義は `build.yml`（`workflow_call`）1 本で、リリースと PR が同じものを呼ぶ。コピーすると乖離して
「PR は通るがリリースで落ちる」状態になるため。

> **取得元が差し替えられたことに、タグを打つ前に気づける**のが要点。Inno Setup の SHA-256 照合は
> 初回のリリースが成功しても消えないリスクで、PR で回していれば事前に分かる。

### タグを打つ

`v*` タグを push すると `release.yml` が走り、インストーラを作って**下書きの** Release に添付する。**公開は手動**。

**タグは手で打たず `scripts/tag-release.ps1` に生成させる**（ISSUE #76）。

```powershell
git switch main
git pull
.\scripts\tag-release.ps1 -WhatIf   # 何をするかだけ表示する
.\scripts\tag-release.ps1
```

タグ名は `src/Hirake/Hirake.csproj` の `<Version>` から作られる。**手で書き写さない。** 打ち間違えても消せないため、写し間違いの経路自体を無くしてある。

次のいずれかに当てはまると、**タグを作らずに中止**する。

- 版が数値 3〜4 要素でない
- いま居るコミットがリリース対象ブランチ（既定 `main`）の先端でない
- 同名タグがローカルまたはリモートに既にある ＝ **版の上げ忘れ**

> **タグ名を csproj から生成すると、`verify-tag` の「タグ名と版が一致すること」は必ず成功するようになる。**
> 上げ忘れは不一致ではなく**重複**として現れるので、重複の検出がその柵を引き継ぐ。

#### 成否は git の終了コードだけで見る（#80）

v1.1.0 のリリースで、**push に成功しているのに「中止しました」と表示され、ローカルタグまで消えた**。

Windows PowerShell 5.1 は `$ErrorActionPreference = 'Stop'` のとき、**ネイティブコマンドが stderr へ 1 行書いただけで例外を投げる**（終了コードは見ない）。`git push` は成功しても `To <remote>` や資格情報ヘルパーの警告を stderr に出すため、成功が失敗として扱われていた。**#66（bash の `-e` が終了コード 1 の正常な判定を致命扱いした）と同じ種類の誤り。**

いまは git を呼ぶ間だけ `Continue` に戻し、**終了コードだけで判定する**。あわせて `2>&1` で混ざった stderr の行（`ErrorRecord`）は出力の行から除いている。混ぜたままだと `rev-parse` の結果が警告の文字列になったり、タグの重複を誤検出したりするため。

**ロールバックは「リモートに無い」と確かめてからにする。** push 失敗時にローカルタグを戻す処理が、push 成功時にも走っていた。確かめられないときは**残す**（リモートにあるタグがローカルから消える方が、余分なタグが残るより困る）。

#### `-Prefix` で作成〜push を試す

#76 では 4 パターンを確認したが、**実際に push する経路だけ試していなかった**。そこに #80 があった。

`v*` は消せないので試せない。**`-Prefix` で `v` 以外のタグ名にすれば、ルールセットの対象外のまま作成〜push を最後まで通せる。**

```powershell
.\scripts\tag-release.ps1 -Prefix tagtest-v
git push origin :refs/tags/tagtest-v1.1.0   # 後始末
git tag --delete tagtest-v1.1.0
```

`v` 以外を指定したときは「確認用のタグ」と表示し、`release.yml` が動かないことも出す。

> **確認用の接頭辞は `v` で始められない。** `-Prefix vtest` は `vtest1.1.0` ＝ **`v*` に当たる**ので、ルールセットの対象になり消せなくなる。「確認用だから消せる」と案内しながら消せないタグを作らせないよう、スクリプト側で断る。

**手で試さなくても済むよう、`scripts/test-tag-release.ps1` に残してある。** 一時フォルダに bare リポジトリを作って origin とし、`push` のときだけ stderr へ警告を出す git の shim 越しに、`tag-release.ps1` を作成〜push まで走らせる（GitHub には触れない）。**終了コードとタグの状態だけでなく、「どの理由で終わったか」と「push の経路まで実際に到達したか」も見る**（手前で落ちた実行を「rollback が効いた」と読み違えないため）。#80 の直前のスクリプトに当てると 8 件中 2 件しか通らない。

```powershell
.\scripts\test-tag-release.ps1          # 8 通りすべて
.\scripts\test-tag-release.ps1 -Case ok # 1 つだけ、出力もそのまま見る
```

守る決まりは 3 つ。

1. **タグは main のマージコミットへ打つ。** `scripts/tag-release.ps1` が既定で強制する。未マージの feature へ打った場合は CI も落ちる（#52）
   > PR でも同じビルドが走るようになったため（#70）、ここで初めて失敗する範囲は狭い。
   > この検証は、2026-08-16 まで**正しいタグでも必ず失敗していた**（#66）。GitHub Actions の `shell: bash` が
   > `-e` 付きで起動するため、「含まれない」を表す終了コード 1 でシェルごと落ちていた。原因と対処は
   > `~/wiki/knowledge/github-actions-shell-bash-errexit.md`
2. **`v*` タグは打ち直せない・消せない。** ルールセット `protect-release-tags` が update と deletion を拒否する（#57）。**打ち間違えたら、そのタグは残したまま次のパッチ版へ進む**
3. **公開は下書きを確認してから。** `publish-release` が作るのは下書きまで

### 打つ先は main のマージコミット

**develop の先端には打たない。** Releases に載る成果物と main の内容を一致させるため。実際の `v1.0.0` も `a36761a`（develop からのマージ）に打たれている。

> **CI はここまで縛っていない。** `verify-tag` が見るのは「main **または** develop に含まれること」＝**未マージのコミットでないこと**（#52）だけで、develop の先端に打っても通ってしまう。
>
> つまり**運用ルールの方が CI より厳しい**。守っているのは `scripts/tag-release.ps1` の既定（`-Branch main`）とこの手順書であって、CI ではない。`-Branch develop` は残してあるが、通常の運用では使わない。

### これは権限の分離ではない

ルールセットは「うっかりを防ぐ柵」であって、権限の制御ではない。**リポジトリは個人アカウントの単独所有で、ルールセット自体を編集・削除できるのはタグを打つ本人と同じ**であるため。

得られるのは次の 2 つ。

- `--force` や `--delete` が事故で通らない
- 「打ち直さない運用である」ことが設定として残り、後から読んだ人にも伝わる

「権限で塞いだ」と記録すると実態より強い保証だと誤解されるため、この区別を残しておく。

> `v*` 以外のタグ（作業用の目印など）は対象外で、従来どおり自由に作成・削除できる。

### ルールセットが効いているか確かめる

2026-08-16 に `v0.0.0-ruleset-test` で実施し、5 項目すべて期待どおりだった。設定を変えたときは同じ手順で確かめる。

| 操作 | 期待 |
|---|---|
| `v*` テストタグの push | 成功（`creation` は制限していない） |
| `git push --force` | 拒否 `Cannot update this protected ref.` |
| `git push --delete` | 拒否 `Cannot delete this tag` |
| `v*` 以外のタグの作成・削除 | どちらも成功 |
| Actions | **失敗する**（下記） |

**設定内容だけなら push せずに読める。** 実地の拒否確認が要らないときはこれで足りる。

```powershell
gh api repos/soramaru777/hirake-markdown/rulesets/20899125 `
  --jq '{enforcement, bypass: (.bypass_actors|length), rules: [.rules[].type]}'
```

#### 後始末に一手間かかる

**テストタグも「消せない」の対象**なので、`Enforcement status` を一時的に `Disabled` にしてから削除し、`Active` に戻す（ルールセット自体は削除しないこと）。

**戻し忘れが最大の事故ポイント。** 上の `gh api` で `enforcement` が `active` に戻っていることを必ず確認する。

#### 下書き Release はできない

テストタグの版（`0.0.0-ruleset-test`）は `csproj` の `<Version>` と一致せず、数値 3〜4 要素の形式チェックにも通らないため、`build` ジョブが落ちて `publish-release` まで到達しない。**後始末の対象が 1 つ減るので、むしろ好都合。**

> 2026-08-16 の実施時は、落ちた場所が版チェックではなく**ブランチ検証**だった。これが #66（`-e` でシェルごと終了）の発覚経路。#66 修正後は版チェックで落ちる。

## アンインストールしても残るもの

設定・ワークスペース・セマンティック索引は**残す**（再インストール時に失わないため）。完全に消す場合はアンインストール後に削除する。

```powershell
Remove-Item -Recurse -Force "$env:LocalAppData\Hirake"
```

→ [[hirake-data-paths]]

## 今後

MSIX / Microsoft Store 配布を別 ISSUE で検討中。**Store 配布にリポジトリの private 化は不要**であることを確認済みで、public のまま進める方針。
