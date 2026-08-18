using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hirake;

/// <summary>editors.json の 1 エントリ（ISSUE #55）。</summary>
internal sealed class EditorPreset
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>PATH / App Paths を引くときの実行ファイル名。</summary>
    public List<string> ExeNames { get; set; } = new();

    /// <summary>既知のインストール先。環境変数（%LOCALAPPDATA% 等）を含んでよい。</summary>
    public List<string> KnownPaths { get; set; } = new();

    public List<string> FileArgs { get; set; } = new();
    public List<string> NoLineArgs { get; set; } = new();
    public List<string> FolderArgs { get; set; } = new();
    public bool SupportsLine { get; set; }

    /// <summary>実機で確認した日（未確認は null）。</summary>
    public string? Verified { get; set; }

    /// <summary>選ばれたときに settings.json へ保存する形へ写す。</summary>
    public EditorSettings ToSettings(string exePath) => new()
    {
        Id = Id,
        Name = Name,
        ExePath = exePath,
        FileArgs = new List<string>(FileArgs),
        NoLineArgs = new List<string>(NoLineArgs),
        FolderArgs = new List<string>(FolderArgs),
        SupportsLine = SupportsLine,
    };
}

/// <summary>editors.json 全体。</summary>
internal sealed class EditorPresetFile
{
    public int Version { get; set; }
    public List<EditorPreset> Editors { get; set; } = new();

    /// <summary>未検証のプリセット。候補には出さない（利用者が editors へ移して使う）。</summary>
    public List<EditorPreset> Unverified { get; set; } = new();
}

/// <summary>
/// 同梱プリセット（<c>Assets/editors.json</c>）の読み込み（ISSUE #55）。
///
/// 同梱するのは**実機で確認できたものだけ**にする。動かないプリセットは、
/// 動かないうえに原因が分からないため、無い方がまだ良い。未確認のものは
/// <c>unverified</c> に置き、利用者が自分で有効化する。
///
/// ファイルが無い・壊れていても落とさない。エディタが選べないだけで、
/// 文書の表示という本来の機能は止めない。
/// </summary>
internal static class EditorPresets
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>プリセットを読む。読めなければ空。</summary>
    public static IReadOnlyList<EditorPreset> Load(string assetsDirectory)
    {
        string path = Path.Combine(assetsDirectory, "editors.json");
        try
        {
            if (!File.Exists(path))
            {
                Diagnostics.Record("editor", "editors.json が見つかりません", path);
                return Array.Empty<EditorPreset>();
            }

            string json = File.ReadAllText(path);
            EditorPresetFile? file = JsonSerializer.Deserialize<EditorPresetFile>(json, Options);
            if (file?.Editors == null)
            {
                return Array.Empty<EditorPreset>();
            }

            var list = new List<EditorPreset>();
            foreach (EditorPreset preset in file.Editors)
            {
                if (IsUsable(preset))
                {
                    list.Add(preset);
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            Diagnostics.Record("editor", "editors.json を読めませんでした", ex);
            return Array.Empty<EditorPreset>();
        }
    }

    /// <summary>最低限の形が揃っているか（壊れたエントリを候補に出さない）。</summary>
    private static bool IsUsable(EditorPreset? preset)
        => preset != null
           && !string.IsNullOrWhiteSpace(preset.Id)
           && !string.IsNullOrWhiteSpace(preset.Name)
           && preset.NoLineArgs.Count > 0
           && (preset.ExeNames.Count > 0 || preset.KnownPaths.Count > 0);
}
