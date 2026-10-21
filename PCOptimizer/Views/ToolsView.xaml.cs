using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class ToolsView : UserControl
{
    bool _loaded;

    public ToolsView()
    {
        InitializeComponent();
        ElevateButton.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        ElevateText.Text = Elevation.IsElevated
            ? "PC Optimizer is running with administrator rights. All features are available."
            : "Needed to clean the Windows temp folder and change startup items for all users.";
    }

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadPlansAsync();
    }

    async Task LoadPlansAsync()
    {
        try { PlanList.ItemsSource = await PowerPlans.ListAsync(); }
        catch (Exception ex) { ShowOutput($"Couldn't read power plans: {ex.Message}"); }
    }

    async void Plan_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string guid) return;
        if (!await PowerPlans.SetActiveAsync(guid)) ShowOutput("Windows refused to switch the power plan.");
        await LoadPlansAsync();
    }

    async void Ultimate_Click(object sender, RoutedEventArgs e)
    {
        var plans = PlanList.ItemsSource as IEnumerable<PowerPlan>;
        if (plans?.Any(p => p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)) == true)
        {
            ShowOutput("Ultimate Performance is already in your list.");
            return;
        }
        var (ok, output) = await PowerPlans.AddUltimateAsync();
        ShowOutput(ok ? "Ultimate Performance added. Activate it below, or let Game Mode use it while you play."
                      : $"Windows couldn't add it: {output}");
        await LoadPlansAsync();
    }

    static readonly Dictionary<string, (string Title, string Commands)> Repairs = new()
    {
        ["system"] = ("Scan and repair system files",
            "DISM /Online /Cleanup-Image /RestoreHealth && sfc /scannow && echo. && echo Done. You can close this window."),
        ["network"] = ("Reset network",
            "netsh winsock reset && netsh int ip reset && ipconfig /flushdns && echo. && echo Done. Restart your PC to finish."),
        ["update"] = ("Fix Windows Update",
            "net stop wuauserv & net stop bits & net stop cryptsvc & rd /s /q %windir%\\SoftwareDistribution\\Download & " +
            "net start cryptsvc & net start bits & net start wuauserv & echo. & echo Done. Try Windows Update again."),
        ["disk"] = ("Check drive C:", "chkdsk C: /scan && echo. && echo Done."),
    };

    void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string key || !Repairs.TryGetValue(key, out var repair)) return;
        if (MessageBox.Show($"{repair.Title}?\n\nAn administrator console window will open and run:\n\n{repair.Commands}",
                repair.Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        if (!Shell.RunElevatedConsole(repair.Commands)) ShowOutput("Cancelled: the administrator prompt was declined.");
    }

    void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (!Elevation.RestartElevated()) ShowOutput("Administrator restart was cancelled.");
    }

    async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        var (code, output) = await Shell.RunAsync("ipconfig", "/flushdns");
        ShowOutput(code == 0 ? "DNS cache flushed." : output);
    }

    async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Your taskbar and open File Explorer windows will close and reopen. Continue?",
                "Restart Explorer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); await p.WaitForExitAsync(); } catch { }
            finally { p.Dispose(); }
        }
        await Task.Delay(800);
        // Windows usually restarts the shell itself; start it only if it didn't.
        if (Process.GetProcessesByName("explorer").Length == 0) Shell.Open("explorer.exe");
        ShowOutput("Windows Explorer restarted.");
    }

    void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string target) Shell.Open(target);
    }

    void ShowOutput(string text)
    {
        FixOutput.Text = text;
        FixOutput.Visibility = Visibility.Visible;
    }
}
