using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Hirake;

public partial class MainWindow : Window, IDocumentTabHost
{
    private readonly string _assetsDirectory;
    private readonly string _tempDirectory;
    private readonly string _clipboardDirectory;

    private bool _applyingZoom;

    // ---- サイドバー（ファイルツリー / 横断検索）状態 ----
    private const double DefaultSidebarWidth = 240;
    private bool _sidebarVisible;
    private string? _currentRootFolder;
    private FileTreeItem? _activeTreeItem;
    private GridLength _savedSidebarWidth = new(DefaultSidebarWidth);

    // ツリー構築の世代カウンタ。連続タブ切替時に最新要求の結果だけを反映するために使う。
    private int _treeBuildGeneration;

    private DispatcherTimer? _searchDebounce;
    private CancellationTokenSource? _searchCts;

    // 意味（セマンティック）検索モード。ON のときは Enter 実行のみ（インクリメンタル検索は無効）。
    private bool _semanticMode;

    public ObservableCollection<DocumentTab> Tabs { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        _tempDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hirake",
            "temp");
        _clipboardDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hirake",
            "clipboard");

        // 起動時に古いクリップボード一時ファイルを軽く掃除する。
        CleanupClipboardFiles();

        UpdateEmptyState();
        UpdateTitle();

        // 保存済みテーマで WPF クローム・テーマボタンを初期化する。
        ApplyEffectiveTheme();

        // 保存済みのサイドバー表示状態を復元する（ツリー内容はタブ確定時に構築する）。
        SetSidebarVisible(SettingsStore.Instance.SidebarVisible, persist: false);

        // Theme="auto" のとき OS のアプリテーマ変更へ追従するために購読する。
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    // ---- ファイルを開く -----------------------------------------------

    public void OpenFile(string path) => OpenFile(path, null);

    /// <summary>ファイルをタブで開く。line 指定時は該当ソース行（1 始まり）へスクロールする。</summary>
    public void OpenFile(string path, int? line)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        // 同一ファイル（大文字小文字無視）は既存タブをアクティブ化する。
        var existing = Tabs.FirstOrDefault(
            t => string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabList.SelectedItem = existing;
            if (line is int existingLine)
            {
                existing.ScrollToSourceLine(existingLine);
            }
            return;
        }

        var tab = new DocumentTab(fullPath, this, _assetsDirectory, _tempDirectory);
        if (line is int newLine)
        {
            // 初期化前のため保留され、初回ロード完了時に適用される。
            tab.ScrollToSourceLine(newLine);
        }
        tab.WebView.Visibility = Visibility.Collapsed;
        WebViewHost.Children.Add(tab.WebView);
        Tabs.Add(tab);
        TabList.SelectedItem = tab;

        UpdateEmptyState();
    }

    // ---- ナレッジグラフ・ビュー ---------------------------------------

    /// <summary>
    /// アクティブタブのフォルダを起点にナレッジグラフ・ビューを開く。
    /// 同一フォルダのグラフタブが既にあればアクティブ化のみ行う。
    /// </summary>
    private void OpenGraphView()
    {
        // 仮想タブの FilePath はフォルダ自身のため GetDirectoryName すると親に化ける。
        // アクティブタブの種別ごとにスコープを直接決める（_currentRootFolder は
        // サイドバー追従で同様に親へずれることがあるため最後のフォールバックのみ）。
        string? root;
        switch (TabList.SelectedItem)
        {
            case GraphTab:
                return; // 既にグラフビューがアクティブ。
            case StructureQueryTab structure:
                root = structure.RootFolder;
                break;
            case FingerprintTab fingerprint:
                root = fingerprint.RootFolder;
                break;
            case StatsTab stats:
                root = stats.RootFolder;
                break;
            case CanvasTab canvas:
                root = canvas.RootFolder;
                break;
            case DocumentTab active:
                try
                {
                    root = Path.GetDirectoryName(active.FilePath);
                }
                catch
                {
                    root = null;
                }
                break;
            default:
                root = null;
                break;
        }
        root ??= _currentRootFolder;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        // サブフォルダから開いても親フォルダ側のリンクが漏れないよう、
        // ワークスペースルート（md を直接含む最上位フォルダ）を起点にする。
        string fullRoot = LinkGraphService.FindWorkspaceRoot(root);
        var existing = Tabs.OfType<GraphTab>().FirstOrDefault(
            t => string.Equals(t.RootFolder, fullRoot, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabList.SelectedItem = existing;
            return;
        }

        var tab = new GraphTab(fullRoot, this, _assetsDirectory, _tempDirectory);
        tab.WebView.Visibility = Visibility.Collapsed;
        WebViewHost.Children.Add(tab.WebView);
        Tabs.Add(tab);
        TabList.SelectedItem = tab;

        UpdateEmptyState();
    }

    private void GraphButton_Click(object sender, RoutedEventArgs e) => OpenGraphView();

    // ---- 構造クエリビュー ---------------------------------------------

    /// <summary>
    /// アクティブタブのファイルが属するフォルダを起点に構造クエリビューを開く。
    /// スコープは横断検索と同じ規則（ワークスペースルートへは広げない）。
    /// 同一フォルダの構造クエリタブが既にあればアクティブ化のみ行う。
    /// </summary>
    private void OpenStructureView()
    {
        // アクティブタブの種別ごとにスコープを直接決める（OpenGraphView と同じ理由で、
        // 仮想タブでは RootFolder を引き継ぎ、_currentRootFolder は最後のフォールバックのみ）。
        string? root;
        switch (TabList.SelectedItem)
        {
            case StructureQueryTab:
                return; // 既に構造クエリビューがアクティブ。
            case GraphTab graph:
                root = graph.RootFolder;
                break;
            case FingerprintTab fingerprint:
                root = fingerprint.RootFolder;
                break;
            case StatsTab stats:
                root = stats.RootFolder;
                break;
            case CanvasTab canvas:
                root = canvas.RootFolder;
                break;
            case DocumentTab active:
                try
                {
                    root = Path.GetDirectoryName(active.FilePath);
                }
                catch
                {
                    root = null;
                }
                break;
            default:
                root = null;
                break;
        }
        root ??= _currentRootFolder;

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        string fullRoot = Path.GetFullPath(root);
        var existing = Tabs.OfType<StructureQueryTab>().FirstOrDefault(
            t => string.Equals(t.RootFolder, fullRoot, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabList.SelectedItem = existing;
            return;
        }

        var tab = new StructureQueryTab(fullRoot, this, _assetsDirectory, _tempDirectory);
        tab.WebView.Visibility = Visibility.Collapsed;
        WebViewHost.Children.Add(tab.WebView);
        Tabs.Add(tab);
        TabList.SelectedItem = tab;

        UpdateEmptyState();
    }

    private void StructureButton_Click(object sender, RoutedEventArgs e) => OpenStructureView();

    // ---- 文書指紋・類似検出ビュー -------------------------------------

    /// <summary>
    /// アクティブタブのファイルが属するフォルダを起点に文書指紋ビューを開く。
    /// スコープ決定は構造クエリビューと同じ規則。
    /// 同一フォルダの指紋タブが既にあればアクティブ化のみ行う。
    /// </summary>
    private void OpenFingerprintView()
    {
        string? root;
        switch (TabList.SelectedItem)
        {
            case FingerprintTab:
                return; // 既に指紋ビューがアクティブ。
            case GraphTab graph:
                root = graph.RootFolder;
                break;
            case StructureQueryTab structure:
                root = structure.RootFolder;
                break;
            case StatsTab stats:
                root = stats.RootFolder;
                break;
            case CanvasTab canvas:
                root = canvas.RootFolder;
                break;
            case DocumentTab active:
                try
                {
                    root = Path.GetDirectoryName(active.FilePath);
                }
                catch
                {
                    root = null;
                }
                break;
            default:
                root = null;
                break;
        }
        root ??= _currentRootFolder;

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        string fullRoot = Path.GetFullPath(root);
        var existing = Tabs.OfType<FingerprintTab>().FirstOrDefault(
            t => string.Equals(t.RootFolder, fullRoot, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabList.SelectedItem = existing;
            return;
        }

        var tab = new FingerprintTab(fullRoot, this, _assetsDirectory, _tempDirectory);
        tab.WebView.Visibility = Visibility.Collapsed;
        WebViewHost.Children.Add(tab.WebView);
        Tabs.Add(tab);
        TabList.SelectedItem = tab;

        UpdateEmptyState();
    }

    private void FingerprintButton_Click(object sender, RoutedEventArgs e) => OpenFingerprintView();

    // ---- フォルダ統計ダッシュボード -----------------------------------

    /// <summary>
    /// アクティブタブのファイルが属するフォルダを起点に統計ダッシュボードを開く。
    /// スコープ決定は他の仮想タブと同じ規則。
    /// 同一フォルダの統計タブが既にあればアクティブ化のみ行う。
    /// </summary>
    private void OpenStatsView()
    {
        string? root;
        switch (TabList.SelectedItem)
        {
            case StatsTab:
                return; // 既に統計ダッシュボードがアクティブ。
            case GraphTab graph:
                root = graph.RootFolder;
                break;
            case StructureQueryTab structure:
                root = structure.RootFolder;
                break;
            case FingerprintTab fingerprint:
                root = fingerprint.RootFolder;
                break;
            case DocumentTab active:
                try
                {
                    root = Path.GetDirectoryName(active.FilePath);
                }
                catch
                {
                    root = null;
                }
                break;
            default:
                root = null;
                break;
        }
        root ??= _currentRootFolder;

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        string fullRoot = Path.GetFullPath(root);
        var existing = Tabs.OfType<StatsTab>().FirstOrDefault(
            t => string.Equals(t.RootFolder, fullRoot, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabList.SelectedItem = existing;
            return;
        }

        var tab = new StatsTab(fullRoot, this, _assetsDirectory, _tempDirectory);
        tab.WebView.Visibility = Visibility.Collapsed;
        WebViewHost.Children.Add(tab.WebView);
        Tabs.Add(tab);
        TabList.SelectedItem = tab;

        UpdateEmptyState();
    }

    private void StatsButton_Click(object sender, RoutedEventArgs e) => OpenStatsView();

    // ---- 無限キャンバス・モード ---------------------------------------

    /// <summary>
    /// ワークスペースルートを起点に無限キャンバス・モードを開く（ADR-0001 案B）。
    /// グラフビューと同じく FindWorkspaceRoot で親フォルダ側のリンクも含める。
    /// 同一ルートのキャンバスタブが既にあればアクティブ化のみ行う。
    /// </summary>
    private void OpenCanvasView()
    {
        string? root;
        switch (TabList.SelectedItem)
        {
            case CanvasTab:
                return; // 既にキャンバスがアクティブ。
            case GraphTab graph:
                root = graph.RootFolder;
                break;
            case StructureQueryTab structure:
                root = structure.RootFolder;
                break;
            case FingerprintTab fingerprint:
                root = fingerprint.RootFolder;
                break;
            case StatsTab stats:
                root = stats.RootFolder;
                break;
            case DocumentTab active:
                try
                {
                    root = Path.GetDirectoryName(active.FilePath);
                }
                catch
                {
                    root = null;
                }
                break;
            default:
                root = null;
                break;
        }
        root ??= _currentRootFolder;

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        string fullRoot = LinkGraphService.FindWorkspaceRoot(root);
        var existing = Tabs.OfType<CanvasTab>().FirstOrDefault(
            t => string.Equals(t.RootFolder, fullRoot, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabList.SelectedItem = existing;
            return;
        }

        var tab = new CanvasTab(fullRoot, this, _assetsDirectory, _tempDirectory);
        tab.WebView.Visibility = Visibility.Collapsed;
        WebViewHost.Children.Add(tab.WebView);
        Tabs.Add(tab);
        TabList.SelectedItem = tab;

        UpdateEmptyState();
    }

    private void CanvasButton_Click(object sender, RoutedEventArgs e) => OpenCanvasView();

    // ---- タブを閉じる -------------------------------------------------

    private void CloseTab(DocumentTab tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        bool wasSelected = ReferenceEquals(TabList.SelectedItem, tab);

        Tabs.Remove(tab);
        WebViewHost.Children.Remove(tab.WebView);
        tab.Dispose();

        if (wasSelected && Tabs.Count > 0)
        {
            int newIndex = Math.Min(index, Tabs.Count - 1);
            TabList.SelectedIndex = newIndex;
        }

        UpdateEmptyState();
        UpdateTitle();
    }

    private void CloseActiveTab()
    {
        if (TabList.SelectedItem is DocumentTab tab)
        {
            CloseTab(tab);
        }
    }

    // ---- 選択・タイトル・空状態 ---------------------------------------

    private async void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = TabList.SelectedItem as DocumentTab;

        foreach (var tab in Tabs)
        {
            tab.WebView.Visibility =
                ReferenceEquals(tab, selected) ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateTitle();

        // サイドバー（ファイルツリー）をアクティブタブのフォルダへ追従させる。
        UpdateSidebarForActiveTab();

        if (selected != null)
        {
            try
            {
                await selected.InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "Hirake - タブ初期化エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void UpdateTitle()
    {
        Title = TabList.SelectedItem is DocumentTab tab
            ? $"{tab.FileName} - Hirake"
            : "Hirake";
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool hasTabs = Tabs.Count > 0;
        PrintButton.IsEnabled = hasTabs;
        ExportPdfButton.IsEnabled = hasTabs;
    }

    // ---- タブ操作の UI イベント ---------------------------------------

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DocumentTab tab)
        {
            CloseTab(tab);
        }
    }

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        PrintActiveTab();
    }

    private void ExportPdfButton_Click(object sender, RoutedEventArgs e)
    {
        ExportActivePdf();
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        CycleTheme();
    }

    private void PrintActiveTab()
    {
        if (TabList.SelectedItem is DocumentTab tab)
        {
            tab.ShowPrintUI();
        }
    }

    // ---- PDF エクスポート ---------------------------------------------

    public async void ExportActivePdf()
    {
        if (TabList.SelectedItem is not DocumentTab tab)
        {
            return;
        }

        string initialFileName = Path.GetFileNameWithoutExtension(tab.FileName) + ".pdf";
        string? initialDir = Path.GetDirectoryName(tab.FilePath);

        var dialog = new SaveFileDialog
        {
            Filter = "PDF ファイル (*.pdf)|*.pdf|すべてのファイル (*.*)|*.*",
            FileName = initialFileName,
            DefaultExt = ".pdf",
            AddExtension = true,
        };
        if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
        {
            dialog.InitialDirectory = initialDir;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        bool ok;
        try
        {
            ok = await tab.ExportPdfAsync(dialog.FileName);
        }
        catch
        {
            ok = false;
        }

        if (!ok)
        {
            MessageBox.Show(
                "PDF の書き出しに失敗しました。",
                "Hirake - PDF エクスポート",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ---- クリップボード / テキストのクイックプレビュー ---------------

    public void QuickPasteFromClipboard()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string? path = SaveClipboardText(text);
        if (path != null)
        {
            OpenFile(path);
        }
    }

    /// <summary>テキストを clipboard フォルダへ .md として保存し、そのパスを返す。</summary>
    private string? SaveClipboardText(string text)
    {
        try
        {
            Directory.CreateDirectory(_clipboardDirectory);
            CleanupClipboardFiles();

            string name = $"clip-{DateTime.Now:yyyyMMdd_HHmmss}.md";
            string fullPath = Path.Combine(_clipboardDirectory, name);
            if (File.Exists(fullPath))
            {
                // 同一秒の衝突を避ける。
                name = $"clip-{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.md";
                fullPath = Path.Combine(_clipboardDirectory, name);
            }

            File.WriteAllText(fullPath, text, new System.Text.UTF8Encoding(false));
            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>clipboard フォルダの 30 日超の古いファイルを掃除する（失敗は握りつぶす）。</summary>
    private void CleanupClipboardFiles()
    {
        try
        {
            if (!Directory.Exists(_clipboardDirectory))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(_clipboardDirectory, "*.md"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 個別ファイルの削除失敗は無視する。
                }
            }
        }
        catch
        {
            // フォルダ列挙失敗は無視する。
        }
    }

    private bool IsClipboardTempFile(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (dir == null)
            {
                return false;
            }
            return string.Equals(
                dir.TrimEnd(Path.DirectorySeparatorChar),
                _clipboardDirectory.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void TabItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle
            && sender is FrameworkElement fe
            && fe.DataContext is DocumentTab tab)
        {
            CloseTab(tab);
        }
    }

    // ---- キーボードショートカット -------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (!ctrl)
        {
            return;
        }

        // Ctrl+Shift 併用のショートカット。
        if (shift)
        {
            switch (e.Key)
            {
                case Key.E:
                    ExportActivePdf();
                    e.Handled = true;
                    return;
                case Key.V:
                    QuickPasteFromClipboard();
                    e.Handled = true;
                    return;
                case Key.D:
                    CycleTheme();
                    e.Handled = true;
                    return;
                case Key.F:
                    OpenSidebarForSearch();
                    e.Handled = true;
                    return;
                case Key.S:
                    OpenStructureView();
                    e.Handled = true;
                    return;
                case Key.R:
                    OpenFingerprintView();
                    e.Handled = true;
                    return;
                case Key.T:
                    OpenStatsView();
                    e.Handled = true;
                    return;
                case Key.C:
                    OpenCanvasView();
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.B:
                ToggleSidebar();
                e.Handled = true;
                break;
            case Key.W:
                CloseActiveTab();
                e.Handled = true;
                break;
            case Key.O:
                ShowOpenDialog();
                e.Handled = true;
                break;
            case Key.P:
                PrintActiveTab();
                e.Handled = true;
                break;
            case Key.G:
                OpenGraphView();
                e.Handled = true;
                break;
            case Key.Tab:
                if (shift)
                {
                    SelectAdjacentTab(-1);
                }
                else
                {
                    SelectAdjacentTab(1);
                }
                e.Handled = true;
                break;
        }
    }

    private void SelectAdjacentTab(int direction)
    {
        if (Tabs.Count == 0)
        {
            return;
        }

        int current = TabList.SelectedIndex;
        if (current < 0)
        {
            current = 0;
        }

        int next = (current + direction + Tabs.Count) % Tabs.Count;
        TabList.SelectedIndex = next;
    }

    private void ShowOpenDialog()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Markdown ファイル (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|すべてのファイル (*.*)|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            foreach (var file in dialog.FileNames)
            {
                OpenFile(file);
            }
        }
    }

    // ---- ドラッグ＆ドロップ -------------------------------------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool acceptable = e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetDataPresent(DataFormats.UnicodeText)
            || e.Data.GetDataPresent(DataFormats.Text);

        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files)
                {
                    OpenFile(file);
                }
            }
            return;
        }

        // ファイルでない場合はテキストを一時ファイル化して開く。
        string? text = null;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                text = e.Data.GetData(DataFormats.UnicodeText) as string;
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                text = e.Data.GetData(DataFormats.Text) as string;
            }
        }
        catch
        {
            // データ取得失敗は無視する。
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            string? path = SaveClipboardText(text);
            if (path != null)
            {
                OpenFile(path);
            }
        }
    }

    // ---- ウィンドウ前面化 ---------------------------------------------

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // 確実に前面へ出すための一時 Topmost トグル。
        bool previousTopmost = Topmost;
        Topmost = true;
        Topmost = previousTopmost;
    }

    // ---- IDocumentTabHost ---------------------------------------------

    public void OpenFileInNewTab(string path, int? line = null) => OpenFile(path, line);

    public void ShortcutCloseActive() => CloseActiveTab();

    public void ShortcutNextTab() => SelectAdjacentTab(1);

    public void ShortcutPrevTab() => SelectAdjacentTab(-1);

    public void ShortcutOpenFile() => ShowOpenDialog();

    public void ShortcutQuickPaste() => QuickPasteFromClipboard();

    public void ShortcutExportPdf() => ExportActivePdf();

    public void ShortcutGlobalSearch() => OpenSidebarForSearch();

    public void ShortcutToggleSidebar() => ToggleSidebar();

    public void ShortcutCycleTheme() => CycleTheme();

    public void ShortcutToggleGraphView() => OpenGraphView();

    public void ShortcutToggleStructureView() => OpenStructureView();

    public void ShortcutToggleFingerprintView() => OpenFingerprintView();

    public void ShortcutToggleStatsView() => OpenStatsView();

    public void ShortcutToggleCanvasView() => OpenCanvasView();

    /// <summary>あるタブでズームが変わったら、他の全タブへ同じ倍率を反映する。</summary>
    public void OnTabZoomChanged(DocumentTab source, double zoomFactor)
    {
        if (_applyingZoom)
        {
            return;
        }

        _applyingZoom = true;
        try
        {
            foreach (var tab in Tabs)
            {
                if (ReferenceEquals(tab, source))
                {
                    continue;
                }
                tab.ApplyZoom(zoomFactor);
            }
        }
        finally
        {
            _applyingZoom = false;
        }
    }

    // ---- サイドバー（ファイルツリー / 横断検索） ----------------------

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    /// <summary>サイドバーの表示/非表示を切り替える。</summary>
    private void ToggleSidebar() => SetSidebarVisible(!_sidebarVisible, persist: true);

    /// <summary>サイドバーを開き、検索ボックスへフォーカスする（Ctrl+Shift+F）。</summary>
    private void OpenSidebarForSearch()
    {
        SetSidebarVisible(true, persist: true);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>サイドバーの列幅・可視性を反映する。persist=true のとき状態を保存する。</summary>
    private void SetSidebarVisible(bool visible, bool persist)
    {
        _sidebarVisible = visible;

        if (visible)
        {
            SidebarColumn.Width = _savedSidebarWidth.Value > 0
                ? _savedSidebarWidth
                : new GridLength(DefaultSidebarWidth);
            SidebarColumn.MinWidth = 160;
            Sidebar.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;

            // 開いた時点でツリー内容を最新化する。
            UpdateSidebarForActiveTab();
        }
        else
        {
            // 現在の幅を控えてから畳む。
            if (SidebarColumn.Width.IsAbsolute && SidebarColumn.Width.Value > 0)
            {
                _savedSidebarWidth = SidebarColumn.Width;
            }
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }

        if (persist)
        {
            SettingsStore.Instance.SidebarVisible = visible;
        }
    }

    /// <summary>アクティブタブのフォルダを起点にツリーを（必要なら再）構築し、ファイルをハイライトする。</summary>
    private void UpdateSidebarForActiveTab()
    {
        if (!_sidebarVisible)
        {
            return;
        }

        var active = TabList.SelectedItem as DocumentTab;
        string? folder = null;
        if (active != null)
        {
            try
            {
                folder = Path.GetDirectoryName(active.FilePath);
            }
            catch
            {
                folder = null;
            }
        }

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            _currentRootFolder = null;
            FileTree.ItemsSource = null;
            TreeRootLabel.Text = string.Empty;
            TreeEmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        // ルートフォルダが変わったときだけツリーを作り直す（同一なら維持）。
        bool sameRoot = string.Equals(
            folder, _currentRootFolder, StringComparison.OrdinalIgnoreCase);
        if (!sameRoot)
        {
            _currentRootFolder = folder;
            // 構築はバックグラウンドで行い、完了後にハイライトまで実施する。
            BuildFileTree(folder);
            return;
        }

        HighlightActiveFile(active!.FilePath);
    }

    /// <summary>
    /// ルート直下の走査をバックグラウンドで行い、完了後に UI スレッドでツリーへ反映する。
    /// 大きいフォルダ/ネットワークドライブでもタブ切替時に UI をブロックしない。
    /// </summary>
    private void BuildFileTree(string rootFolder)
    {
        _activeTreeItem = null;
        int generation = ++_treeBuildGeneration;

        // 構築中の軽い表示（読み込み中）。
        FileTree.ItemsSource = null;
        TreeRootLabel.Text = rootFolder;
        TreeEmptyLabel.Text = "読み込み中...";
        TreeEmptyLabel.Visibility = Visibility.Visible;

        _ = BuildFileTreeAsync(rootFolder, generation);
    }

    private async Task BuildFileTreeAsync(string rootFolder, int generation)
    {
        List<FileTreeItem> children;
        try
        {
            children = await Task.Run(() => FileTreeItem.LoadChildren(rootFolder));
        }
        catch
        {
            children = new List<FileTreeItem>();
        }

        // 連続タブ切替で新しい要求が来ていたら、この結果は破棄する（最新のみ反映）。
        if (generation != _treeBuildGeneration)
        {
            return;
        }

        FileTree.ItemsSource = children;
        TreeRootLabel.Text = rootFolder;
        TreeEmptyLabel.Text = "フォルダがありません";
        TreeEmptyLabel.Visibility = children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 反映後に、現在アクティブなタブのファイルをハイライトする
        // （このツリーのルートに属している場合のみ）。
        if (TabList.SelectedItem is DocumentTab active
            && string.Equals(
                Path.GetDirectoryName(active.FilePath),
                _currentRootFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            HighlightActiveFile(active.FilePath);
        }
    }

    /// <summary>ツリー内のアクティブファイルを（祖先を展開しつつ）ハイライト表示する。</summary>
    private void HighlightActiveFile(string filePath)
    {
        if (_activeTreeItem != null)
        {
            _activeTreeItem.IsActive = false;
            _activeTreeItem = null;
        }

        if (FileTree.ItemsSource is not IEnumerable<FileTreeItem> roots)
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch
        {
            return;
        }

        var found = ExpandToFile(roots, fullPath);
        if (found != null)
        {
            found.IsActive = true;
            found.IsSelected = true;
            _activeTreeItem = found;
        }
    }

    /// <summary>target ファイルへ至るフォルダを展開しながら、対応する葉ノードを探す。</summary>
    private static FileTreeItem? ExpandToFile(IEnumerable<FileTreeItem> items, string target)
    {
        foreach (var item in items)
        {
            if (!item.IsDirectory)
            {
                if (string.Equals(item.FullPath, target, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
                continue;
            }

            // フォルダ: target がこのフォルダ配下なら展開して再帰する。
            string prefix = item.FullPath.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                item.EnsureChildrenLoaded();
                item.IsExpanded = true;
                var result = ExpandToFile(item.Children, target);
                if (result != null)
                {
                    return result;
                }
            }
        }
        return null;
    }

    private void FileTreeItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileTreeItem item)
        {
            if (item.IsDirectory)
            {
                // フォルダ名クリックで展開/折りたたみをトグルする。
                item.IsExpanded = !item.IsExpanded;
            }
            else
            {
                OpenFile(item.FullPath);
            }
        }
    }

    // ---- フォルダ内横断検索 -------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text;
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(query)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(query))
        {
            // 空になったらツリー表示へ戻す。
            CancelSearch();
            ShowTreePane();
            return;
        }

        // 意味検索モードはインクリメンタル実行しない（Enter でのみ実行）。
        if (_semanticMode)
        {
            return;
        }

        // 400ms デバウンスで検索を起動する。
        _searchDebounce ??= CreateSearchDebounce();
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>意味検索トグルの切替。ON/OFF で検索経路を変え、入力中の語があれば即時に再実行する。</summary>
    private void SemanticToggle_Click(object sender, RoutedEventArgs e)
    {
        _semanticMode = SemanticToggle.IsChecked == true;
        SearchPlaceholder.Text = _semanticMode ? "意味で検索（Enter）" : "フォルダ内を検索";

        // モード切替は明示的な操作なので、語が入っていれば新モードで一度だけ実行する。
        _searchDebounce?.Stop();
        string query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length > 0)
        {
            RunSearch(query);
        }
    }

    private DispatcherTimer CreateSearchDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RunSearch(SearchBox.Text);
        };
        return timer;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _searchDebounce?.Stop();
            RunSearch(SearchBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private async void RunSearch(string query)
    {
        query = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            ShowTreePane();
            return;
        }

        string? root = _currentRootFolder;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            ShowSearchPane();
            SearchResultsList.ItemsSource = null;
            SearchSummary.Text = string.Empty;
            SearchEmptyLabel.Text = "検索対象のフォルダがありません";
            SearchEmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        // 意味検索モードなら専用経路へ（従来のキーワード検索は挙動を変えない）。
        if (_semanticMode)
        {
            await RunSemanticSearch(query, root);
            return;
        }

        // 直前の検索をキャンセルする。
        CancelSearch();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        ShowSearchPane();
        SearchSummary.Text = "検索中…";
        SearchEmptyLabel.Visibility = Visibility.Collapsed;

        List<SearchFileResult> results;
        bool ocrPending;
        try
        {
            (results, ocrPending) = await FolderSearchService.SearchAsync(root, query, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            results = new List<SearchFileResult>();
            ocrPending = false;
        }

        // 実行後に別の検索へ切り替わっていたら破棄する。
        if (cts.IsCancellationRequested || !ReferenceEquals(_searchCts, cts))
        {
            return;
        }

        // OCR 時間バジェット超過で未 OCR の画像が残っている場合はその旨を示す
        // （再検索のたびにキャッシュが温まり反映されていく）。0 件時も隠さない。
        const string ocrPendingNote = "画像OCR準備中（再検索で反映）";

        SearchResultsList.ItemsSource = results;
        int totalHits = results.Sum(r => r.TotalHits);
        if (results.Count == 0)
        {
            SearchSummary.Text = ocrPending ? ocrPendingNote : string.Empty;
            SearchEmptyLabel.Text = "一致する項目がありません";
            SearchEmptyLabel.Visibility = Visibility.Visible;
        }
        else
        {
            SearchSummary.Text = $"{totalHits} 件ヒット（{results.Count} ファイル）"
                + (ocrPending ? "・" + ocrPendingNote : string.Empty);
            SearchEmptyLabel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 意味（セマンティック）検索を実行する。モデル準備・索引作成の進捗は SearchSummary に表示し、
    /// 失敗時はエラーメッセージを空状態ラベルに出す（結果クリックは既存経路をそのまま通す）。
    /// </summary>
    private async Task RunSemanticSearch(string query, string root)
    {
        CancelSearch();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        ShowSearchPane();
        SearchResultsList.ItemsSource = null;
        SearchSummary.Text = "意味検索を準備中…";
        SearchEmptyLabel.Visibility = Visibility.Collapsed;

        // 進捗（ダウンロード・索引作成・検索中）を要約行へ反映する。
        var progress = new Progress<string>(msg =>
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                SearchSummary.Text = msg;
            }
        });

        List<SearchFileResult> results;
        string? error;
        try
        {
            (results, error) = await SemanticIndexService.SearchAsync(root, query, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            results = new List<SearchFileResult>();
            error = ex.Message;
        }

        // 実行後に別の検索へ切り替わっていたら破棄する。
        if (cts.IsCancellationRequested || !ReferenceEquals(_searchCts, cts))
        {
            return;
        }

        if (error != null)
        {
            SearchResultsList.ItemsSource = null;
            SearchSummary.Text = string.Empty;
            SearchEmptyLabel.Text = error;
            SearchEmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        SearchResultsList.ItemsSource = results;
        if (results.Count == 0)
        {
            SearchSummary.Text = string.Empty;
            SearchEmptyLabel.Text = "一致する項目がありません";
            SearchEmptyLabel.Visibility = Visibility.Visible;
        }
        else
        {
            // 意味検索は「一致」ではなく関連度順の候補である旨を表現に含める。
            SearchSummary.Text = $"意味検索（関連度順）: {results.Count} ファイル";
            SearchEmptyLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelSearch()
    {
        if (_searchCts != null)
        {
            try
            {
                _searchCts.Cancel();
                _searchCts.Dispose();
            }
            catch
            {
                // ignore
            }
            _searchCts = null;
        }
    }

    private void ShowTreePane()
    {
        SearchPane.Visibility = Visibility.Collapsed;
        TreePane.Visibility = Visibility.Visible;
    }

    private void ShowSearchPane()
    {
        TreePane.Visibility = Visibility.Collapsed;
        SearchPane.Visibility = Visibility.Visible;
    }

    private void SearchResult_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SearchFileResult result)
        {
            OpenFile(result.FullPath);
        }
    }

    /// <summary>
    /// ヒット行（L12: プレビュー）クリック: 該当ファイルを該当行で開く。
    /// e.Handled でファイル項目側の SearchResult_MouseUp（行ジャンプなし）を抑止する。
    /// FullPath 未設定のヒットは従来動作（ファイル項目側）へフォールバックする。
    /// </summary>
    private void SearchHit_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is SearchHit hit
            && !string.IsNullOrEmpty(hit.FullPath))
        {
            OpenFile(hit.FullPath, hit.LineNumber);
            e.Handled = true;
        }
    }

    // ---- テーマ切替 ---------------------------------------------------

    /// <summary>テーマ設定を auto → light → dark → auto と巡回して反映・保存する。</summary>
    private void CycleTheme()
    {
        string current = SettingsStore.Instance.Theme;
        string next = current switch
        {
            "light" => "dark",
            "dark" => "auto",
            _ => "light", // "auto"（およびその他）→ light
        };

        SettingsStore.Instance.Theme = next;
        ApplyEffectiveTheme();
    }

    /// <summary>現在のテーマ設定から実効テーマを求め、全タブと WPF クロームへ反映する。</summary>
    private void ApplyEffectiveTheme()
    {
        string themeSetting = SettingsStore.Instance.Theme;
        string effective = SettingsStore.GetEffectiveTheme(themeSetting);

        ApplyChromeTheme(effective);
        UpdateThemeButton(themeSetting);

        foreach (var tab in Tabs)
        {
            tab.ApplyTheme(effective);
        }
    }

    /// <summary>WPF クローム（ウィンドウ・タブバー・文字色）の配色をテーマ用ブラシへ差し替える。</summary>
    // 配色は Assets/style.css の CSS 変数パレットと手動同期。変更時は両方更新すること。
    private void ApplyChromeTheme(string effective)
    {
        if (effective == "dark")
        {
            // GitHub ダーク風の配色。
            SetBrush("ChromeWindowBackground", 0x0D, 0x11, 0x17);
            SetBrush("ChromeBarBackground", 0x16, 0x1B, 0x22);
            SetBrush("ChromeBorder", 0x30, 0x36, 0x3D);
            SetBrush("ChromeText", 0xE6, 0xED, 0xF3);
            SetBrush("ChromeTitleText", 0x8B, 0x94, 0x9E);
            SetBrush("ChromeSubText", 0x6E, 0x76, 0x81);
            SetBrush("ChromeAccent", 0x44, 0x93, 0xF8);
            SetBrush("ChromeHover", 0x1F, 0x26, 0x30);
            SetBrush("ChromeInputBackground", 0x0D, 0x11, 0x17);
            SetBrush("ChromeActiveBackground", 0x1F, 0x3A, 0x5F);
            SetSelectionBrushes(0x1F, 0x3A, 0x5F, 0xE6, 0xED, 0xF3);
        }
        else
        {
            // ライト（既定）。
            SetBrush("ChromeWindowBackground", 0xFF, 0xFF, 0xFF);
            SetBrush("ChromeBarBackground", 0xF3, 0xF3, 0xF3);
            SetBrush("ChromeBorder", 0xDD, 0xDD, 0xDD);
            SetBrush("ChromeText", 0x1F, 0x1F, 0x1F);
            SetBrush("ChromeTitleText", 0x88, 0x88, 0x88);
            SetBrush("ChromeSubText", 0xAA, 0xAA, 0xAA);
            SetBrush("ChromeAccent", 0x09, 0x69, 0xDA);
            SetBrush("ChromeHover", 0xE8, 0xE8, 0xE8);
            SetBrush("ChromeInputBackground", 0xFF, 0xFF, 0xFF);
            SetBrush("ChromeActiveBackground", 0xDD, 0xEB, 0xFF);
            SetSelectionBrushes(0xDD, 0xEB, 0xFF, 0x1F, 0x1F, 0x1F);
        }
    }

    private void SetBrush(string key, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        Resources[key] = brush;
    }

    /// <summary>
    /// TreeView/ListBox の選択ハイライトに使われる SystemColors 系ブラシをテーマ配色で
    /// 上書きする。既定の非アクティブ選択ブラシ（明るいグレー）はダークテーマの文字色と
    /// 同化して選択項目が読めなくなるため必須。
    /// </summary>
    private void SetSelectionBrushes(byte bgR, byte bgG, byte bgB, byte fgR, byte fgG, byte fgB)
    {
        var bg = new SolidColorBrush(Color.FromRgb(bgR, bgG, bgB));
        bg.Freeze();
        var fg = new SolidColorBrush(Color.FromRgb(fgR, fgG, fgB));
        fg.Freeze();

        Resources[SystemColors.HighlightBrushKey] = bg;
        Resources[SystemColors.InactiveSelectionHighlightBrushKey] = bg;
        Resources[SystemColors.HighlightTextBrushKey] = fg;
        Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = fg;
    }

    /// <summary>テーマボタンのアイコンとツールチップを現在の設定に合わせて更新する。</summary>
    private void UpdateThemeButton(string themeSetting)
    {
        // Segoe MDL2 Assets: 自動=コントラスト, ライト=明るさ(太陽), ダーク=月。
        (string glyph, string label) = themeSetting switch
        {
            "light" => ("", "ライト"),
            "dark" => ("", "ダーク"),
            _ => ("", "自動"),
        };

        ThemeButton.Content = glyph;
        ThemeButton.ToolTip = $"テーマ: {label} (Ctrl+Shift+D)";
    }

    /// <summary>OS のアプリテーマ変更に追従する（Theme="auto" のときのみ実効テーマを再評価）。</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        // 別スレッドから来る可能性があるため UI スレッドへ戻す。
        Dispatcher.BeginInvoke(() =>
        {
            if (string.Equals(SettingsStore.Instance.Theme, "auto", StringComparison.OrdinalIgnoreCase))
            {
                ApplyEffectiveTheme();
            }
        });
    }

    // ---- セッション復元 -----------------------------------------------

    /// <summary>前回セッションのタブ（存在するファイルのみ）を開き、前回アクティブを復元する。</summary>
    public void RestoreSession()
    {
        var sessionTabs = SettingsStore.Instance.SessionTabs;
        string? active = SettingsStore.Instance.SessionActiveTab;

        foreach (var path in sessionTabs)
        {
            try
            {
                if (File.Exists(path))
                {
                    OpenFile(path);
                }
            }
            catch
            {
                // 個別ファイルの復元失敗は無視する。
            }
        }

        if (!string.IsNullOrEmpty(active))
        {
            try
            {
                string fullActive = Path.GetFullPath(active);
                var match = Tabs.FirstOrDefault(
                    t => string.Equals(t.FilePath, fullActive, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    TabList.SelectedItem = match;
                }
            }
            catch
            {
                // アクティブタブ復元失敗は無視する。
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // OS テーマ追従の購読を解除する（静的イベントのためリークに注意）。
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // 進行中の横断検索を打ち切る。
        CancelSearch();

        // セッション（開いているタブとアクティブタブ）を保存する。
        // クリップボード一時ファイルのタブはセッション復元対象から除外する。
        try
        {
            var sessionPaths = Tabs
                .Select(t => t.FilePath)
                .Where(p => !IsClipboardTempFile(p))
                .ToList();

            string? active = (TabList.SelectedItem as DocumentTab)?.FilePath;
            if (active != null && IsClipboardTempFile(active))
            {
                active = null;
            }

            SettingsStore.Instance.SetSession(sessionPaths, active);
        }
        catch
        {
            // セッション保存失敗は握りつぶす。
        }

        foreach (var tab in Tabs.ToList())
        {
            tab.Dispose();
        }
        Tabs.Clear();
        base.OnClosed(e);
    }
}
