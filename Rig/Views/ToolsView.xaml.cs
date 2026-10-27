using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Rig.Services;

namespace Rig.Views;

public partial class ToolsView : UserControl
{
    bool _loaded;

    public ToolsView()
    {
        InitializeComponent();
        ElevateButton.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        ElevateText.Text = Elevation.IsElevated
            ? "Rig is running with administrator rights. All features are available."
            : "Needed to clean the Windows temp folder and change startup items for all users.";
    }

    public async void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        GpuList.ItemsSource = GpuInfo.Load();
        RenderPause();
        RenderMaintenance();
        await LoadPlansAsync();
    }

    // ---------- Maintenance night ----------

    AppSettings? _settings;

    PowerTimer? _power;

    public void AttachPowerTimer(PowerTimer power)
    {
        _power = power;
        power.Changed += (_, _) => RenderPower();
        RenderPower();
    }

    void RenderPower()
    {
        if (_power is null) return;
        PowerStatus.Text = _power.Status;
        PowerButton.Content = _power.Armed ? "Cancel" : "Start";
        PowerButton.Style = (Style)FindResource(_power.Armed ? typeof(Button) : "PrimaryButton");
    }

    void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_power is null) return;
        if (_power.Armed) { _power.Cancel(); return; }
        var action = (PowerAction)PowerActionBox.SelectedIndex;
        if (PowerDownloadsRadio.IsChecked == true) _power.ArmAfterDownloads(action);
        else if (int.TryParse(PowerMinutesBox.Text, out int minutes) && minutes is >= 1 and <= 1440) _power.ArmIn(action, TimeSpan.FromMinutes(minutes));
        else PowerStatus.Text = "Enter 1 to 1440 minutes.";
    }

    public void Attach(AppSettings settings)
    {
        _settings = settings;
        DayOfWeek[] days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        foreach (var d in days)
        {
            var rb = new RadioButton { Content = d.ToString()[..3], GroupName = "MaintDay", Margin = new Thickness(0, 0, 6, 0),
                                       IsChecked = settings.MaintenanceDay == d };
            rb.Checked += async (_, _) => { settings.MaintenanceDay = d; await SaveMaintenanceAsync(); };
            MaintDays.Items.Add(rb);
        }
        foreach (var h in new[] { 1, 2, 3, 4, 5 })
        {
            var rb = new RadioButton { Content = $"{h} AM", GroupName = "MaintHour", Margin = new Thickness(0, 0, 6, 0),
                                       IsChecked = settings.MaintenanceHour == h };
            rb.Checked += async (_, _) => { settings.MaintenanceHour = h; await SaveMaintenanceAsync(); };
            MaintHours.Items.Add(rb);
        }
    }

    void RenderMaintenance()
    {
        if (_settings is null) return;
        MaintSwitch.IsChecked = _settings.MaintenanceEnabled;
        MaintUpdates.IsChecked = _settings.MaintenanceInstallUpdates;
        var when = $"every {_settings.MaintenanceDay} at {_settings.MaintenanceHour} AM";
        MaintText.Text = (_settings.MaintenanceEnabled
                             ? $"On: cleans junk and checks app updates {when}, even if this app isn't open. Runs later if your PC was off; skipped during games."
                             : "Cleans junk and checks app updates once a week while you sleep, then leaves you a summary.") +
                         (_settings.LastMaintenanceSummary is { } s && _settings.LastMaintenance is { } t ? $"\nLast run {t:g}: {s}" : "");
    }

    async Task SaveMaintenanceAsync()
    {
        if (_settings is null) return;
        _settings.Save();
        if (_settings.MaintenanceEnabled)
        {
            var (ok, output) = await Maintenance.ScheduleAsync(_settings);
            if (!ok) ShowOutput($"Couldn't schedule maintenance: {output}");
        }
        RenderMaintenance();
    }

    async void Maint_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.MaintenanceEnabled = MaintSwitch.IsChecked == true;
        if (!_settings.MaintenanceEnabled) { Maintenance.Unschedule(); _settings.Save(); RenderMaintenance(); return; }
        await SaveMaintenanceAsync();
    }

    async void MaintUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.MaintenanceInstallUpdates = MaintUpdates.IsChecked == true;
        await SaveMaintenanceAsync();
    }

    async void MaintRun_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        MaintRunButton.IsEnabled = false;
        var summary = await Maintenance.RunAsync(_settings, new Progress<string>(s => MaintText.Text = s));
        MaintRunButton.IsEnabled = true;
        RenderMaintenance();
        ShowOutput($"Maintenance done. {summary}");
    }

    void RenderPause()
    {
        var until = UpdatePause.PausedUntil();
        bool admin = Elevation.IsElevated;
        Pause1.IsEnabled = Pause2.IsEnabled = Pause5.IsEnabled = ResumeButton.IsEnabled = admin;
        ResumeButton.Visibility = until is null ? Visibility.Collapsed : Visibility.Visible;
        PauseText.Text = until is { } u
            ? $"Paused until {u:D}. Security fixes resume after that; don't stay paused forever."
            : "Stop updates and surprise restarts during a tournament or launch week. Windows allows up to 5 weeks." +
              (admin ? "" : " Needs administrator mode (button at the top).");
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string w) return;
        try { UpdatePause.Pause(int.Parse(w)); }
        catch (Exception ex) { ShowOutput($"Couldn't pause updates: {ex.Message}"); }
        RenderPause();
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        try { UpdatePause.Resume(); }
        catch (Exception ex) { ShowOutput($"Couldn't resume updates: {ex.Message}"); }
        RenderPause();
    }

    void Driver_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is string url) Shell.Open(url);
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
