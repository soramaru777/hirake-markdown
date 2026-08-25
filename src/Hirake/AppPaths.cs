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
/// - 起点だけは環境変数 <c>HIRAKE_DATA_ROOT</c> で差し替えられる（ISSUE #94）。
///   キャンバスの検証ハーネスが実利用の設定・ピン留め・WebView2 プロファイルを
///   汚さずに動くために要る。派生プロパティは <see cref="Root"/> からの合成のままで、
///   1 か所だけ差し替えれば全体が追随する
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "Hirake";

    /// <summary>データ領域の起点を差し替える環境変数名（絶対パスのみ受理）。</summary>
    private const string DataRootVariable = "HIRAKE_DATA_ROOT";

    static AppPaths()
    {
        Root = ResolveRoot();

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

    /// <summary>
    /// データ領域の起点を決める。
    ///
    /// 既定は <c>%LocalAppData%\Hirake</c>。環境変数 <c>HIRAKE_DATA_ROOT</c> に
    /// 絶対パスが入っているときだけ、そちらを起点にする（ISSUE #94 の検証ハーネス用）。
    ///
    /// 絶対パス以外（未設定・空・空白のみ・相対パス）は黙って既定へ倒す。
    /// 壊れた環境変数で、カレントディレクトリ配下など予期しない場所へ
    /// 利用者のデータを散らかす方が害が大きいため、ここは fail-safe にする。
    /// </summary>
    private static string ResolveRoot()
    {
        string? custom = null;
        try
        {
            custom = Environment.GetEnvironmentVariable(DataRootVariable);
        }
        catch (System.Security.SecurityException)
        {
            // 環境変数を読めない構成。既定へ倒す。
        }

        if (!string.IsNullOrWhiteSpace(custom) && Path.IsPathFullyQualified(custom))
        {
            try
            {
                // 末尾のセパレータは落とす。Root は表示にも比較にも使うため、
                // 同じ場所が 2 通りの文字列で現れないようにしておく
                // （Path.Combine は二重区切りを作らないので派生側の実害は無いが、
                //   ドライブ直下は TrimEndingDirectorySeparator が保持する）。
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(custom));
            }
            catch (ArgumentException)
            {
                // 不正な文字などで正規化できない。既定へ倒す。
            }
            catch (NotSupportedException)
            {
            }
            catch (PathTooLongException)
            {
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);
    }

    /// <summary>データ領域の起点（既定は <c>%LocalAppData%\Hirake</c>）。</summary>
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
