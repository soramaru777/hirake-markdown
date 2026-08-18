using System.Globalization;
using System.IO;
using System.Text;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Hirake;

/// <summary>hirake:// の解析結果（検証済み）。verb ごとに派生型を持つ。</summary>
public abstract record HirakeRequest;

/// <summary>hirake://open の解析結果（検証済み）。</summary>
public sealed record HirakeOpenRequest(string Path, int? Line, string? Heading) : HirakeRequest;

/// <summary>hirake://workspace の解析結果（検証済み）。</summary>
public sealed record HirakeWorkspaceRequest(string Name) : HirakeRequest;

/// <summary>見出しの探し方（ISSUE #53）。</summary>
public enum HeadingMatchMode
{
    /// <summary>
    /// 見出しテキストを優先し、見つからなければ id で探す。
    /// <c>hirake://open?heading=</c> はこちら（契約が「見出しテキスト」のため）。
    /// </summary>
    TextFirst,

    /// <summary>
    /// id を優先し、見つからなければ見出しテキストで探す。
    /// 文書内リンクの <c>[表示](other.md#target)</c> はこちら
    /// （URL のフラグメントは本来 id を指すため）。
    /// </summary>
    IdFirst,
}

/// <summary>
/// hirake:// URL スキームの解析と検証。
///
/// 文法: hirake://open?path=&lt;絶対パス&gt;[&amp;line=&lt;1始まり&gt;][&amp;heading=&lt;見出しテキスト&gt;]
///
/// これはブラウザや他アプリから任意に叩かれる入口なので、検証はここ 1 か所へ閉じる。
/// コマンドライン引数から来た URI も、名前付きパイプ（2 番目のプロセス）から来た
/// URI も、送信側を信用せず必ず <see cref="TryParse"/> を通すこと。
/// 検証に落ちた URI は黙って無視する（エラーダイアログを出すと「外部から任意に
/// ダイアログを出させる」経路が成立するため）。
///
/// 判定は fail-closed（判定できないものは拒否）で統一する。
/// 行うのは「Markdown をタブで開く」ことだけ。プロセス起動・ファイル書き込み・
/// 任意コマンド実行は一切しない。
/// </summary>
public static class HirakeUri
{
    public const string Scheme = "hirake";

    /// <summary>実装済みの verb。未知の verb（canvas 等）は拒否する。</summary>
    private const string OpenVerb = "open";

    /// <summary>ワークスペースをウィンドウとして開く verb。</summary>
    private const string WorkspaceVerb = "workspace";

    private const int MaxUriLength = 2048;
    private const int MaxHeadingLength = 200;
    private const int MaxLine = 1_000_000;

    /// <summary>
    /// 見出し検索で読み込むファイルサイズの上限。外部から繰り返し URI を叩かれる
    /// 入口なので、巨大ファイルで既存プロセスを詰まらせないよう上限を設ける
    /// （走査系の既存上限と同じ 2MB）。
    /// </summary>
    private const long MaxHeadingSearchBytes = 2L * 1024 * 1024;

    private static readonly string[] AllowedExtensions = [".md", ".markdown"];

    private static readonly char[] InvalidPathChars = ['<', '>', '"', '|', '?', '*', '\0'];

    private const string SchemePrefix = Scheme + "://";

    /// <summary>文字列が hirake:// URI か（引数・パイプ行の判別用）。</summary>
    public static bool IsHirakeUri(string? value)
        => value != null && value.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// URI を解析・検証する。1 つでも規則に反したら false（呼び出し側は無反応）。
    /// 未知キー・キー重複・値の不正はいずれも URI 全体の拒否とする
    /// （処理系ごとの解釈差を突かれないよう、曖昧な入力は受け付けない）。
    /// </summary>
    public static bool TryParse(string? uri, out HirakeRequest? request)
    {
        request = null;

        if (string.IsNullOrWhiteSpace(uri) || uri.Length > MaxUriLength)
        {
            return false;
        }
        if (!IsHirakeUri(uri))
        {
            return false;
        }

        // verb とクエリの切り出し。文法が単純なうちは自前で分解する（依存を増やさない）。
        string rest = uri[SchemePrefix.Length..];
        int queryIndex = rest.IndexOf('?');
        string verb = (queryIndex < 0 ? rest : rest[..queryIndex]).TrimEnd('/');

        if (queryIndex < 0)
        {
            return false; // どの verb もクエリが必須。
        }

        string query = rest[(queryIndex + 1)..];

        if (string.Equals(verb, WorkspaceVerb, StringComparison.OrdinalIgnoreCase))
        {
            return TryParseWorkspace(query, out request);
        }

        if (!string.Equals(verb, OpenVerb, StringComparison.OrdinalIgnoreCase))
        {
            return false; // 未知・未実装の verb は無視する。
        }

        string? rawPath = null;
        string? rawLine = null;
        string? rawHeading = null;

        foreach (string pair in query.Split('&'))
        {
            if (pair.Length == 0)
            {
                return false; // "a=1&&b=2" のような空要素は受け付けない。
            }

            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                return false; // '=' 欠損・キー空。
            }

            string key = pair[..eq];
            string value = pair[(eq + 1)..];

            if (key.Equals("path", StringComparison.OrdinalIgnoreCase))
            {
                if (rawPath != null) return false; // キー重複は拒否。
                rawPath = value;
            }
            else if (key.Equals("line", StringComparison.OrdinalIgnoreCase))
            {
                if (rawLine != null) return false;
                rawLine = value;
            }
            else if (key.Equals("heading", StringComparison.OrdinalIgnoreCase))
            {
                if (rawHeading != null) return false;
                rawHeading = value;
            }
            else
            {
                return false; // 未知キーは拒否。
            }
        }

        string? path = TryDecodeAndValidatePath(rawPath);
        if (path == null)
        {
            return false;
        }

        int? line = null;
        if (rawLine != null && !TryParseLine(rawLine, out line))
        {
            return false; // 指定されたが不正な値は URI 全体を拒否する。
        }

        string? heading = null;
        if (rawHeading != null && !TryParseHeading(rawHeading, out heading))
        {
            return false;
        }

        request = new HirakeOpenRequest(path, line, heading);
        return true;
    }

    /// <summary>
    /// URL エンコードされていない生のパスを、open verb と同じ規則で検証する。
    /// workspaces.json はローカルファイルであり書き換えられ得るため、そこから
    /// 復元する文書パスもこの規則を通す（送信元を問わず入口で同じ検証をする）。
    /// 受理できなければ null。
    /// </summary>
    public static string? ValidateDocumentPath(string? path) => ValidatePathCore(path);

    /// <summary>
    /// フォルダが UNC・ネットワークドライブ・再解析ポイント経由かどうか。
    /// 仮想タブ（キャンバス等）の起点フォルダの検証に使う。判定できない場合は
    /// リモート扱い（true）にする（fail-closed）。
    /// </summary>
    public static bool IsRemoteOrUncFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.IndexOfAny(InvalidPathChars) >= 0)
        {
            return true;
        }

        if (folder.StartsWith(@"\\", StringComparison.Ordinal)
            || folder.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            string full = Path.GetFullPath(folder);

            if (full.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(full))
            {
                return true;
            }

            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            {
                return true;
            }

            return HasLinkInPath(full);
        }
        catch
        {
            return true;
        }
    }

    private static string? TryDecodeAndValidatePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        string decoded;
        try
        {
            // '+' は空白として解釈しない（Windows パスに '+' が現れうるため）。
            decoded = Uri.UnescapeDataString(rawPath);
        }
        catch
        {
            return null;
        }

        return ValidatePathCore(decoded);
    }

    private static string? ValidatePathCore(string? decoded)
    {
        if (string.IsNullOrWhiteSpace(decoded) || decoded.IndexOfAny(InvalidPathChars) >= 0)
        {
            return null;
        }

        // UNC は拒否する。\\attacker\share\x.md を踏ませると SMB 認証情報の
        // 漏洩につながるため、ローカルの完全修飾パスだけを許可する。
        if (decoded.StartsWith(@"\\", StringComparison.Ordinal)
            || decoded.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(decoded))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(decoded);
        }
        catch
        {
            return null;
        }

        // 正規化後も UNC でないこと（"C:\..\\\\host\share" 等の抜けを塞ぐ）。
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        // NTFS 代替データストリーム（"ordinary.txt:payload.md"）は拡張子検査を
        // すり抜けるため、ドライブレターのコロン以外の ':' を拒否する。
        int colon = full.IndexOf(':', StringComparison.Ordinal);
        if (colon != 1 || full.IndexOf(':', colon + 1) >= 0)
        {
            return null;
        }

        string extension = Path.GetExtension(full);
        bool allowed = false;
        foreach (string candidate in AllowedExtensions)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }
        if (!allowed)
        {
            return null;
        }

        // ドライブレターにマップされたネットワークドライブも UNC と同じ経路のため拒否する。
        // 判定できない場合も拒否する（fail-closed）。
        try
        {
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            var drive = new DriveInfo(root);
            if (drive.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        // ローカルパスに見えても、途中のジャンクション／シンボリックリンクが
        // リモートを指していれば同じ漏洩経路になる。再解析ポイントは拒否する。
        if (HasLinkInPath(full))
        {
            return null;
        }

        try
        {
            if (!File.Exists(full))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        return full;
    }

    /// <summary>
    /// ファイル自身または祖先ディレクトリにシンボリックリンク／ジャンクションが
    /// 含まれるか。判定できない場合は true（拒否側）を返す。
    ///
    /// 注意（既知の限界）: この検査と、実際の読み込み・WebView2 のナビゲーションは
    /// 別操作のため TOCTOU は残る。検査後に祖先ディレクトリを junction へ差し替え
    /// られれば UNC へ到達し得る。ただしそれには対象フォルダへのローカル書き込み
    /// 権限が必要で、その権限があるなら文書ファイル自体を差し替えられる（本アプリの
    /// 脅威モデルでは既に敗北している）。ハンドルベースの完全な解決は、文書の実体を
    /// doc.hirake 仮想ホスト経由で WebView2 が読む現在の構造では閉じられないため、
    /// ここでは「リモート指定を平時に弾く」多層防御として実装している。
    /// </summary>
    private static bool HasLinkInPath(string fullPath)
    {
        try
        {
            var file = new FileInfo(fullPath);
            if (file.LinkTarget != null)
            {
                return true;
            }

            for (DirectoryInfo? dir = file.Directory; dir != null; dir = dir.Parent)
            {
                if (dir.LinkTarget != null)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return true; // 判定不能は拒否する。
        }
    }

    private static bool TryParseLine(string rawLine, out int? line)
    {
        line = null;
        if (!int.TryParse(rawLine, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }
        if (value < 1 || value > MaxLine)
        {
            return false;
        }
        line = value;
        return true;
    }

    private static bool TryParseHeading(string rawHeading, out string? heading)
    {
        heading = null;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(rawHeading).Trim();
        }
        catch
        {
            return false;
        }

        if (decoded.Length == 0 || decoded.Length > MaxHeadingLength)
        {
            return false;
        }
        foreach (char c in decoded)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        heading = decoded;
        return true;
    }

    // ---- 見出し → 行番号 -----------------------------------------------

    /// <summary>
    /// Markdown ソースから見出しテキストに一致する行（1 始まり）を探す。
    ///
    /// 解析は表示と同じ Markdig パイプライン（MarkdownRenderer.Pipeline）で行う。
    /// 行単位の自前スキャンだと、コードフェンスの長さ・インデントされたコード・
    /// リスト内の見出し・YAML フロントマターといった規則を取りこぼし、表示内容と
    /// ずれた位置へジャンプしてしまうため、構文木を唯一の真実源にする。
    ///
    /// 見出し id（AutoIdentifiers）ではなく見出しテキストで指定するのは、日本語の
    /// 見出しでは id が section / section-1 のような非可読な採番になり、URL として
    /// 書けず、見出しの追加・並べ替えで簡単にずれるため。
    ///
    /// ただし**見出しテキストで見つからなかったときは id でも照合する**（ISSUE #53）。
    /// 文書内リンクの `[表示](other.md#my-heading)` は GitHub 流の id を書く記法が
    /// 一般的で、テキスト照合だけでは 1 件も当たらないため。優先はテキスト側のままで、
    /// hirake:// の契約（見出しテキストで指定する）は変えない。
    /// </summary>
    public static int? FindHeadingLine(
        string filePath, string headingText, HeadingMatchMode mode = HeadingMatchMode.TextFirst)
    {
        if (string.IsNullOrWhiteSpace(headingText))
        {
            return null;
        }

        // 外部入力起点の処理なので、読み込む・解析するサイズに上限を設ける。
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length > MaxHeadingSearchBytes)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        string text;
        try
        {
            text = MarkdownRenderer.ReadFileText(filePath);
        }
        catch
        {
            return null;
        }

        try
        {
            // 解析コンテキストを渡すのは、見出しに [[wikilink]] が含まれる場合に
            // 描画側と同じ表示テキストへ揃えるため（ISSUE #53）。
            // textOnly にして**ファイルは探させない**。ここはリンクのクリック処理から
            // 同期で呼ばれるので、索引の構築（フォルダ走査）を持ち込むと画面が止まる。
            MarkdownDocument document = Markdown.Parse(
                text,
                MarkdownRenderer.Pipeline,
                MarkdownRenderer.CreateParserContext(filePath, root: null, textOnly: true));
            string target = NormalizeSpaces(headingText);
            int? textMatch = null;
            int? idMatch = null;

            // 両方を集めてから優先順位で選ぶ。先に当たった方を即返すと、
            // 「id 優先」を指定しても文書内の順番しだいでテキスト側が勝ってしまう。
            foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
            {
                // Markdig の Line は 0 始まり。ファイル行に合わせて 1 始まりで扱う。
                if (textMatch == null)
                {
                    string title = NormalizeSpaces(GetInlineText(heading.Inline));
                    if (string.Equals(title, target, StringComparison.OrdinalIgnoreCase))
                    {
                        textMatch = heading.Line + 1;
                    }
                }

                if (idMatch == null)
                {
                    string? id = heading.TryGetAttributes()?.Id;
                    if (id != null && string.Equals(id, target, StringComparison.OrdinalIgnoreCase))
                    {
                        idMatch = heading.Line + 1;
                    }
                }

                if (textMatch != null && idMatch != null)
                {
                    break;
                }
            }

            if (mode == HeadingMatchMode.IdFirst)
            {
                return idMatch ?? textMatch;
            }
            return textMatch ?? idMatch;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 連続する空白（改行・タブを含む）を 1 つの半角空白へ潰し、前後を除去する。
    /// 複数行にまたがる Setext 見出しは Markdig が改行込みの 1 つのリテラルとして
    /// 持つため、表示テキストと同じ「1 つの空白でつながった形」に揃えて比較する。
    /// </summary>
    private static string NormalizeSpaces(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool pendingSpace = false;

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>インライン要素から表示テキストだけを取り出す（強調記号などは落とす）。</summary>
    private static string GetInlineText(ContainerInline? container)
    {
        if (container == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (Inline inline in container.Descendants())
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case HtmlEntityInline entity:
                    // &amp; などは表示テキスト（&）で比較する。
                    sb.Append(entity.Transcoded.AsSpan());
                    break;
                case LineBreakInline:
                    // 複数行の Setext 見出しは表示上 1 つの空白でつながる。
                    sb.Append(' ');
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// hirake://workspace?name=&lt;名前&gt; を解析する。
    /// 名前はパスとして一切解釈しない（ファイル名に落とさない設計のため、
    /// トラバーサルの経路自体が存在しない）。制御文字と長さだけを検証する。
    /// </summary>
    private static bool TryParseWorkspace(string query, out HirakeRequest? request)
    {
        request = null;

        string? rawName = null;

        foreach (string pair in query.Split('&'))
        {
            if (pair.Length == 0)
            {
                return false; // 空要素は受け付けない。
            }

            int eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                return false; // '=' 欠損・キー空。
            }

            string key = pair[..eq];
            string value = pair[(eq + 1)..];

            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                if (rawName != null) return false; // キー重複は拒否。
                rawName = value;
            }
            else
            {
                return false; // 未知キーは拒否。
            }
        }

        if (rawName == null)
        {
            return false; // name は必須。
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(rawName);
        }
        catch
        {
            return false;
        }

        // 空白の正規化・制御文字の拒否・長さ上限は WorkspaceStore と同じ規則を使う。
        string name = WorkspaceStore.NormalizeName(decoded);
        if (name.Length == 0)
        {
            return false;
        }

        request = new HirakeWorkspaceRequest(name);
        return true;
    }

    // ---- URI 組み立て（「この位置へのリンクをコピー」用） ---------------

    /// <summary>指定ファイル（と任意の行）を指す hirake:// URI を組み立てる。</summary>
    public static string BuildOpenUri(string fullPath, int? line)
    {
        var sb = new StringBuilder();
        sb.Append(SchemePrefix).Append(OpenVerb).Append("?path=");
        sb.Append(Uri.EscapeDataString(fullPath));
        if (line is int value && value >= 1)
        {
            sb.Append("&line=").Append(value.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
