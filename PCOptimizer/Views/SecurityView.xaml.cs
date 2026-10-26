using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class SecurityView : UserControl
{
    bool _loaded;
    CancellationTokenSource? _scanCts;
    Func<IReadOnlyList<GameLibrary>>? _libraries;

    public SecurityView() => InitializeComponent();

    public void Attach(Func<IReadOnlyList<GameLibrary>> libraries) => _libraries = libraries;

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshAsync();
    }

    async Task RefreshAsync()
    {
        try
        {
            var s = await Security.StatusAsync();
            bool ok = s.Protected && !s.DefinitionsOld;
            StatusTitle.Text = !s.DefenderActive ? $"Protected by {s.Antivirus}"
                : !s.RealTime ? "Real-time protection is OFF"
                : s.DefinitionsOld ? "Protected, but definitions are out of date" : "You're protected";
            StatusDetail.Text = !s.DefenderActive
                ? "Another antivirus is in charge, so Microsoft Defender is standing by. Scans and updates below use Defender."
                : $"Microsoft Defender · real-time protection {(s.RealTime ? "on" : "off")} · tamper protection {(s.TamperProtected ? "on" : "off")}";
            var color = (Brush)FindResource(ok ? "GoodBrush" : s.RealTime ? "WarnBrush" : "DangerBrush");
            Shield.Foreground = color;
            ShieldBg.Background = new SolidColorBrush(ok ? Color.FromRgb(0x1E, 0x3A, 0x2E) : Color.FromRgb(0x3A, 0x2A, 0x1A));
            StatusCard.BorderBrush = ok ? (Brush)FindResource("CardBorderBrush") : color;

            ScanText.Text = $"Last quick scan: {Ago(s.LastQuickScan)} · last full scan: {Ago(s.LastFullScan)}.";
            DefsText.Text = s.DefinitionsUpdated is { } d ? $"Updated {Ago(d)}." + (s.DefinitionsOld ? " Update recommended." : "") : "Unknown.";
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Couldn't read Windows Security";
            StatusDetail.Text = ex.Message;
        }

        bool admin = Elevation.IsElevated;
        ExcludeButton.IsEnabled = IncludeButton.IsEnabled = admin;
        if (!admin) ExclusionText.Text = "Needs administrator mode (Tools → Restart as administrator).";
        else
        {
            var current = await Security.ExclusionsAsync();
            var games = GameFolders();
            int excluded = games.Count(g => current.Any(c => c.TrimEnd('\\').Equals(g, StringComparison.OrdinalIgnoreCase)));
            ExclusionText.Text = games.Count == 0 ? "No game libraries found."
                : $"{excluded} of {games.Count} game libraries excluded: {string.Join(", ", games)}";
        }
    }

    List<string> GameFolders() =>
        (_libraries?.Invoke() ?? []).Where(l => l.Name is null).Select(l => l.Root.TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static string Ago(DateTime? d) => d is not { } t ? "never" : (DateTime.Now - t) switch
    {
        var a when a.TotalHours < 1 => "just now",
        var a when a.TotalDays < 1 => $"{(int)a.TotalHours} h ago",
        var a => $"{(int)a.TotalDays} day{((int)a.TotalDays == 1 ? "" : "s")} ago",
    };

    async void Quick_Click(object sender, RoutedEventArgs e) => await ScanAsync(1);
    async void Full_Click(object sender, RoutedEventArgs e) => await ScanAsync(2);

    async Task ScanAsync(int type)
    {
        if (_scanCts is not null) { _scanCts.Cancel(); return; }
        _scanCts = new CancellationTokenSource();
        var button = type == 1 ? QuickButton : FullButton;
        (type == 1 ? FullButton : QuickButton).IsEnabled = false;
        button.Content = "Cancel";
        ScanProgress.Visibility = Visibility.Visible;
        ScanText.Text = type == 1 ? "Quick scan running (usually a few minutes)…" : "Full scan running (this can take an hour or more)…";
        try
        {
            var (ok, threats, _) = await Security.ScanAsync(type, _scanCts.Token);
            ScanText.Text = !ok ? "The scan couldn't run. Try from Windows Security."
                : threats ? "Threats were found. Windows Security is handling them: open it to review."
                : "No threats found.";
            if (threats) Shell.Open("windowsdefender://threat");
        }
        catch (OperationCanceledException) { ScanText.Text = "Scan cancelled."; }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            QuickButton.Content = "Quick scan";
            FullButton.Content = "Full scan";
            QuickButton.IsEnabled = FullButton.IsEnabled = true;
            ScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    async void Defs_Click(object sender, RoutedEventArgs e)
    {
        DefsButton.IsEnabled = false;
        DefsText.Text = "Updating…";
        var (ok, output) = await Security.UpdateDefinitionsAsync();
        DefsButton.IsEnabled = true;
        if (!ok) DefsText.Text = $"Couldn't update: {output.Split('\n').LastOrDefault()?.Trim()}";
        await RefreshAsync();
    }

    async void Exclude_Click(object sender, RoutedEventArgs e)
    {
        var games = GameFolders();
        if (games.Count == 0) return;
        if (MessageBox.Show("Exclude these game folders from real-time scanning?\n\n" + string.Join("\n", games.Select(g => "  • " + g)) +
                            "\n\nFiles in them won't be scanned as they're written. You can remove the exclusions any time.",
                "Game folder exclusions", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var (ok, output) = await Security.SetExclusionsAsync(games, add: true);
        if (!ok) MessageBox.Show($"Windows refused:\n{output}", "Game folder exclusions");
        await RefreshAsync();
    }

    async void Include_Click(object sender, RoutedEventArgs e)
    {
        var (ok, output) = await Security.SetExclusionsAsync(GameFolders(), add: false);
        if (!ok) MessageBox.Show($"Windows refused:\n{output}", "Game folder exclusions");
        await RefreshAsync();
    }

    void OpenWs_Click(object sender, RoutedEventArgs e) => Shell.Open("windowsdefender:");
}
