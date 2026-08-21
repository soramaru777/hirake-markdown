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

    // リンクを経由するフォルダを設定なしで開こうとしたときの案内（ISSUE #61）。
    // ネットワークのときには出さない（設定を有効にしても開けないため）。
    private const string TreeEmptyMessage = "フォルダがありません";

    private const string LinkBlockedTreeMessage =
        "このフォルダはリンクを経由しています。settings.json の FollowDirectoryLinks を true にすると表示できます。";

    private const string LinkBlockedSearchMessage =
        "このフォルダはリンクを経由しています。FollowDirectoryLinks を true にすると検索できます。";

    private const string LinkBlockedDialogMessage =
        "このフォルダはリンク（シンボリックリンク／ジャンクション）を経由しています。\n\n"
        + "settings.json の FollowDirectoryLinks を true にして再起動すると開けます。";

    private string? _currentRootFolder;
    private FileTreeItem? _activeTreeItem;
    private GridLength _savedSidebarWidth = new(DefaultSidebarWidth);

    // ツリー構築の世代カウンタ。連続タブ切替時に最新要求の結果だけを反映するために使う。
    private int _treeBuildGeneration;

    // ---- 表示履歴とツリールートの固定（ISSUE #84） --------------------
    // 「1 つ前に表示していたファイル」へ戻るための履歴。ウィンドウごとに独立で、
    // 保存しない（終了時に破棄）。タブの参照ではなく**パス**で持つのは、
    // タブを閉じた後でも戻れるようにするため（戻り先のタブが無ければ開き直す）。
    private const int MaxHistory = 50;
    private readonly List<string> _history = new();

    // 履歴の現在位置。-1 = 空。新しいファイルを表示すると増え、戻ると減る。
    // 「進む」は今は UI が無いが、後から足せるよう index 方式にしてある
    // （新しいファイルを表示したら index より後ろを捨てる、というブラウザと同じ規則）。
    private int _historyIndex = -1;

    // 戻る操作で起きるタブ切替を履歴に積まないための再入防止。
    private bool _suppressHistoryPush;

    // 上階層ボタンで固定したツリールート。null なら従来どおりアクティブタブへ追従する。
    private string? _treeRootOverride;

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
        _tempDirectory = AppPaths.TempDir;
        _clipboardDirectory = AppPaths.ClipboardDir;

        // 起動時に古いクリップボード一時ファイルを軽く掃除する。
        CleanupClipboardFiles();

        UpdateEmptyState();
        UpdateTitle();

        // 保存済みテーマで WPF クローム・テーマボタンを初期化する。
        ApplyEffectiveTheme();

        // 保存済みのサイドバー表示状態を復元する（ツリー内容はタブ確定時に構築する）。
        SetSidebarVisible(SettingsStore.Instance.SidebarVisible, persist: false);

        // OS のアプリテーマ変更の購読は WindowManager が 1 か所で行う
        // （ウィンドウごとに購読すると枚数分だけ購読が増える。静的イベントのため
        //  解除漏れがそのままリークになる）。
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

    // ---- ナレッジグラフ（キャンバスの全体俯瞰） -----------------------

    /// <summary>
    /// ナレッジグラフ＝キャンバスの全体俯瞰として開く（#32・R3 完成）。
    /// 旧グラフビュー（GraphTab）は廃止し、キャンバスを遠景（fit-to-content）で
    /// 開くことで置き換える。保存済みビューポートは使わない。
    /// </summary>
    private void OpenCanvasOverview() => OpenCanvasView(overview: true);

    private void GraphButton_Click(object sender, RoutedEventArgs e) => OpenCanvasOverview();

    // ---- 構造クエリビュー ---------------------------------------------

    /// <summary>
    /// アクティブタブのファイルが属するフォルダを起点に構造クエリビューを開く。
    /// スコープは横断検索と同じ規則（ワークスペースルートへは広げない）。
    /// 同一フォルダの構造クエリタブが既にあればアクティブ化のみ行う。
    /// </summary>
    private void OpenStructureView()
    {
        // 仮想タブの FilePath はフォルダ自身のため GetDirectoryName すると親に化ける。
        // アクティブタブの種別ごとにスコープを直接決める（_currentRootFolder は
        // サイドバー追従で親へずれることがあるため最後のフォールバックのみ）。
        string? root;
        switch (TabList.SelectedItem)
        {
            case StructureQueryTab:
                return; // 既に構造クエリビューがアクティブ。
            case FingerprintTab fingerprint:
                root = fingerprint.RootFolder;
                break;
            case StatsTab stats:
                root = stats.RootFolder;
                break;
            case CanvasTab canvas:
                root = canvas.RootFolder;
                break;
            case LinkCheckTab linkCheck:
                root = linkCheck.RootFolder;
                break;
            case DocumentTab active:
                // 検査済みの実パスにしてから使う。素のフォルダ名のまま
                // Directory.Exists へ渡すと、祖先が UNC を指すリンクだった
                // 場合にその時点で SMB へ出る（ISSUE #54）。
                root = ResolveTreeRootOrHint(active.FilePath);
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

        OpenStructureForRoot(root);
    }

    /// <summary>
    /// 仮想タブの起点として使えるフォルダか。使える場合は正規化したパスを返す。
    /// 仮想タブは配下を再帰的に走査するため、生成の入口でまとめて検査する
    /// （呼び出し元が増えたときに検査漏れが再発しないように）。
    /// </summary>
    private static string? ResolveVirtualTabRoot(string root)
    {
        try
        {
            string full = Path.GetFullPath(root);
            if (HirakeUri.IsRemoteOrUncFolder(full))
            {
                return null;
            }

            // 案内は入口（ResolveTreeRootOrHint）で出す。ここで出すと、
            // 同じ操作で 2 回ダイアログが出ることになる。
            if (!FileTreeItem.CheckTraversal(full, isDirectory: true).IsAllowed)
            {
                return null;
            }

            return full;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>指定フォルダで構造クエリビューを開く（既にあればアクティブ化）。</summary>
    private void OpenStructureForRoot(string root)
    {
        if (ResolveVirtualTabRoot(root) is not string fullRoot)
        {
            return;
        }

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
            case StructureQueryTab structure:
                root = structure.RootFolder;
                break;
            case StatsTab stats:
                root = stats.RootFolder;
                break;
            case CanvasTab canvas:
                root = canvas.RootFolder;
                break;
            case LinkCheckTab linkCheck:
                root = linkCheck.RootFolder;
                break;
            case DocumentTab active:
                // 検査済みの実パスにしてから使う。素のフォルダ名のまま
                // Directory.Exists へ渡すと、祖先が UNC を指すリンクだった
                // 場合にその時点で SMB へ出る（ISSUE #54）。
                root = ResolveTreeRootOrHint(active.FilePath);
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

        OpenFingerprintForRoot(root);
    }

    /// <summary>指定フォルダで文書指紋ビューを開く（既にあればアクティブ化）。</summary>
    private void OpenFingerprintForRoot(string root)
    {
        if (ResolveVirtualTabRoot(root) is not string fullRoot)
        {
            return;
        }

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
            case StructureQueryTab structure:
                root = structure.RootFolder;
                break;
            case FingerprintTab fingerprint:
                root = fingerprint.RootFolder;
                break;
            case LinkCheckTab linkCheck:
                root = linkCheck.RootFolder;
                break;
            case DocumentTab active:
                // 検査済みの実パスにしてから使う。素のフォルダ名のまま
                // Directory.Exists へ渡すと、祖先が UNC を指すリンクだった
                // 場合にその時点で SMB へ出る（ISSUE #54）。
                root = ResolveTreeRootOrHint(active.FilePath);
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

        OpenStatsForRoot(root);
    }

    /// <summary>指定フォルダで統計ダッシュボードを開く（既にあればアクティブ化）。</summary>
    private void OpenStatsForRoot(string root)
    {
        if (ResolveVirtualTabRoot(root) is not string fullRoot)
        {
            return;
        }

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

    // ---- リンク切れ・孤立ページの検出ビュー -----------------------------

    /// <summary>
    /// アクティブタブのファイルが属するフォルダを起点にリンク検出ビューを開く（ISSUE #56）。
    /// スコープ決定は他の仮想タブと同じ規則。
    /// 同一フォルダの検出タブが既にあればアクティブ化のみ行う。
    /// </summary>
    private void OpenLinkCheckView()
    {
        string? root;
        switch (TabList.SelectedItem)
        {
            case LinkCheckTab:
                return; // 既にリンク検出ビューがアクティブ。
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
                // 検査済みの実パスにしてから使う。素のフォルダ名のまま
                // Directory.Exists へ渡すと、祖先が UNC を指すリンクだった
                // 場合にその時点で SMB へ出る（ISSUE #54）。
                root = ResolveTreeRootOrHint(active.FilePath);
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

        OpenLinkCheckForRoot(root);
    }

    /// <summary>指定フォルダでリンク検出ビューを開く（既にあればアクティブ化）。</summary>
    private void OpenLinkCheckForRoot(string root)
    {
        if (ResolveVirtualTabRoot(root) is not string fullRoot)
        {
            return;
        }

        var existing = Tabs.OfType<LinkCheckTab>().FirstOrDefault(
            t => string.Equals(t.RootFolder, fullRoot, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabList.SelectedItem = existing;
            return;
        }

        var tab = new LinkCheckTab(fullRoot, this, _assetsDirectory, _tempDirectory);
        tab.WebView.Visibility = Visibility.Collapsed;
        WebViewHost.Children.Add(tab.WebView);
        Tabs.Add(tab);
        TabList.SelectedItem = tab;

        UpdateEmptyState();
    }

    private void LinkCheckButton_Click(object sender, RoutedEventArgs e) => OpenLinkCheckView();

    // ---- 無限キャンバス・モード ---------------------------------------

    /// <summary>
    /// ワークスペースルートを起点に無限キャンバス・モードを開く（ADR-0001 案B）。
    /// グラフビューと同じく FindWorkspaceRoot で親フォルダ側のリンクも含める。
    /// 同一ルートのキャンバスタブが既にあればアクティブ化のみ行う。
    /// </summary>
    /// <param name="overview">
    /// true のとき全体俯瞰（遠景）で開く。Ctrl+G / グラフボタン経由の呼び出しで、
    /// 既にキャンバスが開いていればそのタブを俯瞰へズームアウトさせる。
    /// </param>
    private void OpenCanvasView(bool overview = false)
    {
        string? root;
        switch (TabList.SelectedItem)
        {
            case CanvasTab active:
                if (overview)
                {
                    active.ShowOverview();
                }
                return; // 既にキャンバスがアクティブ。
            case StructureQueryTab structure:
                root = structure.RootFolder;
                break;
            case FingerprintTab fingerprint:
                root = fingerprint.RootFolder;
                break;
            case StatsTab stats:
                root = stats.RootFolder;
                break;
            case LinkCheckTab linkCheck:
                root = linkCheck.RootFolder;
                break;
            case DocumentTab active:
                // 検査済みの実パスにしてから使う。素のフォルダ名のまま
                // Directory.Exists へ渡すと、祖先が UNC を指すリンクだった
                // 場合にその時点で SMB へ出る（ISSUE #54）。
                root = ResolveTreeRootOrHint(active.FilePath);
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

        OpenCanvasForRoot(LinkGraphService.FindWorkspaceRoot(root), view: null, overview: overview);
    }

    /// <summary>
    /// 指定ルートでキャンバスを開く（既にあればアクティブ化）。
    /// view を渡すと、保存済みのビューポート（パン位置とズーム率）で表示する。
    /// ノードの配置は CanvasLayoutStore の共有値をそのまま使う。
    /// </summary>
    private void OpenCanvasForRoot(string root, CanvasViewState? view, bool overview = false)
    {
        if (ResolveVirtualTabRoot(root) is not string fullRoot)
        {
            return;
        }

        var existing = Tabs.OfType<CanvasTab>().FirstOrDefault(
            t => string.Equals(t.RootFolder, fullRoot, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            TabList.SelectedItem = existing;
            if (overview)
            {
                existing.ShowOverview();
            }
            else if (view.IsValid())
            {
                existing.ApplyView(view!);
            }
            return;
        }

        var tab = new CanvasTab(fullRoot, this, _assetsDirectory, _tempDirectory, overview);
        if (!overview && view.IsValid())
        {
            tab.SetInitialView(view!);
        }
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

        // 全部閉じたら「上へ」で固定したルートも捨てる。UpdateSidebarForActiveTab では
        // 一時的な null（Remove してから SelectedIndex を決めるまでの間）と区別が付かず、
        // 固定を保つ側に倒してある。恒久的に 0 枚になったかはここでしか分からない。
        // 残したままだと、閉じて開き直したのに以前の「上へ」の状態へ戻ってしまう。
        if (Tabs.Count == 0)
        {
            _treeRootOverride = null;
        }

        UpdateEmptyState();
        UpdateTitle();

        // タブが 0 枚になるとツリーも消えるので、ボタンの状態を取り直す。
        UpdateNavigationButtons();
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

        // 履歴は**最初の await より前**に積む。await をまたぐと、戻る操作中の
        // 再入防止フラグ（_suppressHistoryPush）が既に false へ戻っていて、
        // 戻り先を「新しく表示したファイル」として積んでしまう。
        if (selected != null)
        {
            PushHistory(selected.FilePath);
        }

        foreach (var tab in Tabs)
        {
            tab.WebView.Visibility =
                ReferenceEquals(tab, selected) ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateTitle();

        // サイドバー（ファイルツリー）をアクティブタブのフォルダへ追従させる。
        UpdateSidebarForActiveTab();
        UpdateNavigationButtons();

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

    /// <summary>
    /// 現在の表示位置を指す hirake:// リンクをクリップボードへコピーする。
    /// 行が取れない場合はファイルを開くだけの URI にする（コピー自体は成功させる）。
    /// </summary>
    private async void CopyDeepLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DocumentTab tab)
        {
            return;
        }
        if (!tab.SupportsDeepLink)
        {
            return; // 仮想タブ（実ファイルなし）。メニューは無効化済みだが念のため。
        }

        int? line = null;
        try
        {
            line = await tab.GetCurrentSourceLineAsync().ConfigureAwait(true);
        }
        catch
        {
            // 行が取れなくてもファイル単位のリンクは作れる。
        }

        string uri = HirakeUri.BuildOpenUri(tab.FilePath, line);
        try
        {
            Clipboard.SetText(uri);
        }
        catch
        {
            // 他プロセスがクリップボードを掴んでいる場合など。通知は出さない。
        }
    }

    // ---- エディタで開く（ISSUE #55） ---------------------------------

    /// <summary>
    /// いま「エディタで開く」を処理中のタブ。連打・キーリピートで
    /// 同じファイルを何度も起動しないための門番（UI スレッドからのみ触る）。
    /// </summary>
    private readonly HashSet<DocumentTab> _editorLaunching = new();

    /// <summary>ショートカット（Ctrl+E）。viewer.js からの転送もここへ来る。</summary>
    public void ShortcutOpenInEditor() => OpenActiveInEditor();

    /// <summary>
    /// タブのコンテキストメニューを開くたびに、「フォルダをエディタで開く」の
    /// 有効・無効を決める。設定済みのエディタがフォルダを開けない場合
    /// （サクラエディタ・メモ帳など FolderArgs が空）に押せてしまうと、
    /// 毎回エラーになるだけで何もできない。
    /// エディタ未設定のときは押せるままにする（押すと選択画面へ入れるため）。
    /// </summary>
    private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        // 未設定の判定は ResolveEditor と同じにする。ExePath が空の壊れた設定を
        // 「設定済み」と見なすと、メニューが無効のまま選び直せなくなる。
        EditorSettings? editor = SettingsStore.Instance.Editor;
        bool unset = editor == null || string.IsNullOrWhiteSpace(editor.ExePath);
        bool canOpenFolder = unset || (editor!.FolderArgs?.Count ?? 0) > 0;

        foreach (object item in menu.Items)
        {
            if (item is MenuItem { CommandParameter: "openFolder" } folderItem)
            {
                folderItem.IsEnabled =
                    canOpenFolder && folderItem.Tag is DocumentTab { SupportsDeepLink: true };
            }
        }
    }

    private void OpenInEditor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DocumentTab tab })
        {
            _ = RunOpenInEditorAsync(tab);
        }
    }

    private void OpenFolderInEditor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DocumentTab tab } || !tab.SupportsDeepLink)
        {
            return;
        }

        EditorSettings? editor = ResolveEditor();
        if (editor == null)
        {
            return; // 未設定のまま（利用者がキャンセルした）。
        }

        string? folder = Path.GetDirectoryName(tab.FilePath);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        if (!EditorLauncher.TryOpenFolder(editor, folder, out string error))
        {
            ShowEditorError(editor, error);
        }
    }

    private void OpenActiveInEditor()
    {
        if (TabList.SelectedItem is DocumentTab tab)
        {
            _ = RunOpenInEditorAsync(tab);
        }
    }

    /// <summary>
    /// 投げっぱなしにする非同期処理は、例外を必ずここで受ける。
    /// 受けないと未観測の例外として消え、利用者から見れば「押しても何も起きない」に
    /// なる。ISSUE #55 が最も避けたい状態なので、握らずに見せて記録する。
    /// </summary>
    private async Task RunOpenInEditorAsync(DocumentTab tab)
    {
        // 行の取得は非同期なので、その間に Ctrl+E を連打（キーリピート含む）されると
        // 待っている分だけエディタが起動してしまう。タブ単位で 1 回に絞る。
        if (!_editorLaunching.Add(tab))
        {
            return;
        }

        try
        {
            await OpenInEditorAsync(tab).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Diagnostics.Record("editor", "エディタで開く処理が失敗しました", ex);
            MessageBox.Show(
                this,
                "エディタで開けませんでした。" + Environment.NewLine + Environment.NewLine + ex.Message,
                "エディタで開く",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _editorLaunching.Remove(tab);
        }
    }

    /// <summary>
    /// いま読んでいる行を外部エディタで開く。
    /// 行が取れなくてもファイルは開く（開けないより、先頭で開く方がよい）。
    /// </summary>
    private async Task OpenInEditorAsync(DocumentTab tab)
    {
        if (!tab.SupportsDeepLink)
        {
            return; // 仮想タブ（実ファイルなし）。メニューは無効化済みだが念のため。
        }

        EditorSettings? editor = ResolveEditor();
        if (editor == null)
        {
            return;
        }

        int? line = null;
        try
        {
            line = await tab.GetCurrentSourceLineAsync().ConfigureAwait(true);
        }
        catch
        {
            // 行が取れなくてもファイル単位では開ける。
        }

        // 行を取っている間にタブが閉じられていたら、もう開かない。
        // 閉じたのに数秒後にエディタが開く方が驚かれる（破棄済み WebView の例外は
        // GetCurrentSourceLineAsync が null に変換するため、ここで見ないと素通りする）。
        if (!TabList.Items.Contains(tab))
        {
            return;
        }

        if (!EditorLauncher.TryOpenFile(editor, tab.FilePath, line, out string error))
        {
            ShowEditorError(editor, error);
        }
    }

    /// <summary>
    /// 設定済みのエディタを返す。未設定なら 1 回だけ検出して選ばせ、保存する。
    /// 検出は起動時ではなくここで走らせる（使わない人に払わせない）。
    /// </summary>
    private EditorSettings? ResolveEditor()
    {
        EditorSettings? editor = SettingsStore.Instance.Editor;
        if (editor != null && !string.IsNullOrWhiteSpace(editor.ExePath))
        {
            return editor;
        }

        IReadOnlyList<EditorCandidate> candidates;
        try
        {
            candidates = EditorDetector.Detect(EditorPresets.Load(_assetsDirectory));
        }
        catch (Exception ex)
        {
            Diagnostics.Record("editor", "エディタの検出に失敗しました", ex);
            candidates = Array.Empty<EditorCandidate>();
        }

        var picker = new EditorPickerWindow(candidates) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected == null)
        {
            return null;
        }

        SettingsStore.Instance.Editor = picker.Selected;
        return picker.Selected;
    }

    /// <summary>
    /// 失敗を黙らせない。何を実行しようとしたのかまで見せないと、
    /// 設定が悪いのか、エディタが無いのか、パスが違うのかを切り分けられない。
    /// </summary>
    private void ShowEditorError(EditorSettings editor, string error)
    {
        MessageBox.Show(
            this,
            "エディタを起動できませんでした。" + Environment.NewLine + Environment.NewLine
            + $"エディタ: {editor.Name}" + Environment.NewLine
            + $"実行ファイル: {editor.ExePath}" + Environment.NewLine + Environment.NewLine
            + $"理由: {error}" + Environment.NewLine + Environment.NewLine
            + "設定は settings.json の Editor にあります。選び直すには、その項目を削除してください。",
            "エディタで開く",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
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
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        // Alt 併用のショートカット（ISSUE #84）。Ctrl の判定より前に置く。
        // WPF は Alt を伴うキーを Key.System として渡し、実際のキーは SystemKey に入る。
        // e.Key だけを見ると Left/Up が取れない。
        if (alt && !ctrl && !shift)
        {
            Key altKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (altKey == Key.Left)
            {
                GoBack();
                e.Handled = true;
                return;
            }
            if (altKey == Key.Up)
            {
                GoUpFolder();
                e.Handled = true;
                return;
            }
        }

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
                case Key.L:
                    OpenLinkCheckView();
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
                case Key.W:
                    ToggleWorkspacePopup();
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.N:
                OpenNewWindow();
                e.Handled = true;
                break;
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
            case Key.E:
                // 押しっぱなしの自動リピートは 1 回ぶんとして扱う（ISSUE #55）。
                // 「押した回数だけプロセスが起動する」操作なのでここだけ抑える。
                // Handled はリピートでも立てる（本文へキーが漏れないように）。
                e.Handled = true;
                if (!e.IsRepeat)
                {
                    OpenActiveInEditor();
                }
                break;
            case Key.P:
                PrintActiveTab();
                e.Handled = true;
                break;
            case Key.G:
                OpenCanvasOverview();
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

    public void ShortcutGoBack() => GoBack();

    public void ShortcutGoUpFolder() => GoUpFolder();

    public void ShortcutCycleTheme() => CycleTheme();

    // 旧グラフビュー廃止後も、各ビュー JS からの 'toggleGraphView' は
    // キャンバス俯瞰へのエイリアスとして受け続ける（JS 側の転送は変更不要）。
    public void ShortcutToggleGraphView() => OpenCanvasOverview();

    public void ShortcutToggleStructureView() => OpenStructureView();

    public void ShortcutToggleLinkCheckView() => OpenLinkCheckView();

    public void ShortcutToggleFingerprintView() => OpenFingerprintView();

    public void ShortcutToggleStatsView() => OpenStatsView();

    public void ShortcutToggleCanvasView() => OpenCanvasView();

    public void ShortcutToggleWorkspaceMenu() => ToggleWorkspacePopup();

    public void ShortcutNewWindow() => OpenNewWindow();

    /// <summary>あるタブでズームが変わったら、他の全タブへ同じ倍率を反映する。</summary>
    public void OnTabZoomChanged(DocumentTab source, double zoomFactor)
    {
        // ズーム倍率はアプリ全体で 1 つ。ウィンドウ内だけに反映すると、
        // 同じ設定を共有しているのにウィンドウごとに表示倍率が分裂する。
        WindowManager.Instance.ApplyZoomToAll(source, zoomFactor);
    }

    /// <summary>WindowManager からズーム反映を要求されたときの入口。</summary>
    public void ApplyZoomFromHost(DocumentTab source, double zoomFactor)
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

        // サイドバーが閉じている間は「上へ」を押せない（押しても見た目が変わらないため）。
        UpdateNavigationButtons();
    }

    /// <summary>アクティブタブのフォルダを起点にツリーを（必要なら再）構築し、ファイルをハイライトする。</summary>
    private void UpdateSidebarForActiveTab()
    {
        if (!_sidebarVisible)
        {
            return;
        }

        var active = TabList.SelectedItem as DocumentTab;

        // 上階層ボタンで固定したルートは、アクティブファイルがその配下にある限り保つ
        // （上げた直後に上のフォルダのファイルを開いても、元に戻らないようにするため）。
        // 配下から外れたら固定を捨てて、従来どおりの追従へ戻す。
        //
        // active が null のときは固定を**捨てない**。タブを閉じると、次のタブが選ばれる前に
        // 一瞬 SelectedItem が null になる（CloseTab が Remove してから SelectedIndex を
        // 設定するため）。ここで捨てると、同じルート配下の隣のタブへ移っただけで
        // 固定が解けてしまう。
        if (_treeRootOverride != null && active != null)
        {
            // 固定した時点では検査済みでも、その後にフォルダをリンクへ差し替えられる。
            // Directory.Exists を先に呼ぶと、UNC を指すリンクならそこでネットワークへ出る。
            // CheckTreeRoot と同じく、**祖先の検査を先に**通してから実体を触る。
            TraversalCheck overrideCheck =
                FileTreeItem.CheckTraversal(_treeRootOverride, isDirectory: true);
            string? overrideRoot = overrideCheck.ResolvedPath;

            // 配下判定も実体パス同士で行う（リンク経由で開いたファイルは、揃えないと
            // 一致しない・ISSUE #54）。解決できないときは元のパスで見る。
            string activePath =
                FileTreeItem.ResolveCheckedPath(active.FilePath, isDirectory: false) ?? active.FilePath;

            if (!string.IsNullOrEmpty(overrideRoot)
                && Directory.Exists(overrideRoot)
                && IsUnder(activePath, overrideRoot))
            {
                _treeRootOverride = overrideRoot;

                if (string.Equals(overrideRoot, _currentRootFolder, StringComparison.OrdinalIgnoreCase))
                {
                    HighlightActiveFile(active.FilePath);
                }
                else
                {
                    _currentRootFolder = overrideRoot;
                    BuildFileTree(overrideRoot);
                }
                return;
            }

            _treeRootOverride = null;
        }

        TraversalCheck check = active != null
            ? CheckTreeRoot(active.FilePath)
            : TraversalCheck.Blocked(TraversalVerdict.NotFound);
        string? folder = check.ResolvedPath;

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            // 走っている構築の結果を捨てる。世代を進めないと、先行していた
            // BuildFileTreeAsync が後から世代チェックを通り、ここで空にしたはずの
            // ツリーを古い内容で描き直す（タブ 0 枚なのに中身が出る）。
            _treeBuildGeneration++;

            _currentRootFolder = null;
            _activeTreeItem = null;
            FileTree.ItemsSource = null;
            TreeRootLabel.Text = string.Empty;
            // 「リンクを経由しているだけ」なら設定で開けるので、そう案内する。
            // ネットワークや不存在では案内しない（設定を入れても開けないため）。
            TreeEmptyLabel.Text = check.NeedsSettingHint ? LinkBlockedTreeMessage : TreeEmptyMessage;
            TreeEmptyLabel.Visibility = Visibility.Visible;
            UpdateNavigationButtons();
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
        TreeEmptyLabel.Text = TreeEmptyMessage;
        TreeEmptyLabel.Visibility = children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 反映後に、現在アクティブなタブのファイルをハイライトする
        // （このツリーのルートに属している場合のみ）。
        //
        // 直親の一致ではなく**配下かどうか**で見る。「上へ」でルートを祖父母まで
        // 上げると直親とは一致せず、ハイライトが出なくなるため（ISSUE #84）。
        // 比較は実体パス同士で行う（ツリーの項目が実パスなのと同じ理由・ISSUE #54）。
        if (TabList.SelectedItem is DocumentTab active)
        {
            string? activeReal = FileTreeItem.ResolveCheckedPath(active.FilePath, isDirectory: false);
            if (activeReal != null && IsUnder(activeReal, rootFolder))
            {
                HighlightActiveFile(active.FilePath);
            }
        }

        // ルートが変わると「上へ」の可否も変わる（ドライブ直下まで来たら押せない）。
        UpdateNavigationButtons();
    }

    // ---- 戻る / 上の階層へ（ISSUE #84） -------------------------------

    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();

    private void UpButton_Click(object sender, RoutedEventArgs e) => GoUpFolder();

    /// <summary>
    /// 表示したファイルを履歴へ積む。<b>タブ切替のたびに呼ばれる。</b>
    ///
    /// <para>
    /// 積まないのは 2 つ。戻る操作で起きた切替（<see cref="_suppressHistoryPush"/>）と、
    /// 現在位置と同じパス（Ctrl+Tab の往復で履歴が伸びるのを防ぐ）。
    /// </para>
    /// </summary>
    private void PushHistory(string fullPath)
    {
        if (_suppressHistoryPush || string.IsNullOrEmpty(fullPath))
        {
            return;
        }

        if (_historyIndex >= 0
            && string.Equals(_history[_historyIndex], fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 戻った先から別のファイルを開いたら、それより後ろ（＝進む先）は捨てる。
        // ブラウザと同じ規則にしておく（「進む」を後から足しても筋が通る）。
        if (_historyIndex < _history.Count - 1)
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        }

        _history.Add(fullPath);
        _historyIndex = _history.Count - 1;

        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
            _historyIndex--;
        }
    }

    /// <summary>
    /// 1 つ前に表示していたファイルへ戻る。
    ///
    /// <para>
    /// 戻り先のタブが閉じられていても、履歴はパスで持っているので
    /// <see cref="OpenFile(string)"/> が開き直す。消えたファイルは履歴から取り除いて
    /// さらに 1 つ前を試す（ユーザー操作の結果ではないのでダイアログは出さない）。
    /// </para>
    /// </summary>
    private void GoBack()
    {
        while (_historyIndex > 0)
        {
            string target = _history[_historyIndex - 1];

            // 積んだ時点ではローカルのファイルでも、その後に祖先フォルダを UNC を指す
            // リンクへ差し替えられる。File.Exists はそれだけでネットワークへ出るので、
            // **実在確認より先に**祖先を検査する（CheckTreeRoot と同じ順序）。
            // 開くのは検査を通った元のパス。実体パスに差し替えると、リンク経由で開いた
            // 既存タブと別物になり、同じファイルのタブが 2 枚できる。
            if (FileTreeItem.CheckTraversal(target, isDirectory: false).ResolvedPath == null
                || !File.Exists(target))
            {
                _history.RemoveAt(_historyIndex - 1);
                _historyIndex--;
                continue;
            }

            _suppressHistoryPush = true;
            try
            {
                OpenFile(target);
            }
            finally
            {
                _suppressHistoryPush = false;
            }

            // 実際に表示が移ったときだけ位置を戻す。OpenFile は正規化に失敗すると
            // 黙って何もしないので、確認せずに減らすと「表示は変わらないのに履歴だけ
            // 戻る」状態になり、次の 1 回で 2 つ飛ぶ。
            if (TabList.SelectedItem is DocumentTab active
                && string.Equals(active.FilePath, target, StringComparison.OrdinalIgnoreCase))
            {
                _historyIndex--;
            }

            UpdateNavigationButtons();
            return;
        }

        // 戻れる項目が無くなった（全部消えていた）。
        UpdateNavigationButtons();
    }

    /// <summary>
    /// ファイルツリーのルートを 1 つ上のフォルダへ移す。<b>タブには触らない。</b>
    ///
    /// <para>
    /// 親フォルダは既存と同じ規則（<see cref="FileTreeItem.CheckTraversal"/>）で検査する。
    /// リンク経由・ネットワーク・不存在のときはルートを変えない。
    /// </para>
    /// </summary>
    private void GoUpFolder()
    {
        if (!_sidebarVisible || string.IsNullOrEmpty(_currentRootFolder))
        {
            return;
        }

        string? parent;
        try
        {
            parent = Path.GetDirectoryName(_currentRootFolder);
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(parent))
        {
            // ドライブ直下。これ以上は上がれない。
            return;
        }

        TraversalCheck check = FileTreeItem.CheckTraversal(parent, isDirectory: true);
        if (check.NeedsSettingHint)
        {
            MessageBox.Show(
                LinkBlockedDialogMessage,
                "Hirake - フォルダを開けません",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string? resolved = check.ResolvedPath;
        if (string.IsNullOrEmpty(resolved) || !Directory.Exists(resolved))
        {
            MessageBox.Show(
                "フォルダを開けません。",
                "Hirake - フォルダを開けません",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _treeRootOverride = resolved;
        _currentRootFolder = resolved;
        BuildFileTree(resolved);
    }

    /// <summary>戻る・上への 2 つのボタンの押せる/押せないを更新する。</summary>
    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;

        bool canGoUp = false;
        if (_sidebarVisible && !string.IsNullOrEmpty(_currentRootFolder))
        {
            try
            {
                canGoUp = !string.IsNullOrEmpty(Path.GetDirectoryName(_currentRootFolder));
            }
            catch
            {
                canGoUp = false;
            }
        }

        UpButton.IsEnabled = canGoUp;
    }

    /// <summary>
    /// <paramref name="path"/> が <paramref name="root"/> の配下かどうか。
    /// 区切り文字を付けて比べるので、<c>C:\doc</c> が <c>C:\docs</c> に一致しない。
    /// </summary>
    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(root);

            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            {
                fullRoot += Path.DirectorySeparatorChar;
            }

            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 仮想タブ（構造クエリ・指紋・統計・キャンバス）の入口用。
    /// 開けない理由が「設定で開ける」ものなら知らせる。
    ///
    /// ツリーや検索と違って表示先のラベルが無く、黙って null を返すと
    /// ボタンを押しても無反応に見えるため、ここだけダイアログを出す。
    /// </summary>
    private static string? ResolveTreeRootOrHint(string filePath)
    {
        TraversalCheck check = CheckTreeRoot(filePath);
        if (check.NeedsSettingHint)
        {
            MessageBox.Show(
                LinkBlockedDialogMessage,
                "Hirake - フォルダを開けません",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        return check.ResolvedPath;
    }

    /// <summary>
    /// <see cref="CheckTreeRoot"/> の案内つき版。出し分けが要る呼び出し側で使う。
    /// </summary>
    private static TraversalCheck CheckTreeRoot(string filePath)
    {
        try
        {
            string? folder = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(folder))
            {
                return TraversalCheck.Blocked(TraversalVerdict.NotFound);
            }

            return FileTreeItem.CheckTraversal(folder, isDirectory: true);
        }
        catch
        {
            return TraversalCheck.Blocked(TraversalVerdict.NotFound);
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

        // ツリーの項目は実パスなので、比較する側も実パスに揃える。リンク経由で
        // 直接開いたファイルは、揃えないと一致せずハイライトされない（ISSUE #54）。
        string? fullPath = FileTreeItem.ResolveCheckedPath(filePath, isDirectory: false);
        if (fullPath == null)
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
            // ルートが決まらない理由まで見る。リンクを経由しているだけなら
            // 設定で検索できるようになるので、そう案内する。
            TraversalCheck check = TabList.SelectedItem is DocumentTab activeTab
                ? CheckTreeRoot(activeTab.FilePath)
                : TraversalCheck.Blocked(TraversalVerdict.NotFound);

            ShowSearchPane();
            SearchResultsList.ItemsSource = null;
            SearchSummary.Text = string.Empty;
            SearchEmptyLabel.Text = check.NeedsSettingHint
                ? LinkBlockedSearchMessage
                : "検索対象のフォルダがありません";
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

        // テーマはアプリ全体で 1 つ。ただし適用先の Resources はウィンドウ単位の
        // 辞書なので、開いているウィンドウすべてに適用しないと表示がずれる。
        // ApplyThemeToAll は自ウィンドウも含めて回すため、ここで別途
        // ApplyEffectiveTheme() を呼ぶ必要はない。失敗時の扱いはそちらのコメントを参照。
        WindowManager.Instance.ApplyThemeToAll();
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

    // ---- セッション復元 -----------------------------------------------

    /// <summary>
    /// ウィンドウ記述子からタブを復元する。セッション復元とワークスペース復元は
    /// どちらもこの 1 本の経路を通る（片方だけ仮想タブに対応していない状態を作らないため）。
    /// 復元できない記述子はその 1 件だけ捨て、残りは開く。
    /// </summary>
    public void RestoreWindow(WindowDescriptor descriptor)
    {
        if (descriptor?.Tabs == null)
        {
            return;
        }

        // ワークスペースが持つスクロール位置を先に反映しておく（決定事項 2）。
        // タブ生成時の既存の復元経路（SettingsStore.GetScrollPosition）がそのまま
        // この値を読むため、タブ側へ新しい引数を通す必要がない。
        // 値が無いファイルはグローバル値のままとなり、自然にフォールバックする。
        ApplyWorkspaceScroll();

        // 復元できなかった記述子があると索引がずれるため、
        // 「記述子の索引 → 実際に開いたタブ」の対応を控える。
        DocumentTab? activeTab = null;

        for (int i = 0; i < descriptor.Tabs.Count; i++)
        {
            var tab = descriptor.Tabs[i];
            try
            {
                if (tab == null || !tab.CanRestore())
                {
                    continue;
                }

                int before = Tabs.Count;

                switch (tab.Kind)
                {
                    case TabKinds.Document:
                        OpenFile(tab.Path);
                        break;
                    case TabKinds.Canvas:
                        OpenCanvasForRoot(tab.Path, tab.View);
                        break;
                    case TabKinds.Structure:
                        OpenStructureForRoot(tab.Path);
                        break;
                    case TabKinds.Fingerprint:
                        OpenFingerprintForRoot(tab.Path);
                        break;
                    case TabKinds.Stats:
                        OpenStatsForRoot(tab.Path);
                        break;
                    case TabKinds.LinkCheck:
                        OpenLinkCheckForRoot(tab.Path);
                        break;
                }

                // この記述子で開かれた（または既存だった）タブを覚えておく。
                if (i == descriptor.ActiveIndex)
                {
                    activeTab = Tabs.Count > before
                        ? Tabs[^1]
                        : TabList.SelectedItem as DocumentTab;
                }
            }
            catch
            {
                // 個別タブの復元失敗は無視する。
            }
        }

        // アクティブタブは「記述子が指していたタブそのもの」を選ぶ。
        // 索引でクランプすると、途中の記述子が捨てられたときに別のタブが選ばれる。
        if (activeTab != null && Tabs.Contains(activeTab))
        {
            TabList.SelectedItem = activeTab;
        }
        else if (Tabs.Count > 0)
        {
            TabList.SelectedIndex = 0;
        }
    }

    // 閉じる直前に確定させた構成。Closed イベントの時点ではタブを破棄済みのため、
    // WindowManager から呼ばれる CaptureWindow はこちらを返す必要がある。
    private WindowDescriptor? _finalDescriptor;

    /// <summary>現在のタブ構成をウィンドウ記述子として取り出す。</summary>
    public WindowDescriptor CaptureWindow()
    {
        if (_finalDescriptor != null)
        {
            return _finalDescriptor;
        }

        var tabs = new List<TabDescriptor>();
        int activeIndex = 0;

        foreach (var tab in Tabs)
        {
            var descriptor = TabDescriptor.FromTab(tab, IsClipboardTempFile);
            if (descriptor == null)
            {
                continue;
            }

            if (ReferenceEquals(TabList.SelectedItem, tab))
            {
                activeIndex = tabs.Count;
            }
            tabs.Add(descriptor);
        }

        return new WindowDescriptor
        {
            WorkspaceId = WorkspaceId,
            Tabs = tabs,
            ActiveIndex = activeIndex,
            Bounds = CaptureBounds(),
        };
    }

    /// <summary>
    /// このウィンドウが開いている名前付きワークスペースの ID（無名なら null）。
    /// 設定は <see cref="WindowManager.TryAttachWorkspace"/> 経由でのみ行う
    /// （一意性を 1 か所で保証するため）。
    /// </summary>
    public string? WorkspaceId { get; private set; }

    /// <summary>WindowManager だけが使う所有権の設定口。</summary>
    internal void SetWorkspaceId(string? id) => WorkspaceId = id;

    /// <summary>
    /// ワークスペースの復元先として再利用してよいか（タブが無く、無名のウィンドウ）。
    /// </summary>
    internal bool IsReusableForWorkspace => Tabs.Count == 0 && string.IsNullOrEmpty(WorkspaceId);

    /// <summary>指定ファイルのタブを開いているか（URI・二重起動転送の宛先判定に使う）。</summary>
    public bool HasTabForFile(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        return Tabs.Any(t => t.GetType() == typeof(DocumentTab) &&
                             string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 保存済みのウィンドウ位置・サイズを適用する。現在のモニタ構成に収まらない場合は
    /// 既定位置のままにする（モニタを外した状態で復元して画面外に出るのを防ぐ）。
    /// </summary>
    public void ApplyBounds(WindowBounds? bounds)
    {
        if (bounds is not { IsValid: true })
        {
            return;
        }

        if (!IsWithinAnyScreen(bounds))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        WindowState = bounds.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    /// <summary>
    /// 矩形の中心が仮想デスクトップの範囲内にあるか。
    /// 端が少しはみ出す程度は許容し、モニタが減って完全に画面外になる場合だけ弾く。
    /// </summary>
    private static bool IsWithinAnyScreen(WindowBounds bounds)
    {
        try
        {
            double left = SystemParameters.VirtualScreenLeft;
            double top = SystemParameters.VirtualScreenTop;
            double right = left + SystemParameters.VirtualScreenWidth;
            double bottom = top + SystemParameters.VirtualScreenHeight;

            double centerX = bounds.Left + (bounds.Width / 2);
            double centerY = bounds.Top + (bounds.Height / 2);

            return centerX >= left && centerX <= right && centerY >= top && centerY <= bottom;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// このウィンドウが属するワークスペースのスクロール位置を設定する。
    /// 復元時はここを優先し、無ければ SettingsStore のグローバル値へフォールバックする。
    /// </summary>
    public void SetWorkspaceScroll(IReadOnlyDictionary<string, double>? scroll)
    {
        _workspaceScroll = scroll == null
            ? null
            : new Dictionary<string, double>(scroll, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, double>? _workspaceScroll;

    /// <summary>
    /// ワークスペースのスクロール位置をグローバル値へ反映する。
    /// 同じファイルを複数ウィンドウで開いた場合、グローバル値は後から動いた側で
    /// 上書きされるが、ワークスペースは自前の値を保存するため相互に影響しない。
    /// </summary>
    private void ApplyWorkspaceScroll()
    {
        if (_workspaceScroll == null)
        {
            return;
        }

        foreach (var pair in _workspaceScroll)
        {
            try
            {
                SettingsStore.Instance.UpdateScrollPosition(pair.Key, pair.Value);
            }
            catch
            {
                // 1 件の失敗で残りを止めない。
            }
        }
    }

    /// <summary>ワークスペース保存用に、開いている文書のスクロール位置を集める。</summary>
    public IReadOnlyDictionary<string, double> CaptureScroll()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Tabs)
        {
            if (tab.GetType() != typeof(DocumentTab) || string.IsNullOrEmpty(tab.FilePath))
            {
                continue;
            }

            double? position = SettingsStore.Instance.GetScrollPosition(tab.FilePath);
            if (position is double value)
            {
                result[tab.FilePath] = value;
            }
        }
        return result;
    }

    /// <summary>WindowManager からテーマ再適用を要求されたときの入口。</summary>
    public void ApplyEffectiveThemeFromHost() => ApplyEffectiveTheme();

    // ---- ワークスペース UI ---------------------------------------------

    /// <summary>
    /// ポップアップ一覧の 1 行分（表示用）。
    /// WPF のデータバインディングは非公開型のプロパティを解決できないため public にする
    /// （private のままだと束縛が黙って失敗し、一覧が空欄で並ぶ）。
    /// </summary>
    public sealed record WorkspaceRow(string Id, string Name, string TabLabel, string OpenMark);

    private void WorkspaceButton_Click(object sender, RoutedEventArgs e) => ToggleWorkspacePopup();

    /// <summary>ワークスペース一覧のポップアップを開閉する（Ctrl+Shift+W）。</summary>
    public void ToggleWorkspacePopup()
    {
        if (WorkspacePopup.IsOpen)
        {
            WorkspacePopup.IsOpen = false;
            return;
        }

        RefreshWorkspaceList();
        WorkspacePopup.IsOpen = true;
        WorkspaceNameBox.Focus();
    }

    private void RefreshWorkspaceList()
    {
        var all = WorkspaceStore.Instance.GetAll();

        var rows = all.Select(w => new WorkspaceRow(
            w.Id,
            w.Name,
            $"{w.Window?.Tabs?.Count ?? 0} タブ",
            WindowManager.Instance.FindByWorkspaceId(w.Id) != null ? "●" : "○")).ToList();

        WorkspaceList.ItemsSource = rows;
        WorkspaceEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 現在のウィンドウがワークスペースなら、その名前を入力欄の初期値にする
        // （名前を変えずに押せば上書き保存になる）。
        var current = WorkspaceStore.Instance.FindById(WorkspaceId);
        WorkspaceNameBox.Text = current?.Name ?? string.Empty;
        WorkspaceNameBox.SelectAll();
    }

    private void WorkspacePopup_Closed(object? sender, EventArgs e)
    {
        WorkspaceList.ItemsSource = null;
    }

    private void WorkspaceOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        WorkspacePopup.IsOpen = false;

        var workspace = WorkspaceStore.Instance.FindById(id);
        if (workspace == null)
        {
            return;
        }

        // 既に開いていれば前面化のみ。無ければ新しいウィンドウで開く。
        if (WindowManager.Instance.OpenWorkspace(workspace) == null)
        {
            MessageBox.Show(
                this,
                $"同時に開けるウィンドウは {WindowManager.MaxWindows} 枚までです。",
                "Hirake",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void WorkspaceDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        var workspace = WorkspaceStore.Instance.FindById(id);
        if (workspace == null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"ワークスペース「{workspace.Name}」を削除しますか？",
            "Hirake",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        WorkspaceStore.Instance.Delete(id);

        // 削除したワークスペースを開いているウィンドウは、無名のウィンドウに戻す
        // （閉じるときに存在しない ID へ書き戻そうとしないため）。
        // 1 枚に限らず全ウィンドウを対象にする。
        WindowManager.Instance.DetachWorkspace(id);

        RefreshWorkspaceList();
    }

    private void WorkspaceNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveCurrentWorkspace();
        }
    }

    private void WorkspaceSave_Click(object sender, RoutedEventArgs e) => SaveCurrentWorkspace();

    private void SaveCurrentWorkspace()
    {
        string name = WorkspaceStore.NormalizeName(WorkspaceNameBox.Text);
        if (name.Length == 0)
        {
            return;
        }

        // 別のワークスペースと同名なら上書き確認する。
        var existing = WorkspaceStore.Instance.FindByName(name);
        if (existing != null && !string.Equals(existing.Id, WorkspaceId, StringComparison.Ordinal))
        {
            // そのワークスペースを別のウィンドウが開いている場合は上書きさせない。
            // 許すと同じ ID を 2 枚が所有し、閉じた順に自動書き戻しが競合して
            // 古い構成が新しい構成を上書きできてしまう。
            var owner = WindowManager.Instance.FindByWorkspaceId(existing.Id);
            if (owner != null)
            {
                MessageBox.Show(
                    this,
                    $"ワークスペース「{name}」は別のウィンドウで開いています。\n" +
                    "そのウィンドウで保存するか、別の名前を付けてください。",
                    "Hirake",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                owner.BringToFront();
                return;
            }

            var answer = MessageBox.Show(
                this,
                $"ワークスペース「{name}」は既に存在します。上書きしますか？",
                "Hirake",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }
        }

        var saved = WorkspaceStore.Instance.Save(name, CaptureWindow(), CaptureScroll());
        if (saved == null)
        {
            MessageBox.Show(
                this,
                $"ワークスペースは {WorkspaceStore.MaxWorkspaces} 件までです。不要なものを削除してください。",
                "Hirake",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // 保存したワークスペースを、このウィンドウが開いている状態にする
        // （以後、閉じるときに自動で書き戻される）。
        WindowManager.Instance.TryAttachWorkspace(this, saved.Id);
        WorkspacePopup.IsOpen = false;
    }

    /// <summary>新しい空のウィンドウを開く（Ctrl+N）。</summary>
    public void OpenNewWindow()
    {
        if (WindowManager.Instance.CreateWindow() == null)
        {
            MessageBox.Show(
                this,
                $"同時に開けるウィンドウは {WindowManager.MaxWindows} 枚までです。",
                "Hirake",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private WindowBounds CaptureBounds()
    {
        bool maximized = WindowState == WindowState.Maximized;

        // 最大化中の Left/Top/Width/Height は最大化後の値になるため、
        // RestoreBounds（通常表示に戻したときの矩形）を使う。
        Rect rect = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

        return new WindowBounds
        {
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Width,
            Height = rect.Height,
            Maximized = maximized,
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        // 進行中の横断検索を打ち切る。
        CancelSearch();

        // タブを破棄すると構成を取り出せなくなるため、ここで確定させる。
        // WindowManager が Closed（base.OnClosed の中）で読むのもこの値。
        try
        {
            _finalDescriptor = CaptureWindow();

            // 名前付きワークスペースを開いていた場合、タブ構成の変更を自動で書き戻す
            // （決定事項 1。明示的な「保存」は新規作成と名前変更のためのもの）。
            if (!string.IsNullOrEmpty(WorkspaceId))
            {
                WorkspaceStore.Instance.Update(WorkspaceId, _finalDescriptor, CaptureScroll());
            }
        }
        catch
        {
            // ワークスペースの書き戻し失敗は握りつぶす。
        }

        // セッション（全ウィンドウの構成）の保存は WindowManager が Closed で行う。
        // ウィンドウごとに書き込むと、閉じた順に上書きし合って他のウィンドウが失われる。

        foreach (var tab in Tabs.ToList())
        {
            tab.Dispose();
        }
        Tabs.Clear();
        base.OnClosed(e);
    }
}
