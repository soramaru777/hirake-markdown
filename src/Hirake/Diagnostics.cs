using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Hirake;

/// <summary>
/// 握りつぶしている失敗の記録先。<c>%LocalAppData%\Hirake\logs</c> へ追記する。
///
/// ■ なぜ必要か
///
/// このアプリは「ビューアを止めない」ことを優先して多くの失敗を握りつぶしている。
/// その結果、テーマがずれる・設定が保存されないといった症状が起きても、原因を
/// 追う手掛かりが何も残らない（ISSUE #48）。ユーザーに見せるとモーダルダイアログ
/// しか出せず、見た目だけの操作でエラーダイアログが出るのはむしろ悪いため、
/// 「ユーザーには見せず、開発者が後から追える」ログをここに集約する。
///
/// ■ 方針
///
/// - ログのためにアプリを止めない。書き込みに失敗したらその場で諦める
/// - UI スレッドを止めない。記録はキューへ積むだけで、書き出しは専用スレッド
/// - 文書のパスは記録しない。ユーザーがログを送るときにフォルダ構成が渡らないように
///   （ISSUE #48 設計 6-3。パスが要る箇所が出たらそのときに方針を決める）
/// - 外部送信は一切しない
/// </summary>
public static class Diagnostics
{
    /// <summary>保持する日数。起動時にこれより古いファイルを削除する。</summary>
    private const int RetentionDays = 7;

    /// <summary>1 ファイルの上限。超えたらその日はそれ以上書かない（ローテーションしない）。</summary>
    private const long MaxFileBytes = 1024 * 1024;

    /// <summary>キューの上限。診断のためにメモリを食い潰さない。</summary>
    private const int MaxQueueLength = 1000;

    /// <summary>Detail の上限文字数。</summary>
    private const int MaxDetailLength = 500;

    /// <summary>Message の上限文字数。</summary>
    private const int MaxMessageLength = 200;

    /// <summary>Category の上限文字数。</summary>
    private const int MaxCategoryLength = 32;

    /// <summary>マスク処理へ渡す最大長。正規表現に外部入力の長さを預けないための保険。</summary>
    private const int MaxMaskInputLength = 2000;

    /// <summary>同じ内容を続けて記録しない時間。多重ウィンドウで同じ失敗が並ぶため。</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(5);

    private static readonly BlockingCollection<string> Queue =
        new(new ConcurrentQueue<string>(), MaxQueueLength);

    private static readonly object RecentLock = new();
    private static readonly Dictionary<string, DateTime> RecentKeys = new(StringComparer.Ordinal);

    private static readonly Lazy<string> DirectoryPath = new(() => AppPaths.LogsDir);

    private static readonly Lazy<bool> Started = new(Start);

    private static Thread? _writer;
    private static int _droppedCount;

    /// <summary>ログの保存先フォルダ。</summary>
    public static string LogDirectory => DirectoryPath.Value;

    /// <summary>
    /// アプリの正常終了時に、キューに残っている分を書き出す。
    /// 異常の直後にユーザーがアプリを閉じることは多く、そのとき最も必要な最新の
    /// 記録が失われるため。UI を止めないよう待ち時間には上限を設ける。
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            if (!Started.IsValueCreated || !Started.Value)
            {
                return;
            }

            Queue.CompleteAdding();
            _writer?.Join(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // 終了処理を止めない。
        }
    }

    /// <summary>
    /// 失敗を 1 件記録する。呼び出し側は戻り値を気にしなくてよい（決して例外を投げない）。
    /// </summary>
    /// <param name="category">"theme" / "zoom" など、経路が分かる短い識別子。</param>
    /// <param name="message">何をしようとして失敗したか。</param>
    /// <param name="detail">例外やスクリプトのエラーメッセージ。</param>
    public static void Record(string category, string message, string? detail = null)
    {
        try
        {
            if (!Started.Value)
            {
                return;
            }

            // 3 つとも同じ処理を通す。呼び出し側の作法に頼ると、経路が増えたときに
            // パス混入や改行での行崩れが再発する（記録は入口で必ず安全にする）。
            string safeCategory = Sanitize(category, MaxCategoryLength);
            string safeMessage = Sanitize(message, MaxMessageLength);
            string trimmed = Sanitize(detail, MaxDetailLength);

            if (safeCategory.Length == 0)
            {
                safeCategory = "other";
            }

            if (IsDuplicate(safeCategory, safeMessage, trimmed))
            {
                return;
            }

            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-ddTHH:mm:ss.fffZ}  {1}  {2}{3}",
                DateTime.UtcNow,
                safeCategory,
                safeMessage,
                trimmed.Length == 0 ? string.Empty : "  " + trimmed);

            // 満杯なら捨てる（TryAdd はブロックしない）。
            // 捨てたこと自体が見えないと「ログが無い＝異常が無い」と誤読するため数える。
            if (!Queue.TryAdd(line))
            {
                Interlocked.Increment(ref _droppedCount);
            }
        }
        catch
        {
            // 記録のためにアプリを止めない。
        }
    }

    /// <summary>例外から記録する。</summary>
    public static void Record(string category, string message, Exception? exception)
    {
        string? detail = null;
        if (exception != null)
        {
            // AggregateException は中身の方が有用。
            Exception target = exception is AggregateException aggregate && aggregate.InnerException != null
                ? aggregate.InnerException
                : exception;
            detail = target.GetType().Name + ": " + target.Message;
        }

        Record(category, message, detail);
    }

    // ---- 内部 ---------------------------------------------------------

    /// <summary>1 行化・マスク・長さ制限。記録するすべてのフィールドがこれを通る。</summary>
    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // 改行・タブ・その他の制御文字はログの「1 行 = 1 レコード」と区切りを崩す。
        // 書式指定文字（Cf）も、閲覧時に見た目を偽装できるため落とす。
        string single = ControlCharacters.Replace(value, " ").Trim();

        // マスクへ渡す長さに上限を設ける。正規表現の実行時間を外部入力（例外
        // メッセージ）の長さに預けない。切り詰めは最終長よりだいぶ余裕を持たせる。
        if (single.Length > MaxMaskInputLength)
        {
            single = single[..MaxMaskInputLength];
        }

        single = Mask(single);
        return single.Length > maxLength ? single[..maxLength] : single;
    }

    /// <summary>パスの 1 要素に使える文字（区切り・引用符・改行以外）。</summary>
    private const string PathChar = @"[^\\/\r\n""'<>|]";

    /// <summary>上記から空白を除いたもの。末尾要素を文章へ延ばさないために使う。</summary>
    private const string PathCharNoSpace = @"[^\\/\r\n""'<>|\s]";

    /// <summary>
    /// 利用者の文書に付く拡張子。空白入りのファイル名を伏せるとき、
    /// どこまでがファイル名かの手掛かりに使う（<c>meeting notes.md</c>）。
    /// スクリプト・スタイルは含めない。JS のスタックトレースに出る
    /// <c>viewer.js</c> などはアプリ自身のファイルで、伏せると原因が追えなくなる。
    /// </summary>
    private const string DocumentExtension =
        @"(?:md|markdown|txt|pdf|csv|rtf|docx?|xlsx?|pptx?|png|jpe?g|gif|bmp|svg|webp)";

    /// <summary>
    /// パスの末尾要素。既知の文書拡張子で終わる場合だけ空白を許す。
    /// 常に空白を許すと後続の文章まで飲み込み、常に禁じると
    /// <c>C:\private\meeting notes.md</c> の <c>notes.md</c> が残る。
    /// </summary>
    private const string PathLeaf =
        "(?:" + PathChar + @"*\." + DocumentExtension + @"\b|" + PathCharNoSpace + "*)";

    /// <summary>
    /// ルート以降の残り（区切りで終わる中間フォルダの繰り返し＋末尾要素）。
    ///
    /// 単純な <c>\S*</c> だと <c>C:\Users\alice\My Documents\secret.md</c> のような
    /// 空白入りのパスが途中で切れ、フォルダ名とファイル名が残ってしまう。
    /// 中間フォルダは「次の区切りまで」として空白を許す。
    /// </summary>
    private const string PathTail = "(?:" + PathChar + @"*[\\/])*" + PathLeaf;

    /// <summary>制御文字と書式指定文字。1 行 = 1 レコードと区切りを守るために落とす。</summary>
    private static readonly Regex ControlCharacters = MakeRegex(@"[\p{Cc}\p{Cf}]");

    // パスらしき文字列。いずれも「区切りで終わる中間要素の繰り返し＋末尾要素」で終端する。
    // URI 側も同じ扱いにしないと、file:///C:/My Documents/secret.md が途中で切れる。
    private static readonly Regex FileUri = MakeRegex(@"file://" + PathTail, ignoreCase: true);

    // 文書ホストはドライブ別に c.doc.hirake の形で作られる（MarkdownRenderer.BuildDocHost）。
    // doc.hirake だけを見ていると、実際に使われる URI がそのまま残る。
    // assets.hirake はアプリ自身のファイルで利用者の情報を含まないため対象にしない。
    private static readonly Regex VirtualHostUri =
        MakeRegex(@"https?://(?:[A-Za-z]\.)?(?:doc|temp|previews|thumbs)\.hirake/" + PathTail, ignoreCase: true);

    // UNC は \\server\share と //server/share の両方。scheme:// を拾わないよう
    // 直前が ':' でないことを条件にする（file:// と仮想ホストは先に伏せ済み）。
    private static readonly Regex UncPath = MakeRegex(@"(?<!:)(?:\\\\|//)" + PathTail);

    // 直前が英数字でないことを条件にする。付けないと https://... の "s:/" を
    // ドライブレターと見なし、あらゆる URL をスキーム途中から伏せてしまう。
    private static readonly Regex DrivePath = MakeRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/]" + PathTail);

    // ルートを持たない相対パス。文章と紛れるため、区切りを含み、かつ末尾が
    // 既知の文書拡張子であるものだけに限る（docs/private/notes.md）。
    private static readonly Regex RelativeDocumentPath = MakeRegex(
        PathCharNoSpace + @"+[\\/](?:" + PathChar + @"*[\\/])*" + PathChar + @"*\." + DocumentExtension + @"\b",
        ignoreCase: true);

    /// <summary>
    /// 実行時間に上限を付けた正規表現を作る。入力は外部（例外メッセージ）由来で、
    /// 診断のために UI スレッドを長く止めることは許されない。
    /// </summary>
    private static Regex MakeRegex(string pattern, bool ignoreCase = false) =>
        new(pattern,
            ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// 文書の場所が分かる文字列を伏せる。
    ///
    /// 呼び出し側がパスを渡さないだけでは足りない。WebView2 や JS の例外メッセージ、
    /// ファイル操作の例外には <c>C:\...</c> や <c>file:///...</c>、仮想ホスト URL が
    /// そのまま入り得るため、記録の直前でまとめて伏せる（ISSUE #48 設計 6-3）。
    ///
    /// 判断に迷う場合は伏せる側に倒す。2 つのパスに挟まれた語が巻き込まれることは
    /// あるが、フォルダ構成が残るより望ましい。伏せ切れたか判断できない場合
    /// （タイムアウト）は、その文字列ごと捨てる。
    /// </summary>
    private static string Mask(string detail)
    {
        if (detail.Length == 0)
        {
            return detail;
        }

        try
        {
            // 順序が意味を持つ。ルートを持つものから先に伏せ、最後に相対パスを見る。
            string masked = detail;
            masked = FileUri.Replace(masked, "<path>");
            masked = VirtualHostUri.Replace(masked, "<path>");
            masked = UncPath.Replace(masked, "<path>");
            masked = DrivePath.Replace(masked, "<path>");
            masked = RelativeDocumentPath.Replace(masked, "<path>");
            return masked;
        }
        catch (RegexMatchTimeoutException)
        {
            return "<masked>";
        }
    }

    /// <summary>直近に同じ内容を記録していれば true。</summary>
    private static bool IsDuplicate(string category, string message, string detail)
    {
        string key = category + "" + message + "" + detail;
        DateTime now = DateTime.UtcNow;

        lock (RecentLock)
        {
            if (RecentKeys.TryGetValue(key, out DateTime last) && now - last < DuplicateWindow)
            {
                return true;
            }

            RecentKeys[key] = now;

            // 抑止用の辞書が育ち続けないよう、古い分を落とす。
            if (RecentKeys.Count > 128)
            {
                foreach (string stale in RecentKeys
                             .Where(pair => now - pair.Value >= DuplicateWindow)
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    RecentKeys.Remove(stale);
                }
            }

            return false;
        }
    }

    /// <summary>書き出しスレッドを 1 本だけ起こす。失敗したら以後は記録しない。</summary>
    private static bool Start()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath.Value);
            CleanupOldFiles();

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "HirakeDiagnostics",
            };
            _writer.Start();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriterLoop()
    {
        // BlockingCollection の列挙は CompleteAdding まで待ち続ける。
        // バックグラウンドスレッドなので、アプリ終了時はそのまま落ちてよい。
        var encoding = new UTF8Encoding(false);

        foreach (string line in Queue.GetConsumingEnumerable())
        {
            try
            {
                FlushDropped(encoding);
                TryAppend(line, encoding);
            }
            catch
            {
                // 1 行の書き込み失敗で書き出しスレッドを止めない。
            }
        }

        // 取り出す要素が尽きた後に捨てた分が残ることがある（終了時など）。
        // 書ける最後の機会にもう一度だけ残す。
        try
        {
            FlushDropped(encoding);
        }
        catch
        {
            // 終了処理を止めない。
        }
    }

    /// <summary>
    /// キュー満杯で捨てた件数を 1 行残す。書けなかった場合は件数を戻す
    /// （書けたことにして数を失うと「捨てていない」と誤読される）。
    /// ただしファイルが上限に達している間は通知自体も書けない。
    /// </summary>
    private static void FlushDropped(UTF8Encoding encoding)
    {
        int dropped = Interlocked.Exchange(ref _droppedCount, 0);
        if (dropped == 0)
        {
            return;
        }

        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-ddTHH:mm:ss.fffZ}  diagnostics  {1} 件の記録を捨てました（キュー満杯）",
            DateTime.UtcNow,
            dropped);

        if (!TryAppend(line, encoding))
        {
            Interlocked.Add(ref _droppedCount, dropped);
        }
    }

    /// <summary>1 行追記する。書けなければ false（例外は投げない）。</summary>
    private static bool TryAppend(string line, UTF8Encoding encoding)
    {
        try
        {
            string path = Path.Combine(
                DirectoryPath.Value,
                "hirake-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

            string text = line + Environment.NewLine;
            long size = encoding.GetByteCount(text);

            var info = new FileInfo(path);
            if (info.Exists && info.Length + size > MaxFileBytes)
            {
                return false; // 上限を超える書き込みは行わない（超えてから止めない）。
            }

            File.AppendAllText(path, text, encoding);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupOldFiles()
    {
        try
        {
            DateTime limit = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (string file in Directory.EnumerateFiles(DirectoryPath.Value, "hirake-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < limit)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 1 件の削除失敗で残りを止めない。
                }
            }
        }
        catch
        {
            // 掃除に失敗しても記録は続ける。
        }
    }
}
