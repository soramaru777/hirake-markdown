using System.IO;

namespace Hirake;

/// <summary>
/// アプリのデータ配置を 1 か所に集約する。各クラスは自前でパスを組み立てない。
///
/// ■ なぜ集約するか
///
/// 同じ <c>%LocalAppData%\Hirake\…</c> の組み立てが 9 ファイル 11 か所に散っており、
/// フォルダ名は各クラスが文字列リテラルで持っていた。綴りの取り違えがビルドで
/// 検出できず、配置を変えるときに追随漏れが起きる（ISSUE #40 設計 1-1）。
///
/// インストーラ（Inno Setup）はインストール先とデータ領域を分けて扱う。将来 MSIX へ
/// 移す場合は AppData がリダイレクトされるため、その吸収を <see cref="Root"/> の
/// 解決だけで済ませられるようにしておく。
///
/// ■ 方針
///
/// - ディレクトリの作成は行わない。参照しただけで空フォルダができる挙動にしない
///   （必要になった時点で利用側が <c>Directory.CreateDirectory</c> する。現行と同じ）
/// - 実行中に変わる値ではないため、静的に 1 度だけ確定させる
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "Hirake";

    static AppPaths()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

        SettingsFile = Path.Combine(Root, "settings.json");
        WorkspacesFile = Path.Combine(Root, "workspaces.json");

        CanvasDir = Path.Combine(Root, "canvas");
        ThumbsDir = Path.Combine(CanvasDir, "thumbs");
        PreviewsDir = Path.Combine(CanvasDir, "previews");

        TempDir = Path.Combine(Root, "temp");
        ClipboardDir = Path.Combine(Root, "clipboard");
        WebView2Dir = Path.Combine(Root, "WebView2");
        IndexDir = Path.Combine(Root, "index");
        OcrDir = Path.Combine(Root, "ocr");
        StatsDir = Path.Combine(Root, "stats");
        ModelsDir = Path.Combine(Root, "models");
        LogsDir = Path.Combine(Root, "logs");
    }

    /// <summary>データ領域の起点（<c>%LocalAppData%\Hirake</c>）。</summary>
    public static string Root { get; }

    /// <summary>設定ファイル。</summary>
    public static string SettingsFile { get; }

    /// <summary>ワークスペース定義ファイル。</summary>
    public static string WorkspacesFile { get; }

    /// <summary>キャンバスのノード配置。</summary>
    public static string CanvasDir { get; }

    /// <summary>サムネイル PNG（thumbs.hirake 仮想ホストのマップ先）。</summary>
    public static string ThumbsDir { get; }

    /// <summary>近景プレビュー HTML（previews.hirake 仮想ホストのマップ先）。</summary>
    public static string PreviewsDir { get; }

    /// <summary>NavigateToString の上限を超えた HTML の一時置き場（temp.hirake のマップ先）。</summary>
    public static string TempDir { get; }

    /// <summary>クリップボード画像の一時置き場。</summary>
    public static string ClipboardDir { get; }

    /// <summary>WebView2 のユーザーデータ。</summary>
    public static string WebView2Dir { get; }

    /// <summary>セマンティック索引。</summary>
    public static string IndexDir { get; }

    /// <summary>OCR 結果のキャッシュ。</summary>
    public static string OcrDir { get; }

    /// <summary>統計の履歴。</summary>
    public static string StatsDir { get; }

    /// <summary>埋め込みモデルの置き場（この下にモデル名のフォルダを作る）。</summary>
    public static string ModelsDir { get; }

    /// <summary>診断ログ。</summary>
    public static string LogsDir { get; }
}
