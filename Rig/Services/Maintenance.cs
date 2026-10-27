using System.Text;

namespace Rig.Services;

/// <summary>
/// Weekly "maintenance night": a Windows scheduled task starts "Rig.exe --maintenance", which cleans
/// the safe junk categories, checks (and optionally installs) app updates, then shows a summary notification.
/// Runs late if the PC was off or asleep at the scheduled time; skips itself if a game is being played.
/// </summary>
public static class Maintenance
{
    const string TaskName = "Rig Maintenance";

    static string Exe => Installer.IsRunningInstalled || !File.Exists(Installer.InstalledExe)
        ? Environment.ProcessPath! : Installer.InstalledExe;

    public static async Task<(bool Ok, string Output)> ScheduleAsync(AppSettings s)
    {
        // Register-ScheduledTask (not schtasks) so "run as soon as possible after a missed start" can be set.
        var at = DateTime.Today.AddHours(s.MaintenanceHour).ToString("HH:mm");
        var exe = Exe.Replace("'", "''");
        var script =
            $"$a = New-ScheduledTaskAction -Execute '{exe}' -Argument '--maintenance'; " +
            $"$t = New-ScheduledTaskTrigger -Weekly -DaysOfWeek {s.MaintenanceDay} -At {at}; " +
            "$o = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2); " +
            $"Register-ScheduledTask -TaskName '{TaskName}' -Action $a -Trigger $t -Settings $o -Description 'Weekly cleanup by Rig' -Force | Out-Null";
        if (Places.TaskLog is { } log) { File.AppendAllText(log, $"schedule {TaskName} -> {Exe}\n"); return (true, ""); }
        var (code, output) = await Shell.RunAsync("powershell", $"-NoProfile -Command \"{script}\"", Encoding.UTF8);
        return (code == 0, output.Trim());
    }

    public static void Unschedule() => DeleteTask(TaskName);

    public static void DeleteTask(string name)
    {
        if (Places.TaskLog is { } log) { File.AppendAllText(log, $"delete {name}\n"); return; }
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks", $"/delete /tn \"{name}\" /f")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        });
        p?.WaitForExit(10000);
    }

    /// <summary>Does the work and returns a one-paragraph summary (also saved to settings).</summary>
    public static async Task<string> RunAsync(AppSettings s, IProgress<string>? progress = null)
    {
        var parts = new List<string>();

        progress?.Report("Cleaning junk files…");
        long freed = await Task.Run(() =>
        {
            long total = 0;
            foreach (var c in JunkCleaner.CreateCategories().Where(c => c.IsSelected && !c.IsRecycleBin))
            {
                var scan = JunkCleaner.Scan(c);
                if (scan.Count == 0) continue;
                c.ApplyScan(scan);
                total += JunkCleaner.Clean(c).Freed;
            }
            return total;
        });
        parts.Add($"Freed {Fmt.Bytes(freed)} of junk.");

        try
        {
            progress?.Report("Checking app updates…");
            var updates = await Winget.UpgradesAsync();
            if (updates.Count == 0) parts.Add("All apps are up to date.");
            else if (s.MaintenanceInstallUpdates)
            {
                int ok = 0;
                foreach (var u in updates)
                {
                    progress?.Report($"Updating {u.Name}…");
                    if ((await Winget.UpgradeAsync(u.Id)).Ok) ok++;
                }
                parts.Add($"Updated {ok} of {updates.Count} apps.");
            }
            else parts.Add($"{updates.Count} app update(s) waiting in Apps.");
        }
        catch { parts.Add("Couldn't check app updates (winget unavailable)."); }

        var summary = string.Join(" ", parts);
        var when = DateTime.Now;
        s.LastMaintenance = when;
        s.LastMaintenanceSummary = summary;
        // Write just these two fields onto the newest file, so a stale copy never undoes choices made meanwhile.
        AppSettings.Update(fresh => { fresh.LastMaintenance = when; fresh.LastMaintenanceSummary = summary; });
        return summary;
    }

    /// <summary>Entry point for the scheduled task (no main window).</summary>
    public static async Task RunHeadlessAsync()
    {
        var s = AppSettings.Load();
        if (!s.MaintenanceEnabled) return;

        // Never interrupt a game: try again next week (or the user can press "Run now").
        var detector = new GameDetector();
        detector.RefreshLibraries();
        if (detector.Detect(s) is not null) return;

        var summary = await RunAsync(s);
        if (!s.Notifications) return;
        using var tray = new TrayIcon();
        tray.Notify("Maintenance done", summary);
        await Task.Delay(8000); // keep the icon alive long enough for the notification to show
    }
}
