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
related: [[hirake-build]] [[hirake-file-association]] [[hirake-data-paths]]
confidence: high
updated: 2026-08-16
---

利用者向けの配布形態は **GitHub Releases に置く単一の `HirakeSetup-x.y.z.exe`**（Inno Setup 製）。

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

```powershell
git switch develop
git pull
git tag v1.0.0
git push origin v1.0.0
```

守る決まりは 3 つ。

1. **タグは `main` または `develop` に含まれるコミットへ打つ。** 未マージの feature へ打つと CI が落ちる（#52）。落ちたらタグの位置が誤っている
   > PR でも同じビルドが走るようになったため（#70）、ここで初めて失敗する範囲は狭い。
   > この検証は、2026-08-16 まで**正しいタグでも必ず失敗していた**（#66）。GitHub Actions の `shell: bash` が
   > `-e` 付きで起動するため、「含まれない」を表す終了コード 1 でシェルごと落ちていた。原因と対処は
   > `~/wiki/knowledge/github-actions-shell-bash-errexit.md`
2. **`v*` タグは打ち直せない・消せない。** ルールセット `protect-release-tags` が update と deletion を拒否する（#57）。**打ち間違えたら、そのタグは残したまま次のパッチ版へ進む**
3. **公開は下書きを確認してから。** `publish-release` が作るのは下書きまで

### これは権限の分離ではない

ルールセットは「うっかりを防ぐ柵」であって、権限の制御ではない。**リポジトリは個人アカウントの単独所有で、ルールセット自体を編集・削除できるのはタグを打つ本人と同じ**であるため。

得られるのは次の 2 つ。

- `--force` や `--delete` が事故で通らない
- 「打ち直さない運用である」ことが設定として残り、後から読んだ人にも伝わる

「権限で塞いだ」と記録すると実態より強い保証だと誤解されるため、この区別を残しておく。

> `v*` 以外のタグ（作業用の目印など）は対象外で、従来どおり自由に作成・削除できる。

## アンインストールしても残るもの

設定・ワークスペース・セマンティック索引は**残す**（再インストール時に失わないため）。完全に消す場合はアンインストール後に削除する。

```powershell
Remove-Item -Recurse -Force "$env:LocalAppData\Hirake"
```

→ [[hirake-data-paths]]

## 今後

MSIX / Microsoft Store 配布を別 ISSUE で検討中。**Store 配布にリポジトリの private 化は不要**であることを確認済みで、public のまま進める方針。
