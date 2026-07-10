using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace MdViewer;

public partial class MainWindow : Window, IDocumentTabHost
{
    private readonly string _assetsDirectory;
    private readonly string _tempDirectory;

    public ObservableCollection<DocumentTab> Tabs { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        _tempDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MdViewer",
            "temp");

        UpdateEmptyState();
        UpdateTitle();
    }

    // ---- ファイルを開く -----------------------------------------------

    public void OpenFile(string path)
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
            return;
        }

        var tab = new DocumentTab(fullPath, this, _assetsDirectory, _tempDirectory);
        tab.WebView.Visibility = Visibility.Collapsed;
        WebViewHost.Children.Add(tab.WebView);
        Tabs.Add(tab);
        TabList.SelectedItem = tab;

        UpdateEmptyState();
    }

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
                    "MdViewer - タブ初期化エラー",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    private void UpdateTitle()
    {
        Title = TabList.SelectedItem is DocumentTab tab
            ? $"{tab.FileName} - MdViewer"
            : "MdViewer";
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PrintButton.IsEnabled = Tabs.Count > 0;
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

    private void PrintActiveTab()
    {
        if (TabList.SelectedItem is DocumentTab tab)
        {
            tab.ShowPrintUI();
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

        switch (e.Key)
        {
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files)
            {
                OpenFile(file);
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

    public void OpenFileInNewTab(string path) => OpenFile(path);

    public void ShortcutCloseActive() => CloseActiveTab();

    public void ShortcutNextTab() => SelectAdjacentTab(1);

    public void ShortcutPrevTab() => SelectAdjacentTab(-1);

    public void ShortcutOpenFile() => ShowOpenDialog();

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tab in Tabs.ToList())
        {
            tab.Dispose();
        }
        Tabs.Clear();
        base.OnClosed(e);
    }
}
