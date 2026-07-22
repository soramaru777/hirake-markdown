using System.IO;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Hirake;

/// <summary>
/// Windows OCR API（Windows.Media.Ocr）でローカル画像から文字を抽出するサービス。
/// セマンティック索引（SemanticIndexService）から画像参照ごとに呼ばれ、
/// 抽出テキストを索引チャンクへ混ぜて「スクショの中身」を意味検索できるようにする。
///
/// OcrEngine はユーザープロファイル言語から遅延初期化しプロセス内で共有する。
/// 言語パック未導入等で生成できない場合は以降すべて空文字を返して静かに無効化する。
/// 抽出結果は %LocalAppData%\Hirake\ocr\cache.json に画像パス＋mtime/length キーで
/// キャッシュし、再 OCR を避ける。例外は握りつぶし、失敗は空文字として扱う。
/// </summary>
internal static class OcrTextService
{
    // 10MB 超の画像は OCR コストとメモリを避けるためスキップする。
    private const long MaxImageBytes = 10L * 1024 * 1024;

    // 対応拡張子（それ以外は空文字）。
    private static readonly string[] SupportedExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    // ---- OCR エンジン（プロセス内共有・遅延初期化） -------------------

    private static readonly object EngineLock = new();
    private static bool _engineInitialized;
    private static OcrEngine? _engine;

    // ---- OCR キャッシュ ----------------------------------------------

    private sealed class CacheEntry
    {
        public long MtimeUtcTicks { get; set; }
        public long Length { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private static readonly object CacheLock = new();
    private static Dictionary<string, CacheEntry>? _cache;
    private static readonly object CacheSaveLock = new();

    // deferCacheSave で保存を遅延した未保存エントリがあるか（FlushCache の空振り防止）。
    private static volatile bool _cacheDirty;

    private static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hirake", "ocr");

    private static string CacheFilePath => Path.Combine(CacheDirectory, "cache.json");

    // ================================================================
    //  抽出 API
    // ================================================================

    /// <summary>
    /// 画像ファイルから OCR テキストを抽出する（同期）。バックグラウンドスレッドから
    /// 呼ばれる前提で、内部の WinRT 非同期は同期的に待機する（キャンセルは伝播）。
    /// 対象外拡張子・サイズ超過・エンジン無効時は空文字（確定的な「文字なし」）を返す。
    /// 一時的な読取失敗（破損・共有違反等）は null を返し、キャッシュもしない
    /// （呼び出し側が次回の再試行を判断できるように成功と失敗を区別する）。
    /// </summary>
    /// <summary>
    /// キャッシュ済みの OCR テキストのみを返す（OCR は実行しない・副作用なし）。
    /// 有効なキャッシュがあれば true。対象外拡張子・サイズ超過・非存在は
    /// 「確定的な文字なし」として true / 空文字を返す。
    /// </summary>
    internal static bool TryGetCachedText(string imagePath, out string text)
    {
        text = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return true;
            }

            string full = Path.GetFullPath(imagePath);
            if (!IsSupportedExtension(Path.GetExtension(full)))
            {
                return true;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(full);
                if (!info.Exists || info.Length > MaxImageBytes)
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }

            Dictionary<string, CacheEntry> cache = LoadCache();
            lock (CacheLock)
            {
                if (cache.TryGetValue(full.ToLowerInvariant(), out CacheEntry? hit)
                    && hit.MtimeUtcTicks == info.LastWriteTimeUtc.Ticks
                    && hit.Length == info.Length)
                {
                    text = hit.Text;
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return true; // 想定外の失敗は「文字なし確定」として扱う（再OCRさせない）。
        }
    }

    internal static string? ExtractText(string imagePath, CancellationToken ct)
        => ExtractText(imagePath, ct, deferCacheSave: false);

    /// <param name="deferCacheSave">
    /// true のとき、OCR 結果はメモリキャッシュにのみ反映しディスク保存を遅延する。
    /// 呼び出し側がまとめて <see cref="FlushCache"/> を呼ぶこと（対話検索など
    /// 高頻度経路で 1 枚ごとの全量書き込みを避ける）。
    /// </param>
    internal static string? ExtractText(string imagePath, CancellationToken ct, bool deferCacheSave)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return string.Empty;
            }

            string full = Path.GetFullPath(imagePath);
            string ext = Path.GetExtension(full);
            if (!IsSupportedExtension(ext))
            {
                return string.Empty;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(full);
                if (!info.Exists || info.Length > MaxImageBytes)
                {
                    return string.Empty;
                }
            }
            catch
            {
                return string.Empty;
            }

            string key = full.ToLowerInvariant();
            long mtime = info.LastWriteTimeUtc.Ticks;
            long length = info.Length;

            // キャッシュヒット（mtime/length 一致）なら再 OCR しない。
            Dictionary<string, CacheEntry> cache = LoadCache();
            lock (CacheLock)
            {
                if (cache.TryGetValue(key, out CacheEntry? hit)
                    && hit.MtimeUtcTicks == mtime
                    && hit.Length == length)
                {
                    return hit.Text;
                }
            }

            OcrEngine? engine = GetEngine();
            if (engine == null)
            {
                // エンジン無効時はキャッシュせず空を返す（後から言語パック導入時に再試行できる）。
                return string.Empty;
            }

            string? text = RunOcr(engine, full, ct);
            if (text == null)
            {
                // 読取失敗（破損・共有違反・一時エラー）はキャッシュせず、失敗として返す。
                return null;
            }

            lock (CacheLock)
            {
                cache[key] = new CacheEntry
                {
                    MtimeUtcTicks = mtime,
                    Length = length,
                    Text = text,
                };
            }
            if (deferCacheSave)
            {
                _cacheDirty = true;
            }
            else
            {
                SaveCache();
            }
            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 想定外の失敗は機能単位で無効化（空文字）。
            return string.Empty;
        }
    }

    private static bool IsSupportedExtension(string ext)
    {
        foreach (string s in SupportedExtensions)
        {
            if (ext.Equals(s, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ================================================================
    //  OCR 実行
    // ================================================================

    private static OcrEngine? GetEngine()
    {
        if (_engineInitialized)
        {
            return _engine;
        }
        lock (EngineLock)
        {
            if (!_engineInitialized)
            {
                try
                {
                    _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                }
                catch
                {
                    _engine = null;
                }
                _engineInitialized = true;
            }
        }
        return _engine;
    }

    /// <summary>
    /// 遅延保存（deferCacheSave）分をディスクへ反映する。未保存分が無ければ何もしない。
    /// 冪等・失敗無視。
    /// </summary>
    internal static void FlushCache()
    {
        if (!_cacheDirty)
        {
            return;
        }
        // 保存開始前に false へ戻す（保存中の並行更新を dirty として残すため）。
        // 保存に失敗したら dirty に戻し、次回の Flush で再試行させる。
        _cacheDirty = false;
        if (!SaveCache())
        {
            _cacheDirty = true;
        }
    }

    // BGRA8 展開後の総ピクセル数上限（25M ピクセル ≒ 100MB）。
    // 圧縮率の高い巨大画像（展開爆弾）によるメモリ枯渇を防ぐ。
    private const long MaxImagePixels = 25_000_000;

    // OcrEngine はプロセス内共有だが RecognizeAsync のスレッド安全性は WinRT 実装依存の
    // ため、認識処理は 1 件ずつ直列化する（索引と対話検索が並行しても安全に）。
    private static readonly SemaphoreSlim OcrGate = new(1, 1);

    /// <summary>
    /// FileStream → BitmapDecoder → SoftwareBitmap(Bgra8, Premultiplied) → OCR。
    /// デコード前に画像寸法を検査し、OCR エンジンの上限・総ピクセル上限を超えるものは
    /// 読み込まない。読取失敗は null（キャッシュ対象外）、文字なし成功は空文字を返す。
    /// キャンセルは OperationCanceledException として伝播する。
    /// </summary>
    private static string? RunOcr(OcrEngine engine, string path, CancellationToken ct)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var raStream = stream.AsRandomAccessStream();
            BitmapDecoder decoder = BitmapDecoder
                .CreateAsync(raStream)
                .AsTask(ct).GetAwaiter().GetResult();

            // 展開後サイズの事前検査（圧縮ファイルサイズでは防げない展開爆弾対策）。
            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;
            uint maxDim = OcrEngine.MaxImageDimension;
            if (width == 0 || height == 0
                || width > maxDim || height > maxDim
                || (long)width * height > MaxImagePixels)
            {
                return string.Empty; // 対象外として確定（成功扱いでキャッシュしてよい）
            }

            using SoftwareBitmap bitmap = decoder
                .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                .AsTask(ct).GetAwaiter().GetResult();

            OcrResult result;
            OcrGate.Wait(ct);
            try
            {
                result = engine.RecognizeAsync(bitmap).AsTask(ct).GetAwaiter().GetResult();
            }
            finally
            {
                OcrGate.Release();
            }

            var sb = new StringBuilder();
            foreach (OcrLine line in result.Lines)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(line.Text);
            }
            return sb.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // 読取失敗（キャッシュしない）
        }
    }

    // ================================================================
    //  キャッシュ入出力（%LocalAppData%\Hirake\ocr\cache.json）
    // ================================================================

    private static Dictionary<string, CacheEntry> LoadCache()
    {
        lock (CacheLock)
        {
            if (_cache != null)
            {
                return _cache;
            }

            var map = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(CacheFilePath))
                {
                    string json = File.ReadAllText(CacheFilePath, Encoding.UTF8);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                    if (loaded != null)
                    {
                        map = new Dictionary<string, CacheEntry>(loaded, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                // 破損 JSON は空から作り直す。
                map = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            }
            _cache = map;
            return _cache;
        }
    }

    /// <returns>保存に成功（または保存対象なし）で true、失敗で false。</returns>
    private static bool SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            string dest = CacheFilePath;
            string tmp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";

            // スナップショット作成から置換までを同一ロックで直列化する
            // （並行実行時に古いスナップショットが後勝ちして新エントリを失わないように）。
            lock (CacheSaveLock)
            {
                Dictionary<string, CacheEntry> snapshot;
                lock (CacheLock)
                {
                    if (_cache == null)
                    {
                        return true;
                    }
                    snapshot = new Dictionary<string, CacheEntry>(_cache, StringComparer.OrdinalIgnoreCase);
                }

                string json = JsonSerializer.Serialize(snapshot);
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, dest, overwrite: true);
            }
            return true;
        }
        catch
        {
            // 保存失敗は握りつぶす（呼び出し側が dirty 復帰・再試行を判断する）。
            return false;
        }
    }
}
