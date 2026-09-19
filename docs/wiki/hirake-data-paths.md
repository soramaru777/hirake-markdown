---
title: データの保存先
type: entity
project: hirake
scope: shared
sources:
  - README.md
related: [[hirake-workspaces]] [[hirake-distribution]] [[hirake-architecture]] [[hirake-directory-links]] [[hirake-canvas-harness]]
confidence: medium
updated: 2026-08-25
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

## 起点を差し替える（HIRAKE_DATA_ROOT）

環境変数 `HIRAKE_DATA_ROOT` に**絶対パス**を入れて起動すると、上の表の `%LocalAppData%\Hirake` の部分がまるごとその場所に移る（`settings.json` も `canvas` も `WebView2` も）。派生する 13 か所は起点からの合成なので、差し替えは 1 か所で足りる。

```powershell
$env:HIRAKE_DATA_ROOT = 'C:\Temp\hirake-sandbox'
& "$env:LocalAppData\Programs\Hirake\Hirake.exe"
```

| 値 | 挙動 |
|---|---|
| 絶対パス | その場所を起点にする（末尾のセパレータは落として正規化する） |
| 未設定・空・空白のみ・相対パス | **黙って既定へ倒す** |

相対パスを受け付けないのは fail-safe。壊れた環境変数でカレントディレクトリ配下へデータを散らかす方が害が大きい。

用途は [[hirake-canvas-harness]]（キャンバスの CDP 検証）。ピン留めはワークスペースに属さず**アプリ全体で共有される**ため（→ [[hirake-canvas]]）、隔離せずに自動テストを回すと実利用の配置に混ざり、切り分けが手作業になる。

> 常用の設定ではない。恒久的に変えると、環境変数を持たない起動経路（インストーラが作るショートカット、`.md` の関連付け）と食い違い、「設定が消えた」ように見える。

## 書き込みの作法

`workspaces.json` の書き込みは**一時ファイル経由の原子的置換**。壊れた場合は空の一覧として起動し、**起動そのものは止めない**。

## 補足

- `%LocalAppData%\Hirake\WebView2`（WebView2 のユーザーデータ）と `%LocalAppData%\Hirake\temp`（大きな HTML の一時ファイル）も同じ配下にある。出典は `CLAUDE.md` で、README には記載がない → [[hirake-architecture]]
- MSIX 化した場合、`%LocalAppData%` はパッケージ配下へ仮想化される。移行時にここが論点になる（**未検証**）

> confidence: medium — README が明示しているのは `%LocalAppData%\Hirake` と `workspaces.json`、インストール先の 3 つ。それ以外は別出典または未検証。
