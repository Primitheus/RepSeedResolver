using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CUE4Parse.UE4.Versions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace RepSeedResolver;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private JObject? _repLayout;
    private JObject? _classNetCache;
    private string[] _allGames = [];
    private bool _updatingGameFilter;

    public MainWindow()
    {
        InitializeComponent();
        PopulateGameCombo();
        TxtOutputDir.Text = Environment.CurrentDirectory;
    }

    private void PopulateGameCombo()
    {
        _allGames = Enum.GetNames<EGame>();
        foreach (var name in _allGames)
            CmbGame.Items.Add(name);

        CmbGame.Text = "GAME_UE5_7";
        CmbGame.AddHandler(
            System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(OnGameTextChanged));
    }

    private void OnGameTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingGameFilter) return;
        _updatingGameFilter = true;

        var tb = CmbGame.Template.FindName("PART_EditableTextBox", CmbGame) as TextBox;
        if (tb == null) { _updatingGameFilter = false; return; }

        var text = tb.Text;
        var caret = tb.CaretIndex;

        CmbGame.Items.Clear();
        foreach (var g in _allGames.Where(g =>
            string.IsNullOrEmpty(text) || g.Contains(text, StringComparison.OrdinalIgnoreCase)))
            CmbGame.Items.Add(g);

        CmbGame.IsDropDownOpen = CmbGame.Items.Count > 0 && !string.IsNullOrEmpty(text);

        tb.Text = text;
        tb.CaretIndex = caret;
        tb.SelectionLength = 0;

        _updatingGameFilter = false;
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Margin = new Thickness(7);
            MaxRestoreIcon.Data = Geometry.Parse("M 0 2 H 8 V 10 H 0 Z M 2 2 V 0 H 10 V 8 H 8");
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            MaxRestoreIcon.Data = Geometry.Parse("M 0 0 H 10 V 10 H 0 Z");
        }
    }

    private void BrowseFolder(TextBox target, string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (dlg.ShowDialog() == true)
            target.Text = dlg.FolderName;
    }

    private void BrowseFile(TextBox target, string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        if (dlg.ShowDialog() == true)
            target.Text = dlg.FileName;
    }

    private void BrowsePaksDir(object sender, RoutedEventArgs e) =>
        BrowseFolder(TxtPaksDir, "Select Paks Directory");

    private void BrowseSeedJson(object sender, RoutedEventArgs e) =>
        BrowseFile(TxtSeedPath, "Select replication_seed.json", "JSON files|*.json|All files|*.*");

    private void BrowseUsmap(object sender, RoutedEventArgs e) =>
        BrowseFile(TxtUsmapPath, "Select .usmap file", "Usmap files|*.usmap|All files|*.*");

    private void BrowseOutputDir(object sender, RoutedEventArgs e) =>
        BrowseFolder(TxtOutputDir, "Select Output Directory");

    private async void RunClicked(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            return;
        }

        var paksDir = TxtPaksDir.Text.Trim();
        var seedPath = TxtSeedPath.Text.Trim();
        var gameName = CmbGame.Text.Trim();
        var usmapPath = TxtUsmapPath.Text.Trim();
        var outputDir = TxtOutputDir.Text.Trim();

        if (string.IsNullOrEmpty(paksDir) || string.IsNullOrEmpty(seedPath))
        {
            AppendLog("[!] Paks Dir and Seed JSON are required.");
            return;
        }

        if (!Enum.TryParse<EGame>(gameName, ignoreCase: true, out var eGame))
        {
            AppendLog($"[!] Unknown game: {gameName}");
            return;
        }

        TxtLog.Clear();
        Progress.Value = 0;
        BtnRun.Content = "Cancel";
        SetInputEnabled(false);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var resolver = new SeedResolver(
                msg => Dispatcher.Invoke(() => AppendLog(msg)),
                pct => Dispatcher.Invoke(() => Progress.Value = pct));

            var options = new ResolveOptions(paksDir, seedPath, eGame, usmapPath, outputDir);
            var result = await resolver.RunAsync(options, ct);

            _repLayout = result.RepLayout;
            _classNetCache = result.ClassNetCache;
            RefreshResultTree();

            SetStatus("Complete", FindResource("SuccessBrush") as Brush);
        }
        catch (OperationCanceledException)
        {
            AppendLog("[!] Cancelled.");
            SetStatus("Cancelled", FindResource("FgSecondary") as Brush);
        }
        catch (Exception ex)
        {
            AppendLog($"[!] Error: {ex.Message}");
            SetStatus("Failed", FindResource("ErrorBrush") as Brush);
        }
        finally
        {
            _cts = null;
            BtnRun.Content = "Run";
            SetInputEnabled(true);
        }
    }

    private void SetInputEnabled(bool enabled)
    {
        TxtPaksDir.IsEnabled = enabled;
        TxtSeedPath.IsEnabled = enabled;
        CmbGame.IsEnabled = enabled;
        TxtUsmapPath.IsEnabled = enabled;
        TxtOutputDir.IsEnabled = enabled;
    }

    private void AppendLog(string msg)
    {
        TxtLog.AppendText(msg + "\n");
        TxtLog.ScrollToEnd();
    }

    private void SetStatus(string text, Brush? brush)
    {
        TxtStatus.Text = text;
        TxtStatus.Foreground = brush ?? FindResource("FgSecondary") as Brush
                                     ?? Brushes.Gray;
    }

    private void ResultFileChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshResultTree();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        RefreshResultTree();
    }

    private void RefreshResultTree()
    {
        if (ResultTree == null) return;

        var isRepLayout = CmbResultFile.SelectedIndex == 0;
        var json = isRepLayout ? _repLayout : _classNetCache;
        if (json == null) { ResultTree.ItemsSource = null; return; }

        var classesObj = json["classes"] as JObject;
        if (classesObj == null) { ResultTree.ItemsSource = null; return; }

        var filter = TxtSearch.Text.Trim();
        var items = new List<ResultNode>();

        foreach (var prop in classesObj.Properties())
        {
            if (!string.IsNullOrEmpty(filter)
                && !prop.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            items.Add(new ResultNode(prop.Name, prop.Value) { IsTopLevel = true });
        }

        ResultTree.ItemsSource = items;
    }
}
