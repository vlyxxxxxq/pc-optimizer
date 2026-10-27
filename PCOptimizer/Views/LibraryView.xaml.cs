using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class LibraryView : UserControl
{
    List<LibraryGame> _all = [];
    AppSettings? _settings;
    GameModeService? _gameMode;
    bool _loaded, _loading;

    public LibraryView() => InitializeComponent();

    public void Attach(AppSettings settings, GameModeService gameMode)
    {
        _settings = settings;
        _gameMode = gameMode;
        GameModeSwitch.IsChecked = settings.LaunchWithGameMode;
        FreeAlertSwitch.IsChecked = settings.FreeGameAlerts;
    }

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        CountText.Text = "Looking for games…";
        _all = await Task.Run(InstalledGames.Find);
        Render();
        _loading = false;
        // Covers, news badges and free games load in the background so the grid appears at once.
        _ = LoadFreeGamesAsync();
        foreach (var g in _all.ToList())
            if (await Task.Run(() => InstalledGames.LoadCover(g)) is { } cover) g.Cover = cover;
        foreach (var g in _all.Where(g => g.AppId is not null).ToList())
        {
            try { g.HasNews = (await GameFeeds.SteamNewsAsync(g.AppId!, 1)).Any(n => n.IsRecent); }
            catch { /* offline: no badges */ }
        }
    }

    async Task LoadFreeGamesAsync()
    {
        try
        {
            var free = await GameFeeds.FreeGamesAsync();
            FreeGames.ItemsSource = free;
            FreeText.Text = free.Count == 0
                ? "No free games at the moment. Check back later."
                : $"{free.Count} games you can claim for free right now. Claim them before the date and they're yours to keep.";
        }
        catch { FreeText.Text = "Couldn't load free games (are you offline?)."; }
    }

    void Claim_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Giveaway g && g.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Shell.Open(g.Url);
    }

    void FreeAlertSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.FreeGameAlerts = FreeAlertSwitch.IsChecked == true;
        _settings.Save();
    }

    void News_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LibraryGame game) return;
        if (game.AppId is null)
        {
            MessageBox.Show($"News is only available for Steam games so far.", "What's new");
            return;
        }
        new NewsWindow(game) { Owner = Window.GetWindow(this) }.Show();
    }

    void Render()
    {
        if (!IsInitialized) return;
        var q = SearchBox.Text.Trim();
        IEnumerable<LibraryGame> list = _all.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        list = SortPlayed.IsChecked == true ? list.OrderByDescending(g => g.Played)
             : SortName.IsChecked == true ? list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
             : list.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Name);
        var shown = list.ToList();
        Games.ItemsSource = shown;
        var total = TimeSpan.FromTicks(_all.Sum(g => g.Played.Ticks));
        CountText.Text = _all.Count == 0
            ? "No games found. Steam, Epic Games and GOG Galaxy installs are picked up automatically."
            : $"{_all.Count} games · {total.TotalHours:0} hours played" + (shown.Count != _all.Count ? $" · {shown.Count} shown" : "");
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    void Sort_Checked(object sender, RoutedEventArgs e) => Render();

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    void GameModeSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.LaunchWithGameMode = GameModeSwitch.IsChecked == true;
        _settings.Save();
    }

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LibraryGame game) return;
        InstalledGames.Start(game);
        // With automatic detection on, Game Mode will pick the game up by itself; otherwise switch it on now
        // (manual Game Mode adopts the game once its window appears).
        if (_settings is { LaunchWithGameMode: true, AutoGameMode: false } && _gameMode is { IsActive: false })
            await _gameMode.ToggleAsync();
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryGame { Folder: { } folder } && Directory.Exists(folder))
            Shell.Open(folder);
    }
}
