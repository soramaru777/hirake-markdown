# MdViewer

Windows 用の軽量な Markdown ビューアです。WPF + WebView2 で構築されており、Markdown ファイルをダブルクリックするだけで素早く閲覧できます。

## 概要

MdViewer は、エディタでの編集を目的とせず「素早く正確に Markdown を表示すること」に特化したビューアアプリケーションです。GitHub 風のスタイルでシンタックスハイライトや mermaid 図を含む Markdown を、ネイティブアプリのように高速に表示します。

## 機能一覧

- **タブ表示**: 複数の Markdown ファイルをタブで切り替えて閲覧できます
- **シンタックスハイライト**: コードブロックを highlight.js（GitHub テーマ）で色分け表示します
- **mermaid 図の描画**: フローチャートなどの mermaid コードブロックを図として描画します
- **目次（TOC）**: 見出しから目次を自動生成し、サイドバーから各見出しへジャンプできます
- **ページ内検索（Ctrl+F）**: 表示中のページ内をインクリメンタル検索できます
- **外部リンクはブラウザで開く**: `http(s)://` へのリンクはアプリ内ではなく既定のブラウザで開きます
- **自動リロード**: 表示中のファイルが更新されると自動的に再読み込みします
- **二重起動時は既存ウィンドウに新タブ**: 別の Markdown ファイルを開いたときに新しいウィンドウを増やさず、既存のウィンドウに新しいタブとして追加します

## ビルド方法

.NET 10 SDK がインストールされた環境で、以下のコマンドを実行してください。

```powershell
dotnet publish src/MdViewer -c Release -r win-x64 --self-contained false -o publish
```

ビルド成果物は `publish` フォルダに出力されます（`publish\MdViewer.exe`）。

## .md 関連付け手順

`.md` / `.markdown` ファイルを MdViewer で開けるようにするには、`scripts/register.ps1` を実行します。管理者権限は不要です（現在のユーザー = HKCU にのみ登録します）。

```powershell
# 既定の exe パス（publish または bin\Release）を自動検出して登録する場合
.\scripts\register.ps1

# exe のパスを明示的に指定する場合
.\scripts\register.ps1 -ExePath "C:\Tools\MdViewer\MdViewer.exe"
```

登録すると、以下が行われます。

- `MdViewer.md` という ProgId を HKCU に登録
- `.md` / `.markdown` の「プログラムから開く」候補に MdViewer を追加
- `.md` の既定プログラムが未設定の場合のみ MdViewer を既定に設定（既に別アプリが既定になっている場合は上書きしません）

> **注意**: Windows 11 では初回のみ、`.md` ファイルを右クリック →「プログラムから開く」→「別のプログラムを選択」から MdViewer を選び、「常にこのアプリを使う」にチェックを入れる操作が必要な場合があります。

## 関連付けの解除手順

登録した関連付けを削除するには、`scripts/unregister.ps1` を実行します。

```powershell
.\scripts\unregister.ps1
```

register.ps1 が作成した ProgId・OpenWithProgIds のエントリを削除します。`.md` の既定プログラムは、MdViewer 自身が設定した場合のみ削除され、他アプリが既定になっている場合は変更しません。

## キーボードショートカット

| キー | 動作 |
| --- | --- |
| Ctrl+O | Markdown ファイルを開く |
| Ctrl+W | 現在のタブを閉じる |
| Ctrl+Tab | 次のタブへ切り替え |
| Ctrl+Shift+Tab | 前のタブへ切り替え |
| Ctrl+F | ページ内検索を開く |
| Esc | 検索を閉じる |

## 動作要件

- Windows 10 / Windows 11
- .NET 10 Desktop Runtime
- WebView2 Runtime（Windows 11 には標準搭載。Windows 10 では別途インストールが必要な場合があります）
