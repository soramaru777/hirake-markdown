---
title: .md 関連付けの登録と解除
type: howto
project: hirake
scope: shared
sources:
  - README.md
related: [[hirake-uri-scheme]] [[hirake-distribution]] [[hirake-overview]]
confidence: high
updated: 2026-08-14
---

`.md` / `.markdown` を Hirake で開けるようにする手順と、その契約。**インストーラを使った場合は不要**（ウィザードで登録済み → [[hirake-distribution]]）。ソースからビルドした場合や、インストール時に関連付けを外した場合に使う。

## 実行

```powershell
# 既定の exe パス（publish または bin\Release）を自動検出して登録
.\scripts\register.ps1

# exe のパスを明示的に指定
.\scripts\register.ps1 -ExePath "C:\Tools\Hirake\Hirake.exe"

# 解除
.\scripts\unregister.ps1
```

**管理者権限は不要**（現在のユーザー = HKCU にのみ登録する）。

両スクリプトはレジストリ操作と手順を `scripts/association-lib.ps1` に集約しており、**単体では動作しない。3 ファイルを同じフォルダに置く**こと。

## register が行うこと

- `Hirake.md` という ProgId を HKCU に登録
- `.md` / `.markdown` の「プログラムから開く」候補に Hirake を追加
- **`.md` の既定プログラムが未設定の場合のみ** Hirake を既定に設定
- `hirake://` の URL プロトコルを登録（→ [[hirake-uri-scheme]]）

**他アプリの設定は変更しない。** 既に別アプリが `.md` の既定になっている環境では、Hirake は「プログラムから開く」候補にのみ追加される。他アプリが登録した既定プログラムや `OpenWithProgIds` の候補には触らない。

> Windows 11 では初回のみ、`.md` を右クリック →「プログラムから開く」→「別のプログラムを選択」から Hirake を選び「常にこのアプリを使う」にチェックする操作が必要な場合がある。

## unregister の契約（重要）

**どの値を自分が作ったかは記録していない。** 契約は「**現時点で存在する Hirake の関連付けをすべて解除する**」こと。したがって:

- 実行前から Hirake の関連付けがあった場合、それも解除される
- 実行前から空だった `OpenWithProgIds` / 拡張子キーも掃除の対象になる
- Hirake が追加した値を除去し、その結果として空になった関連キーも削除する
- `.md` の既定プログラムは `Hirake.md` になっている場合のみ削除する。他アプリが既定なら変更しない
- 他アプリが登録した `OpenWithProgIds` のエントリが残っていれば、そのキーは削除しない

### 別アプリ誤削除のガード

`Hirake.md` ProgId と `hirake://` プロトコルのキーは、`shell\open\command` の実行ファイル名が `Hirake.exe` であることを確認してから削除する。確認できない場合（別の実行ファイルを指している / `shell\open\command` が無い）は、**レジストリを 1 つも変更せずに終了コード 1 で中止**する。

> 確認しているのは実行ファイルの**名前だけで、パスは見ていない**。別アプリの実行ファイルもたまたま `Hirake.exe` という名前であれば Hirake のものと判定される。

## 失敗時の扱い

どちらも失敗時は終了コード 1 で終わる。**値ごとのロールバックは行わない**ため、その時点で一部の変更が適用済みのことがある。原因を解消して再実行すればよい（両スクリプトとも何度実行しても同じ結果になる = 冪等）。

## テスト

`tests/` にリグレッションテストがある。**Pester には依存しない**（追加インストール不要）。

```powershell
# レジストリ操作の関数単体
.\tests\test-association-lib.ps1

# 登録・解除の制御フロー（呼ぶ順序、どの失敗で何をやめるか、表示と終了コード）
.\tests\test-association-flow.ps1
```

**実際の `.md` / `.markdown` / `Hirake.md` / `hirake` には一切触れない。** 実行ごとに一意な使い捨ての ProgId・URL スキーム・拡張子だけを使い、終了時に削除する。
