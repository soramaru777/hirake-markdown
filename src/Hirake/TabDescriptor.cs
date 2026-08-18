using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace Hirake;

/// <summary>タブ記述子の種別。未知の値は復元時にその 1 件だけ捨てる（前方互換）。</summary>
public static class TabKinds
{
    public const string Document = "document";
    public const string Canvas = "canvas";
    public const string Structure = "structure";
    public const string Fingerprint = "fingerprint";
    public const string Stats = "stats";
    public const string LinkCheck = "linkcheck";

    /// <summary>既知の種別かどうか。</summary>
    public static bool IsKnown(string? kind) =>
        kind is Document or Canvas or Structure or Fingerprint or Stats or LinkCheck;

    /// <summary>
    /// その種別の <see cref="TabDescriptor.Path"/> がフォルダを指すか（= 仮想タブか）。
    /// document 以外はすべてフォルダを起点に開くタブ。
    /// </summary>
    public static bool IsFolderScoped(string? kind) =>
        kind is Canvas or Structure or Fingerprint or Stats or LinkCheck;
}

/// <summary>ビューポート（<see cref="CanvasViewState"/>）の検証。</summary>
public static class CanvasViewStateExtensions
{
    /// <summary>有限かつズーム率が正であること。壊れた JSON をそのまま適用しないための検証。</summary>
    public static bool IsValid(this CanvasViewState? view) =>
        view != null &&
        double.IsFinite(view.X) && double.IsFinite(view.Y) &&
        double.IsFinite(view.K) && view.K > 0;
}

/// <summary>
/// 開いているタブ 1 つ分の記述子。保存・復元の単位をパス文字列ではなく
/// 種別付きの記述子にすることで、仮想タブ（キャンバス等）も復元できるようにする。
/// </summary>
public sealed class TabDescriptor
{
    /// <summary>種別（<see cref="TabKinds"/>）。</summary>
    public string Kind { get; set; } = TabKinds.Document;

    /// <summary>document はファイルのフルパス、それ以外は起点フォルダのフルパス。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>キャンバスのビューポート（canvas 以外は null）。</summary>
    public CanvasViewState? View { get; set; }

    /// <summary>
    /// 開いているタブから記述子を作る。保存対象外（クリップボード一時ファイル等）や
    /// 種別を判定できないタブでは null を返す。
    /// </summary>
    public static TabDescriptor? FromTab(DocumentTab tab, Func<string, bool> isExcluded)
    {
        if (tab == null)
        {
            return null;
        }

        // 仮想タブは FilePath に「フォルダのパス」が入るため、種別ごとに RootFolder を使う。
        switch (tab)
        {
            case CanvasTab canvas:
                return new TabDescriptor
                {
                    Kind = TabKinds.Canvas,
                    Path = NormalizePath(canvas.RootFolder, TabKinds.Canvas),
                    // 「どこを見ていたか」はワークスペースが持つ（ノード配置は共有のまま）。
                    View = canvas.CurrentView.IsValid() ? canvas.CurrentView : null,
                };
            case StructureQueryTab structure:
                return new TabDescriptor
                {
                    Kind = TabKinds.Structure,
                    Path = NormalizePath(structure.RootFolder, TabKinds.Structure),
                };
            case FingerprintTab fingerprint:
                return new TabDescriptor
                {
                    Kind = TabKinds.Fingerprint,
                    Path = NormalizePath(fingerprint.RootFolder, TabKinds.Fingerprint),
                };
            case StatsTab stats:
                return new TabDescriptor
                {
                    Kind = TabKinds.Stats,
                    Path = NormalizePath(stats.RootFolder, TabKinds.Stats),
                };
            case LinkCheckTab linkCheck:
                return new TabDescriptor
                {
                    Kind = TabKinds.LinkCheck,
                    Path = NormalizePath(linkCheck.RootFolder, TabKinds.LinkCheck),
                };
        }

        // 素の DocumentTab のみ document として扱う。
        if (tab.GetType() != typeof(DocumentTab))
        {
            return null;
        }

        string path = tab.FilePath;
        if (string.IsNullOrEmpty(path) || isExcluded(path))
        {
            return null;
        }

        return new TabDescriptor
        {
            Kind = TabKinds.Document,
            Path = NormalizePath(path, TabKinds.Document),
        };
    }

    /// <summary>
    /// 保存するパスを実体へ直す。<b>直せなければ元のパスをそのまま返す（捨てない）。</b>
    ///
    /// リンクで束ねた vault では、タブのパスがリンク経由のままだと復元時の検査
    /// （<see cref="HirakeUri.HasLinkInPath"/> 経由）で弾かれ、タブが黙って消える
    /// （ISSUE #60）。保存の時点で実体にしておけば、検査を緩めずに復元できる。
    ///
    /// <para>
    /// これは<b>検査ではなく最善努力</b>。解決できないもの（UNC・実在しない・
    /// リンクを辿る設定が無効）を捨てると、設定を入れていない利用者が保存する
    /// たびにタブを失う。安全性は従来どおり <see cref="CanRestore"/> が見る。
    /// </para>
    /// </summary>
    internal static string NormalizePath(string? path, string kind)
        => NormalizePath(path, kind, SettingsStore.Instance.FollowDirectoryLinks);

    /// <summary>
    /// 設定値を明示的に受け取る版。<b>SettingsStore の初期化中はこちらを使う</b>
    /// （初期化中に <c>SettingsStore.Instance</c> を引くと <c>Lazy</c> の再帰取得で
    /// 例外になり、起動時のセッションだけ黙って正規化されない）。
    /// </summary>
    internal static string NormalizePath(string? path, string kind, bool followLinks)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path ?? string.Empty;
        }

        try
        {
            return FileTreeItem.ResolveCheckedPath(path, TabKinds.IsFolderScoped(kind), followLinks)
                ?? path;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// 復元してよい記述子かどうか。種別が既知で、対象が実在することを確認する。
    /// 落ちた記述子はその 1 件だけ捨て、ウィンドウ全体の復元は続ける。
    ///
    /// 文書パスは <see cref="HirakeUri.ValidateDocumentPath"/> を通す。
    /// workspaces.json / settings.json はローカルファイルであり書き換えられ得るため、
    /// hirake://open と同じ規則（UNC・ネットワークドライブ・再解析ポイント・
    /// 拡張子ホワイトリスト）で弾く。
    /// </summary>
    public bool CanRestore()
    {
        if (!TabKinds.IsKnown(Kind) || string.IsNullOrWhiteSpace(Path))
        {
            return false;
        }

        try
        {
            if (!TabKinds.IsFolderScoped(Kind))
            {
                return HirakeUri.ValidateDocumentPath(Path) != null;
            }

            // 仮想タブの起点フォルダ。ファイルは開かないが、配下を再帰的に走査する
            // ため、リモートでないことに加えて「起点として安全か」も確認する。
            // ここを緩くすると、実際の列挙が拒否されるだけの空タブが復元される。
            return !HirakeUri.IsRemoteOrUncFolder(Path)
                && FileTreeItem.IsSafeTraversalRoot(Path);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>ウィンドウの位置とサイズ。</summary>
public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    /// <summary>数値として妥当か（壊れた JSON をそのまま適用しないため）。</summary>
    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Width > 0 && Height > 0;
}

/// <summary>
/// ウィンドウ 1 枚分の記述子。名前付きワークスペースも、名前を持たない
/// 通常のウィンドウ（前回セッション）も、同じ形で表す。
/// </summary>
public sealed class WindowDescriptor
{
    /// <summary>名前付きワークスペースを開いているウィンドウのみ設定される。</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>タブ記述子（順序 = タブの並び順）。</summary>
    public List<TabDescriptor> Tabs { get; set; } = new();

    /// <summary>アクティブタブの位置（<see cref="Tabs"/> の索引）。</summary>
    public int ActiveIndex { get; set; }

    /// <summary>ウィンドウの位置とサイズ。未保存なら null。</summary>
    public WindowBounds? Bounds { get; set; }
}
