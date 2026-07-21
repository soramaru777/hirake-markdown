using System.IO;
using System.Text.Json;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;

namespace Hirake;

/// <summary>構造クエリで抽出した 1 ブロック。</summary>
public sealed class StructureItem
{
    /// <summary>"heading" / "task" / "li" / "para"。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>見出しレベル（heading のみ 1〜6、それ以外は 0）。</summary>
    public int Level { get; init; }

    /// <summary>ブロックの平文（表示・キーワード照合用。長文は末尾を切り詰め済み）。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>ソース行番号（1 始まり。ソースマップ基盤の data-src-line と同じ規約）。</summary>
    public int Line { get; init; }

    /// <summary>タスク項目の完了状態（task のみ有効）。</summary>
    public bool Checked { get; init; }
}

/// <summary>1 ファイル分の抽出結果。</summary>
public sealed class StructureFileResult
{
    public string FullPath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public List<StructureItem> Items { get; init; } = new();
}

/// <summary>構造クエリの実行結果（対象フォルダ配下の全ファイル分）。</summary>
public sealed class StructureQueryResult
{
    public string RootFolder { get; init; } = string.Empty;
    public List<StructureFileResult> Files { get; init; } = new();

    /// <summary>走査上限（ファイル数）に到達し、一部が未走査のとき true。</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// フォルダ配下の全 Markdown から構造要素（見出し・タスク・箇条書き・段落）を
/// Markdig AST で抽出する。意味索引（モデル・ベクトル）には一切依存しない。
/// ファイル列挙・上限は FolderSearchService と同じ規則を共有する。
/// </summary>
public static class StructureQueryService
{
    // 1 ファイルあたりの抽出ブロック上限（巨大文書のガード）。
    private const int MaxItemsPerFile = 500;

    // 全ファイル合計の照合テキスト上限（文字数）。ブロック単位の切り詰めは行わず
    // （切り詰めるとキーワード照合が静かに取りこぼす）、合計がこれを超えたら
    // 以降のファイルを打ち切って Truncated で明示する。表示側の切り詰めは
    // ブラウザ（structure.js の表示プレビュー）が担う。
    private const long MaxTotalTextChars = 8_000_000;

    /// <summary>rootFolder 配下を走査して構造要素を抽出する（同期・CPU バウンド）。</summary>
    public static StructureQueryResult Build(string rootFolder, CancellationToken token)
    {
        string fullRoot = Path.GetFullPath(rootFolder);
        var files = new List<StructureFileResult>();
        int scanned = 0;
        long totalChars = 0;
        bool truncated = false;

        foreach (string file in FolderSearchService.EnumerateMarkdownFiles(fullRoot, token))
        {
            token.ThrowIfCancellationRequested();

            // 上限到達後は列挙自体を打ち切る（巨大フォルダを数えるためだけに歩かない）。
            if (scanned >= FolderSearchService.MaxFiles)
            {
                truncated = true;
                break;
            }
            scanned++;

            StructureFileResult? result = ExtractFile(fullRoot, file, out bool itemsCapped, token);
            if (itemsCapped)
            {
                truncated = true;
            }
            if (result != null && result.Items.Count > 0)
            {
                files.Add(result);

                foreach (StructureItem item in result.Items)
                {
                    totalChars += item.Text.Length;
                }
                if (totalChars > MaxTotalTextChars)
                {
                    truncated = true;
                    break;
                }
            }
        }

        return new StructureQueryResult
        {
            RootFolder = fullRoot,
            Files = files,
            Truncated = truncated,
        };
    }

    private static StructureFileResult? ExtractFile(
        string root, string file, out bool itemsCapped, CancellationToken token)
    {
        itemsCapped = false;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > FolderSearchService.MaxFileBytes)
            {
                return null;
            }

            // UTF-8 厳密 → Shift-JIS フォールバック（表示側と同じ読込規則）。
            string markdown = MarkdownRenderer.ReadFileText(file);

            // 表示側と同じパイプラインで解析する（行番号が data-src-line と一致する）。
            MarkdownDocument document = Markdown.Parse(markdown, MarkdownRenderer.Pipeline);

            var items = new List<StructureItem>();
            foreach (Block block in document)
            {
                token.ThrowIfCancellationRequested();
                if (items.Count >= MaxItemsPerFile)
                {
                    break;
                }
                CollectBlock(block, items);
            }

            // 上限いっぱいまで達したファイルは打ち切りの可能性があるため明示する
            // （ちょうど上限件数のファイルも保守的に打ち切り扱いになる）。
            itemsCapped = items.Count >= MaxItemsPerFile;

            return new StructureFileResult
            {
                FullPath = file,
                FileName = Path.GetFileName(file),
                RelativePath = MakeRelative(root, file),
                Items = items,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 個別ファイルの読込・解析失敗はスキップする（全体を止めない）。
            return null;
        }
    }

    /// <summary>
    /// ブロックを再帰走査して構造要素を収集する。
    /// リスト項目の直下段落は項目本文として扱い、段落単体としては数えない。
    /// コードブロック・HTML ブロックは対象外。
    /// </summary>
    private static void CollectBlock(Block block, List<StructureItem> items)
    {
        if (items.Count >= MaxItemsPerFile)
        {
            return;
        }

        switch (block)
        {
            case HeadingBlock heading:
                AddItem(items, "heading", heading.Level, ExtractText(heading), heading.Line + 1, false);
                return;

            case ListItemBlock listItem:
                CollectListItem(listItem, items);
                return;

            case ParagraphBlock paragraph:
                AddItem(items, "para", 0, ExtractText(paragraph), paragraph.Line + 1, false);
                return;

            case Markdig.Extensions.Tables.Table:
                // 表のセルは段落として数えない（抽出対象は見出し・箇条書き・地の段落のみ）。
                return;

            case ContainerBlock container:
                // ListBlock / QuoteBlock 等は子へ降りる。
                foreach (Block child in container)
                {
                    CollectBlock(child, items);
                }
                return;

            default:
                // コードブロック・HTML ブロック・区切り線などは抽出対象外。
                return;
        }
    }

    private static void CollectListItem(ListItemBlock listItem, List<StructureItem> items)
    {
        // 項目本文 = 直下の段落のみ（ネストした子リストは別項目として再帰で拾う）。
        var textParts = new List<string>();
        bool isTask = false;
        bool isChecked = false;
        bool first = true;

        foreach (Block child in listItem)
        {
            if (child is ParagraphBlock paragraph)
            {
                if (first && paragraph.Inline?.FirstChild is TaskList task)
                {
                    isTask = true;
                    isChecked = task.Checked;
                }
                string text = ExtractText(paragraph);
                if (text.Length > 0)
                {
                    textParts.Add(text);
                }
            }
            first = false;
        }

        string joined = string.Join(" ", textParts);
        if (joined.Length > 0)
        {
            AddItem(items, isTask ? "task" : "li", 0, joined, listItem.Line + 1, isChecked);
        }

        // ネストしたリスト・引用等は個別項目として収集する。
        foreach (Block child in listItem)
        {
            if (child is not ParagraphBlock)
            {
                CollectBlock(child, items);
            }
        }
    }

    private static void AddItem(
        List<StructureItem> items, string kind, int level, string text, int line, bool isChecked)
    {
        if (items.Count >= MaxItemsPerFile || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        items.Add(new StructureItem
        {
            Kind = kind,
            Level = level,
            Text = text,
            Line = line,
            Checked = isChecked,
        });
    }

    private static string ExtractText(MarkdownObject block)
    {
        // 意味索引と同じ平文抽出ヘルパーを共用する。切り詰めはしない
        // （キーワード照合の取りこぼし防止。全体量は MaxTotalTextChars で制御）。
        return SemanticIndexService.ExtractPlainText(block);
    }

    private static string MakeRelative(string root, string file)
    {
        try
        {
            string rel = Path.GetRelativePath(root, file);
            return string.IsNullOrEmpty(rel) ? file : rel;
        }
        catch
        {
            return file;
        }
    }

    /// <summary>
    /// テンプレートの &lt;script&gt; へ直接埋め込める JSON を生成する
    /// （System.Text.Json の既定エスケープにより &lt; などは \uXXXX 化される）。
    /// </summary>
    public static string ToJson(StructureQueryResult result)
    {
        string folderLabel = Path.GetFileName(
            result.RootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderLabel))
        {
            folderLabel = result.RootFolder;
        }

        var files = result.Files
            .Select(f => new
            {
                path = f.FullPath,
                name = f.FileName,
                rel = f.RelativePath,
                items = f.Items.Select(i => new
                {
                    kind = i.Kind,
                    level = i.Level,
                    text = i.Text,
                    line = i.Line,
                    @checked = i.Checked,
                }).ToList(),
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            root = result.RootFolder,
            folderLabel,
            truncated = result.Truncated,
            files,
        });
    }
}
