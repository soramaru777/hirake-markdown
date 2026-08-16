---
title: データの保存先
type: entity
project: hirake
scope: shared
sources:
  - README.md
related: [[hirake-workspaces]] [[hirake-distribution]] [[hirake-architecture]] [[hirake-directory-links]]
confidence: medium
updated: 2026-08-16
---

Hirake がユーザー単位で持つデータの置き場所。**すべて `%LocalAppData%` 配下**で、管理者権限を要さない設計と対になっている。

| パス | 内容 |
|---|---|
| `%LocalAppData%\Hirake\` | 設定・ワークスペース・セマンティック索引（**アンインストールしても残る**） |
| `%LocalAppData%\Hirake\workspaces.json` | ワークスペース一覧 → [[hirake-workspaces]] |
| `%LocalAppData%\Programs\Hirake\` | インストーラによるインストール先（プログラム本体）→ [[hirake-distribution]] |

完全に削除するコマンドは [[hirake-distribution]] を参照。

## settings.json を直接編集して変える設定

設定 UI は無い。`%LocalAppData%\Hirake\settings.json` を直接編集し、Hirake を再起動する。

| キー | 既定 | 効果 |
|---|---|---|
| `FollowDirectoryLinks` | `false` | シンボリックリンク（ジャンクション）で束ねたフォルダを走査するか → [[hirake-directory-links]] |

## 書き込みの作法

`workspaces.json` の書き込みは**一時ファイル経由の原子的置換**。壊れた場合は空の一覧として起動し、**起動そのものは止めない**。

## 補足

- `%LocalAppData%\Hirake\WebView2`（WebView2 のユーザーデータ）と `%LocalAppData%\Hirake\temp`（大きな HTML の一時ファイル）も同じ配下にある。出典は `CLAUDE.md` で、README には記載がない → [[hirake-architecture]]
- MSIX 化した場合、`%LocalAppData%` はパッケージ配下へ仮想化される。移行時にここが論点になる（**未検証**）

> confidence: medium — README が明示しているのは `%LocalAppData%\Hirake` と `workspaces.json`、インストール先の 3 つ。それ以外は別出典または未検証。
