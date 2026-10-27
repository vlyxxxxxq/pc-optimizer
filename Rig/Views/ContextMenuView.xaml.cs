using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Rig.Services;

namespace Rig.Views;

public partial class ContextMenuView : UserControl
{
    List<MenuEntry> _all = [];
    bool _loaded;

    public ContextMenuView()
    {
        InitializeComponent();
        MenuEntry.Changed += _ => { ExplorerBanner.Visibility = Visibility.Visible; Render(); };
        MenuEntry.Failed += (e, ex) => MessageBox.Show(
            $"Couldn't change \"{e.Name}\":\n{ex.Message}" + (e.NeedsAdmin && !Elevation.IsElevated
                ? "\n\nThis item is installed for all users, so it needs administrator mode (Tools → Restart as administrator)." : ""),
            "Right-click menu", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Reload();
    }

    void Reload()
    {
        try { _all = ShellMenu.Load(); }
        catch (Exception ex) { CountText.Text = $"Couldn't read the menu: {ex.Message}"; return; }
        Render();
    }

    void Render()
    {
        var shown = ShowWindows.IsChecked == true ? _all : _all.Where(e => !e.IsWindows).ToList();
        EntryList.ItemsSource = shown;
        int off = _all.Count(e => !e.IsOn);
        CountText.Text = $"{shown.Count} items from {(ShowWindows.IsChecked == true ? "apps and Windows" : "your apps")} · {off} switched off";
    }

    void ShowWindows_Click(object sender, RoutedEventArgs e) => Render();
    void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); await p.WaitForExitAsync(); } catch { }
            finally { p.Dispose(); }
        }
        await Task.Delay(800);
        if (Process.GetProcessesByName("explorer").Length == 0) Shell.Open("explorer.exe");
        ExplorerBanner.Visibility = Visibility.Collapsed;
    }
}
