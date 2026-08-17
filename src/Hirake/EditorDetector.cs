using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Hirake;

/// <summary>候補をどこで見つけたか。利用者に提示して選ばせるために持つ。</summary>
internal enum EditorSource
{
    /// <summary>プリセットの既知インストール先。</summary>
    KnownPath,

    /// <summary>環境変数 PATH。</summary>
    Path,

    /// <summary>App Paths レジストリ。</summary>
    AppPaths,
}

/// <summary>検出したエディタ 1 件。</summary>
/// <param name="Preset">元になったプリセット。</param>
/// <param name="ExePath">実行ファイルのフルパス。</param>
/// <param name="Source">見つけた場所。</param>
internal readonly record struct EditorCandidate(
    EditorPreset Preset, string ExePath, EditorSource Source)
{
    public string Name => Preset.Name;

    public string SourceLabel => Source switch
    {
        EditorSource.KnownPath => "既知のインストール先",
        EditorSource.Path => "PATH",
        _ => "App Paths レジストリ",
    };
}

/// <summary>
/// インストール済みエディタの検出（ISSUE #55）。
///
/// ■ App Paths だけでは足りない（実測）
///
/// ISSUE 起票時の案は App Paths レジストリを引く方針だったが、開発機で実測したところ
/// 登録は 84 件あるのに、インストール済みの VS Code もサクラエディタも含まれていなかった
/// （エディタとして使えるのは notepad / devenv / WinMerge 程度）。一方 PATH からは
/// code.cmd が引けた。したがって「既知のインストール先 → PATH → App Paths」の
/// 3 経路を併用しないと、実際に入っているエディタを取りこぼす。
///
/// ■ 起動時には走らせない
///
/// 「エディタで開く」が初めて使われたときに 1 回だけ実行する。File.Exists の数十回
/// 程度なので体感には出ないが、使わない人に払わせる理由もない。
/// </summary>
internal static class EditorDetector
{
    /// <summary>
    /// 候補を列挙する。同じ実行ファイルは 1 件にまとめ、見つけた順
    /// （既知パス → PATH → App Paths）を保つ。
    /// </summary>
    public static IReadOnlyList<EditorCandidate> Detect(IReadOnlyList<EditorPreset> presets)
    {
        var results = new List<EditorCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (EditorPreset preset in presets)
        {
            foreach (string raw in preset.KnownPaths)
            {
                TryAdd(results, seen, preset, Expand(raw), EditorSource.KnownPath);
            }
        }

        foreach (EditorPreset preset in presets)
        {
            foreach (string exeName in preset.ExeNames)
            {
                TryAdd(results, seen, preset, FindOnPath(exeName), EditorSource.Path);
            }
        }

        foreach (EditorPreset preset in presets)
        {
            foreach (string exeName in preset.ExeNames)
            {
                TryAdd(results, seen, preset, FindInAppPaths(exeName), EditorSource.AppPaths);
            }
        }

        return results;
    }

    private static void TryAdd(
        List<EditorCandidate> results,
        HashSet<string> seen,
        EditorPreset preset,
        string? path,
        EditorSource source)
    {
        if (path == null || !IsLaunchable(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        if (!File.Exists(full) || !seen.Add(full))
        {
            return;
        }
        results.Add(new EditorCandidate(preset, full, source));
    }

    /// <summary>
    /// 起動対象にしてよい実行ファイルか。
    ///
    /// .cmd / .bat は採らない。UseShellExecute = false では直接起動できず、
    /// cmd.exe を噛ませることになる（＝シェルを経由する経路を作ってしまう）。
    /// VS Code は PATH に code.cmd が出るが、実体の Code.exe を既知パスで拾う。
    /// </summary>
    private static bool IsLaunchable(string path)
        => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>環境変数を展開する（%LOCALAPPDATA% など）。</summary>
    private static string Expand(string value)
    {
        try
        {
            return Environment.ExpandEnvironmentVariables(value);
        }
        catch
        {
            return value;
        }
    }

    /// <summary>PATH の各フォルダから実行ファイルを探す。</summary>
    private static string? FindOnPath(string exeName)
    {
        string? pathVar;
        try
        {
            pathVar = Environment.GetEnvironmentVariable("PATH");
        }
        catch
        {
            return null;
        }
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        foreach (string entry in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // PATH の要素は引用符で囲まれていることがある（"C:\Program Files\Editor"）。
            // 外したうえで結合しないと、引用符が残って File.Exists が必ず外れる。
            string dir = entry.Trim().Trim('"');
            if (dir.Length == 0)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(dir, exeName);
            }
            catch
            {
                continue; // パスに使えない文字が入っている要素は飛ばす。
            }
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// App Paths レジストリから実行ファイルを探す。
    /// HKCU → HKLM → WOW6432Node の順（利用者ごとの設定を優先する）。
    /// </summary>
    private static string? FindInAppPaths(string exeName)
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\",
        ];

        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string root in roots)
            {
                try
                {
                    using RegistryKey? key = hive.OpenSubKey(root + exeName);
                    if (key?.GetValue(null) is string value && value.Length > 0)
                    {
                        return value.Trim('"');
                    }
                }
                catch
                {
                    // 読めないキーは飛ばす（権限・破損）。
                }
            }
        }
        return null;
    }
}
