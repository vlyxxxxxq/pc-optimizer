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
        // Covers load in the background so the grid appears at once.
        foreach (var g in _all.ToList())
            if (await Task.Run(() => InstalledGames.LoadCover(g)) is { } cover) g.Cover = cover;
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
