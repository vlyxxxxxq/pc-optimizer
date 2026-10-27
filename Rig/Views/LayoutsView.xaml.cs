using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rig.Services;

namespace Rig.Views;

public partial class LayoutsView : UserControl
{
    AppSettings? _settings;
    List<WindowLayout> _layouts = [];

    public LayoutsView() => InitializeComponent();

    public void Attach(AppSettings settings)
    {
        _settings = settings;
        LaunchCheck.IsChecked = settings.LayoutsLaunchApps;
        _layouts = WindowLayouts.Load();
    }

    public IReadOnlyList<WindowLayout> Layouts => _layouts;

    public void EnsureLoaded()
    {
        int open = WindowLayouts.Capture("").Windows.Count;
        OpenNowText.Text = $"{open} window{(open == 1 ? "" : "s")} open right now. Each one's position, size and maximised state is saved.";
        if (NameBox.Text.Length == 0) NameBox.Text = $"Layout {_layouts.Count + 1}";
        Render();
    }

    void Render()
    {
        LayoutList.ItemsSource = null;
        LayoutList.ItemsSource = _layouts;
        EmptyText.Visibility = _layouts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { NameBox.Focus(); return; }
        var layout = WindowLayouts.Capture(name);
        _layouts.RemoveAll(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _layouts.Add(layout);
        WindowLayouts.Save(_layouts);
        StatusText.Text = $"Saved \"{name}\" with {layout.Windows.Count} windows.";
        NameBox.Text = $"Layout {_layouts.Count + 1}";
        Render();
    }

    void NameBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Save_Click(sender, e); }

    void LaunchCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.LayoutsLaunchApps = LaunchCheck.IsChecked == true;
        _settings.Save();
    }

    void Restore_Click(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is WindowLayout l) Restore(l); }

    /// <summary>Puts windows back; also used by the command palette. Missing apps are opened first if that's switched on.</summary>
    public async void Restore(WindowLayout layout)
    {
        var (placed, missing) = WindowLayouts.Apply(layout);
        if (missing.Count > 0 && _settings?.LayoutsLaunchApps == true && WindowLayouts.Launch(missing) > 0)
        {
            StatusText.Text = $"Opening {missing.Count} app(s) from \"{layout.Name}\"…";
            await Task.Delay(TimeSpan.FromSeconds(6)); // give them time to show their windows
            (placed, missing) = WindowLayouts.Apply(layout);
        }
        StatusText.Text = $"Restored \"{layout.Name}\": {placed} window{(placed == 1 ? "" : "s")} placed." +
            (missing.Count > 0 ? $" Not running: {string.Join(", ", missing.Select(Path.GetFileNameWithoutExtension))}." : "");
    }

    void Update_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WindowLayout old) return;
        var fresh = WindowLayouts.Capture(old.Name);
        _layouts[_layouts.IndexOf(old)] = fresh;
        WindowLayouts.Save(_layouts);
        StatusText.Text = $"Updated \"{old.Name}\" with {fresh.Windows.Count} windows.";
        Render();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not WindowLayout l) return;
        if (MessageBox.Show($"Delete the layout \"{l.Name}\"?", "Window Layouts", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _layouts.Remove(l);
        WindowLayouts.Save(_layouts);
        Render();
    }
}
