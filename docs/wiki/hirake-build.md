---
title: ビルドと動作要件
type: howto
project: hirake
scope: shared
sources:
  - README.md
  - CLAUDE.md
related: [[hirake-distribution]] [[hirake-third-party]] [[hirake-architecture]]
confidence: high
updated: 2026-08-14
---

## ビルド

.NET 10 SDK がインストールされた環境で実行する。

```powershell
dotnet publish src/Hirake -c Release -r win-x64 --self-contained false -o publish
```

成果物は `publish\Hirake.exe`。

```powershell
# ビルドのみ
dotnet build src/Hirake

# 実行して動作確認
dotnet run --project src/Hirake -- testdata\sample.md
```

**テストプロジェクトは存在しない。** 検証は `testdata/` を使った実機確認で行う（関連付けスクリプトのみ `tests/` に PowerShell のリグレッションテストがある → [[hirake-file-association]]）。

配布用インストーラを作る手順は `installer/README.md` を参照 → [[hirake-distribution]]。

> 上のコマンドは `--self-contained false`（フレームワーク依存）で、開発時の発行用。**配布用インストーラは自己完結発行で .NET を同梱する**点が異なる → [[hirake-distribution]]

## 動作要件

| 項目 | 要件 |
|---|---|
| OS | Windows 10 バージョン 1809（10.0.17763）以降 / Windows 11、x64 |
| WebView2 Runtime | Windows 11 には標準搭載。**Windows 10 では別途インストールが必要な場合がある** |
| .NET 10 Desktop Runtime | **インストーラを使う場合は不要**（同梱）。ソースからビルドして使う場合のみ必要 |
