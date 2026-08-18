using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Hirake;

/// <summary>
/// エディタ選択ウィンドウ（ISSUE #55）。初回に「エディタで開く」を使ったときだけ出す。
///
/// 「どこで見つけたか」を必ず表示する。同名の実行ファイルが複数見つかることがあり、
/// どれを選んでいるのか判断できないと、意図しないエディタを設定してしまう。
/// 行ジャンプ非対応であることも選ぶ前に示す（選んだ後で気づくのを避ける）。
/// </summary>
public partial class EditorPickerWindow : Window
{
    /// <summary>一覧に出す 1 行。</summary>
    private sealed class Row
    {
        public required string Name { get; init; }
        public required string ExePath { get; init; }
        public required string Detail { get; init; }
        public required EditorSettings Settings { get; init; }
    }

    /// <summary>選ばれたエディタ。キャンセルなら null。</summary>
    public EditorSettings? Selected { get; private set; }

    internal EditorPickerWindow(IReadOnlyList<EditorCandidate> candidates)
    {
        InitializeComponent();

        var rows = new List<Row>();
        foreach (EditorCandidate candidate in candidates)
        {
            string detail = $"見つけた場所: {candidate.SourceLabel}";
            if (!candidate.Preset.SupportsLine)
            {
                detail += " ／ ※ 行ジャンプには対応していません";
            }

            rows.Add(new Row
            {
                Name = candidate.Name,
                ExePath = candidate.ExePath,
                Detail = detail,
                Settings = candidate.Preset.ToSettings(candidate.ExePath),
            });
        }

        CandidateList.ItemsSource = rows;
        if (rows.Count > 0)
        {
            CandidateList.SelectedIndex = 0;
        }
        else
        {
            HeaderText.Text =
                "エディタが見つかりませんでした。"
                + "「一覧に無い…（exe を選ぶ）」から実行ファイルを指定してください。";
            OkButton.IsEnabled = false;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Commit();

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    private void Commit()
    {
        if (CandidateList.SelectedItem is not Row row)
        {
            return;
        }
        Selected = row.Settings;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 一覧に無いエディタを手で指定する。引数は分からないので
    /// 「ファイルを開くだけ」から始め、行ジャンプが要る場合は
    /// settings.json を編集してもらう（設定 GUI は作らない＝ISSUE の非スコープ）。
    /// </summary>
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "エディタの実行ファイルを選ぶ",
            Filter = "実行ファイル (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string path = dialog.FileName;
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                ".exe を指定してください（.cmd / .bat は起動できません）。",
                "エディタを選ぶ",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Selected = new EditorSettings
        {
            Id = null,
            Name = Path.GetFileNameWithoutExtension(path),
            ExePath = path,
            FileArgs = new List<string> { "{file}" },
            NoLineArgs = new List<string> { "{file}" },
            FolderArgs = new List<string>(),
            SupportsLine = false,
        };
        DialogResult = true;
    }
}
