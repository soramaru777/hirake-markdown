using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Hirake;

/// <summary>
/// 外部エディタの起動（ISSUE #55）。
///
/// ■ コマンドラインを組み立てない
///
/// exe と引数**配列**を持ち、<see cref="ProcessStartInfo.ArgumentList"/> へ 1 要素ずつ
/// 積む。文字列連結も引用符付けも自前で行わないので、空白・日本語・&amp;・引用符を
/// 含むパスで壊れる余地と、コマンド注入の経路が構造的に無い。
///
/// ■ UseShellExecute は必ず false
///
/// true にすると OS の関連付けが働く。.md の既定プログラムは Hirake であることが
/// 多いため（register.ps1 は既定が未設定なら Hirake を既定にする）、
/// **自分自身がもう 1 タブ開くだけ**という状態になる。ISSUE の成功条件で明確に
/// 禁止されている挙動なので、true にする経路をコード上に作らない。
/// </summary>
internal static class EditorLauncher
{
    /// <summary>引数 1 要素の長さ上限。設定が壊れているときに巨大な引数を渡さない。</summary>
    private const int MaxArgumentLength = 1024;

    /// <summary>引数の要素数上限。</summary>
    private const int MaxArgumentCount = 16;

    /// <summary>
    /// ファイルを開く。行が取れない・行ジャンプ非対応なら
    /// <see cref="EditorSettings.NoLineArgs"/> へ落とす（起動そのものは諦めない）。
    /// </summary>
    public static bool TryOpenFile(
        EditorSettings editor, string file, int? line, out string error)
    {
        error = string.Empty;

        if (!File.Exists(file))
        {
            error = $"ファイルが見つかりません: {file}";
            return false;
        }

        // settings.json は手で編集できるため、null が入っている前提で扱う。
        IReadOnlyList<string> fileArgs = editor.FileArgs ?? (IReadOnlyList<string>)Array.Empty<string>();
        IReadOnlyList<string> noLineArgs = editor.NoLineArgs ?? (IReadOnlyList<string>)Array.Empty<string>();

        bool useLine = editor.SupportsLine && line is > 0 && fileArgs.Count > 0;
        IReadOnlyList<string> template = useLine ? fileArgs : noLineArgs;
        string? folder = Path.GetDirectoryName(file);

        return TryLaunch(editor, template, file, useLine ? line : null, folder, out error);
    }

    /// <summary>フォルダを開く。</summary>
    public static bool TryOpenFolder(EditorSettings editor, string folder, out string error)
    {
        error = string.Empty;

        if (!Directory.Exists(folder))
        {
            error = $"フォルダが見つかりません: {folder}";
            return false;
        }

        // settings.json は手で編集できる。null が入っていても落とさない。
        IReadOnlyList<string> template = editor.FolderArgs ?? (IReadOnlyList<string>)Array.Empty<string>();
        if (template.Count == 0)
        {
            error = $"{editor.Name} はフォルダを開く指定がありません"
                    + "（settings.json の FolderArgs に指定を足すと使えます）。";
            return false;
        }

        return TryLaunch(editor, template, file: string.Empty, line: null, folder, out error);
    }

    private static bool TryLaunch(
        EditorSettings editor,
        IReadOnlyList<string> template,
        string file,
        int? line,
        string? folder,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(editor.ExePath))
        {
            error = "エディタが設定されていません。";
            return false;
        }
        if (!File.Exists(editor.ExePath))
        {
            error = $"エディタが見つかりません: {editor.ExePath}";
            Diagnostics.Record("editor", "設定されたエディタが存在しません", editor.ExePath);
            return false;
        }
        if (template.Count is 0 or > MaxArgumentCount)
        {
            error = $"引数の指定が不正です（{template.Count} 個）。settings.json を確認してください。";
            return false;
        }

        List<string> args = BuildArguments(template, file, line, folder);
        foreach (string arg in args)
        {
            if (arg.Length > MaxArgumentLength)
            {
                error = "引数が長すぎます。settings.json を確認してください。";
                return false;
            }
        }

        var info = new ProcessStartInfo(editor.ExePath)
        {
            // シェルを経由しない。関連付けで自分自身が開くのを防ぐ（上のコメント参照）。
            UseShellExecute = false,
            WorkingDirectory = folder ?? string.Empty,
        };
        foreach (string arg in args)
        {
            // エスケープは .NET に任せる。ここで引用符を足さないこと。
            info.ArgumentList.Add(arg);
        }

        try
        {
            // 待たない。エディタは常駐するのが普通で、待つと Hirake が固まる。
            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Diagnostics.Record(
                "editor", "エディタの起動に失敗しました", Describe(editor.ExePath, args) + " / " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// テンプレートを実際の値へ置換する。**要素の境界は動かさない**
    /// （1 要素の中だけで置換する）。
    /// </summary>
    internal static List<string> BuildArguments(
        IReadOnlyList<string> template, string file, int? line, string? folder)
    {
        string lineText = (line is > 0 ? line.Value : 1).ToString(CultureInfo.InvariantCulture);
        var list = new List<string>(template.Count);

        foreach (string item in template)
        {
            // 配列に null が混ざっていても落とさない（settings.json は手で書ける）。
            string value = (item ?? string.Empty)
                .Replace("{file}", file, StringComparison.Ordinal)
                .Replace("{folder}", folder ?? string.Empty, StringComparison.Ordinal)
                .Replace("{line}", lineText, StringComparison.Ordinal);
            list.Add(value);
        }
        return list;
    }

    /// <summary>失敗表示・診断ログ用に「何を実行しようとしたか」を組み立てる。</summary>
    internal static string Describe(string exePath, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder(exePath);
        foreach (string arg in args)
        {
            sb.Append(' ').Append(arg);
        }
        return sb.ToString();
    }
}
