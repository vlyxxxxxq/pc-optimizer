using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Rig.Services;

namespace Rig.Views;

public partial class TweaksView : UserControl
{
    List<TweakGroup> _groups = [];
    bool _loaded;

    public TweaksView()
    {
        InitializeComponent();
        Tweak.Failed += (t, ex) => MessageBox.Show(
            $"Couldn't change \"{t.Name}\":\n{ex.Message}" +
            (t.RequiresAdmin && !Elevation.IsElevated ? "\n\nThis tweak needs administrator rights." : ""),
            "Tweak failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        Tweak.Changed += t =>
        {
            if (t.Note == "Restart Explorer") ExplorerBanner.Visibility = Visibility.Visible;
            if (t.Note is "Restart PC" or "Sign out") RebootBanner.Visibility = Visibility.Visible;
            UpdateSummary();
        };
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _groups = Tweaks.All();
        foreach (var t in _groups.SelectMany(g => g.Tweaks)) t.Refresh();
        GroupList.ItemsSource = _groups;
        RestorePointButton.IsEnabled = Elevation.IsElevated;
        AdminText.Text = Elevation.IsElevated
            ? "Running as administrator, so every tweak is available. Consider a restore point first."
            : "Tweaks marked Admin are locked. Use Tools → Restart as administrator to unlock them.";
        UpdateSummary();
    }

    void UpdateSummary()
    {
        var all = _groups.SelectMany(g => g.Tweaks).ToList();
        var pending = all.Count(t => t.Recommended && !t.IsOn && t.CanChange);
        SummaryText.Text = $"{all.Count(t => t.IsOn)} of {all.Count} tweaks applied" +
                           (pending > 0 ? $" · {pending} recommended left" : " · all recommended done");
    }

    void Recommended_Click(object sender, RoutedEventArgs e)
    {
        var todo = _groups.SelectMany(g => g.Tweaks).Where(t => t.Recommended && !t.IsOn && t.CanChange).ToList();
        if (todo.Count == 0)
        {
            MessageBox.Show("All recommended tweaks you can change are already applied.", "Rig");
            return;
        }
        var list = string.Join("\n", todo.Select(t => "  • " + t.Name));
        if (MessageBox.Show($"Apply these {todo.Count} tweaks?\n\n{list}\n\nYou can switch any of them back later.",
                "Apply recommended", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        foreach (var t in todo) t.IsOn = true;
    }

    async void RestorePoint_Click(object sender, RoutedEventArgs e)
    {
        RestorePointButton.IsEnabled = false;
        RestorePointButton.Content = "Creating…";
        var (code, output) = await Tweaks.CreateRestorePointAsync();
        RestorePointButton.Content = "Create restore point";
        RestorePointButton.IsEnabled = true;
        MessageBox.Show(code == 0 && output.Length == 0
                ? "Restore point created. You can roll back from System Properties → System Protection."
                : $"Windows said:\n{output}\n\n(Windows allows one restore point per 24 hours, and System Protection must be on for C:.)",
            "Restore point", MessageBoxButton.OK, code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

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
