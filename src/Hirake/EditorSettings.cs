using System.Collections.Generic;

namespace Hirake;

/// <summary>
/// 「エディタで開く」に使う外部エディタの設定（ISSUE #55）。
///
/// プリセットを**参照**せず、選んだ時点の内容を**コピーして**保存する。
/// 参照にすると、同梱の editors.json を書き換えたときや Hirake を更新したときに
/// 黙って挙動が変わる。コピーなら settings.json を見れば何が実行されるか分かる。
///
/// 引数は 1 本の文字列ではなく配列で持つ。<see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
/// へ 1 要素ずつ積めば、空白・日本語・&amp; を含むパスのエスケープを .NET に任せられる。
/// </summary>
public sealed class EditorSettings
{
    /// <summary>プリセットの id（"vscode" 等）。手動で指定した場合は null。</summary>
    public string? Id { get; set; }

    /// <summary>メニューに出す表示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>解決済みの実行ファイルのフルパス。</summary>
    public string ExePath { get; set; } = string.Empty;

    /// <summary>行ジャンプありでファイルを開くときの引数。</summary>
    public List<string> FileArgs { get; set; } = new();

    /// <summary>
    /// 行が取れないとき・<see cref="SupportsLine"/> が false のときの引数。
    /// 「{line} を含む要素を落とす」方式にはしない（"-g" だけ残って壊れるため）。
    /// </summary>
    public List<string> NoLineArgs { get; set; } = new();

    /// <summary>フォルダを開くときの引数。空ならメニュー項目を無効化する。</summary>
    public List<string> FolderArgs { get; set; } = new();

    /// <summary>行ジャンプに対応しているか。</summary>
    public bool SupportsLine { get; set; }
}
