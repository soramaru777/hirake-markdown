# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## プロジェクト概要

Hirake は Windows 用の Markdown ビューア（表示専用、編集機能なし）。WPF + WebView2 で構築され、Markdig で Markdown を HTML に変換して WebView2 に表示する。UI・コメント・ドキュメントはすべて日本語。

## ビルドコマンド

.NET 10 SDK が必要。`dotnet test` のテストプロジェクトは無く、検証は `tests/` の PowerShell に置いている
（`tests/*.ps1` は CLI で完結。`tests/canvas` は WebView2 の中を CDP で、`tests/shell` は WPF 側を
UI Automation で見る。後者 2 つは GUI を起動するので CI では `continue-on-error`）。

```powershell
# ビルド
dotnet build src/Hirake

# 発行（成果物は publish\Hirake.exe）
dotnet publish src/Hirake -c Release -r win-x64 --self-contained false -o publish

# 実行しての動作確認
dotnet run --project src/Hirake -- testdata\sample.md
```

`.md` の関連付け登録/解除は `scripts/register.ps1` / `scripts/unregister.ps1`（HKCU のみ、管理者権限不要）。

## アーキテクチャ

ソースは `src/Hirake/` に C# 4 ファイル + `Assets/`（WebView2 内で動く HTML/CSS/JS）という構成。C# 側（ホスト）と Assets 側（ブラウザ内）が仮想ホスト URL と postMessage で連携するのが全体像。

### C# 側（ホスト）

- **App.xaml.cs**: 起動処理。`App.GetEnvironmentAsync()` がアプリ全体で 1 つの `CoreWebView2Environment` を共有提供する（ユーザーデータは `%LocalAppData%\Hirake\WebView2`）。二重起動時は既存プロセスへパスを送って即終了。
- **SingleInstanceManager.cs**: 名前付き Mutex で初回判定、名前付きパイプ（1 行 = 1 パス）で 2 番目以降のプロセスから初回プロセスへファイルパスを転送。
- **MainWindow.xaml(.cs)**: タブ管理。TabControl ではなく、タブごとの WebView2 を `WebViewHost`（Grid）に全部積んで `Visibility` 切替で表示する方式。`IDocumentTabHost` を実装し、DocumentTab からのタブ操作依頼を受ける。タブの `InitializeAsync()` は選択時に遅延実行される。
- **DocumentTab.cs**: 1 タブ = 1 ファイル = 1 WebView2。レンダリング、FileSystemWatcher による自動リロード（300ms デバウンス + スクロール位置復元）、ナビゲーション制御、JS からのショートカット受信を担う。
- **MarkdownRenderer.cs**: Markdig（AdvancedExtensions + YamlFrontMatter）で変換し、`Assets/template.html` の `{{TITLE}}` / `{{BASE}}` / `{{BODY}}` を置換して完全な HTML を生成。ファイル読込は厳密 UTF-8 → 失敗時 Shift-JIS (CP932) フォールバック。

### 仮想ホストの仕組み（重要）

WebView2 の `SetVirtualHostNameToFolderMapping` で 3 つの仮想ホストを実フォルダにマップしている:

| ホスト | マップ先 | 用途 |
|---|---|---|
| `assets.hirake` | 出力先の `Assets/` | style.css / viewer.js / vendor（highlight.js, mermaid） |
| `doc.hirake` | 表示ファイルの**ドライブルート**（例 `C:\`） | 相対パス画像・md 間リンクの解決 |
| `temp.hirake` | `%LocalAppData%\Hirake\temp` | `NavigateToString` の約 2MB 制限超過時の一時 HTML |

`{{BASE}}` にはファイルのフォルダを `https://doc.hirake/...` 形式にした URL が入り、Markdown 内の相対パスは `<base>` 経由で解決される。MarkdownRenderer は HTML 中の Windows 絶対パス（`C:\...` / `file:///C:/...`）も同形式に書き換える。DocumentTab の `ConvertDocUriToPath` が逆変換を行う。

### ナビゲーション制御（DocumentTab.OnNavigationStarting / OnNewWindowRequested）

- `doc.hirake` への `.md` / `.markdown` ナビゲーション → キャンセルして新タブで開く
- 内部ホスト（assets / doc / temp）→ 許可
- それ以外の http(s) → キャンセルして既定ブラウザで開く

### Assets 側（ブラウザ内、viewer.js）

目次サイドバー生成・現在位置ハイライト、Ctrl+F ページ内検索、highlight.js / mermaid の適用を担当。Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+O は WebView2 にフォーカスがあると WPF に届かないため、viewer.js が `window.chrome.webview.postMessage({type:'shortcut', action:...})` で C# 側へ転送し、`DocumentTab.OnWebMessageReceived` → `IDocumentTabHost` で処理する。vendor 未読込でも全体が死なないよう各機能は try/catch でガードされている。

### csproj の注意点

`Assets/**/*` はワイルドカードで `None` + `CopyToOutputDirectory` として取り込まれる（存在しなくてもビルドが通る設計）。Assets にファイルを追加する場合、csproj の編集は不要。
