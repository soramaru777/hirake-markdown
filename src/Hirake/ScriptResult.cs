namespace Hirake;

/// <summary>ページ側で公開されている関数を呼んだ結果の種別。</summary>
public enum ScriptStatus
{
    /// <summary>スクリプトが成功を返した。</summary>
    Ok,

    /// <summary>
    /// 実行する状況になかった。CoreWebView2 が未初期化、関数がまだ公開されていない、
    /// 実行中にタブが閉じられた、など。**失敗ではない**ので記録しない。
    /// テーマ等は初回ロード時にテンプレートへ埋め込まれるため、ここを失敗として
    /// 数えると起動のたびに大量の記録が出る。
    /// </summary>
    Skipped,

    /// <summary>スクリプト側で例外が起きた。</summary>
    ScriptError,

    /// <summary>ExecuteScriptAsync 自体が失敗した（WebView2 の異常など）。</summary>
    HostError,
}

/// <summary>
/// <see cref="DocumentTab.RunScriptAsync"/> の結果。
/// 呼び出し側は <see cref="ShouldRecord"/> が true のときだけ記録すればよい。
/// </summary>
public sealed record ScriptResult(ScriptStatus Status, string? Detail)
{
    public static readonly ScriptResult Ok = new(ScriptStatus.Ok, null);
    public static readonly ScriptResult Skipped = new(ScriptStatus.Skipped, null);

    public static ScriptResult ScriptError(string detail) => new(ScriptStatus.ScriptError, detail);

    public static ScriptResult HostError(string detail) => new(ScriptStatus.HostError, detail);

    /// <summary>診断へ記録すべき結果か。</summary>
    public bool ShouldRecord => Status is ScriptStatus.ScriptError or ScriptStatus.HostError;

    /// <summary>記録に使う短い説明。</summary>
    public string Describe(string what) => Status switch
    {
        ScriptStatus.ScriptError => what + "に失敗しました",
        ScriptStatus.HostError => what + "のスクリプト実行に失敗しました",
        _ => what,
    };
}
