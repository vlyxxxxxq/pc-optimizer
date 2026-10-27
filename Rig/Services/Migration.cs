using System.Diagnostics;
using Microsoft.Win32;

namespace Rig.Services;

/// <summary>
/// Where Rig puts things on this PC. RIG_TEST_INSTALL_ROOT moves all of it (folders, Start menu, desktop, and
/// registry under HKCU\Software\RigTest) into a sandbox, and scheduled tasks are only logged, so install,
/// migration and uninstall can be tested without touching the real system.
/// </summary>
public static class Places
{
    static readonly string? TestRoot = Environment.GetEnvironmentVariable("RIG_TEST_INSTALL_ROOT") is { Length: > 0 } r ? r : null;
    public static bool IsTest => TestRoot is not null;

    public static string LocalAppData => TestRoot is { } r ? Dir(r, "LocalAppData") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string StartMenuPrograms => TestRoot is { } r ? Dir(r, "StartMenu") : Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public static string Desktop => TestRoot is { } r ? Dir(r, "Desktop") : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    static string Dir(string root, string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }

    /// <summary>HKCU\{path}, or HKCU\Software\RigTest\{path} in a test.</summary>
    public static RegistryKey? OpenUserKey(string path, bool write = false) =>
        write ? Registry.CurrentUser.CreateSubKey(Map(path)) : Registry.CurrentUser.OpenSubKey(Map(path));

    public static void DeleteUserKey(string path) => Registry.CurrentUser.DeleteSubKeyTree(Map(path), throwOnMissingSubKey: false);

    static string Map(string path) => TestRoot is null ? path : @"Software\RigTest\" + path;

    /// <summary>In a test, scheduled-task commands are written here instead of run.</summary>
    public static string? TaskLog => TestRoot is { } r ? Path.Combine(r, "tasks.log") : null;
}

/// <summary>
/// Turns a PC Optimizer 1.x install into Rig. 1.x updates itself by downloading "PCOptimizer.exe" (which is now
/// Rig) into its old folder; on that first start this moves the program to its new folder and renames the
/// Start menu and desktop shortcuts, the "Installed apps" entry, start-with-Windows and the maintenance task.
/// Settings and history were already copied to %AppData%\Rig. If any step fails, Rig keeps running from where it is.
/// </summary>
public static class Migration
{
    public const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PCOptimizer";
    public const string OldRunValue = "PCOptimizer";
    public const string OldTaskName = "PC Optimizer Maintenance";
    const string OldShortcut = "PC Optimizer.lnk";

    public static string OldInstallDir => Path.Combine(Places.LocalAppData, "Programs", "PC Optimizer");
    public static string OldExe => Path.Combine(OldInstallDir, "PCOptimizer.exe");
    static string OldStartMenuLink => Path.Combine(Places.StartMenuPrograms, OldShortcut);
    static string OldDesktopLink => Path.Combine(Places.Desktop, OldShortcut);

    /// <summary>Set for the first run after moving, so the app can say what happened.</summary>
    public static bool JustMigrated { get; set; }

    /// <summary>Returns true if Rig was relaunched from its new folder and this copy should exit.</summary>
    public static bool Run(AppSettings settings, string[] args)
    {
        JustMigrated = args.Contains("--migrated", StringComparer.OrdinalIgnoreCase);
        try { MoveStartWithWindows(); } catch { }

        bool runningOld = string.Equals(Environment.ProcessPath, OldExe, StringComparison.OrdinalIgnoreCase);
        bool registeredOld;
        using (var k = Places.OpenUserKey(OldUninstallKey)) registeredOld = k is not null;
        if (!runningOld && !registeredOld) return false;

        try
        {
            // The program itself.
            Directory.CreateDirectory(Installer.InstallDir);
            if (!string.Equals(Environment.ProcessPath, Installer.InstalledExe, StringComparison.OrdinalIgnoreCase))
                File.Copy(Environment.ProcessPath!, Installer.InstalledExe, overwrite: true);

            // Shortcuts: same places as before, new name.
            bool desktop = File.Exists(OldDesktopLink);
            foreach (var link in new[] { OldStartMenuLink, OldDesktopLink }) try { File.Delete(link); } catch { }
            Installer.CreateShortcuts(desktop);

            // Settings → Apps entry.
            Places.DeleteUserKey(OldUninstallKey);
            Installer.Register();

            // Start with Windows now points at the new copy.
            if (AutoStart.IsEnabled) AutoStart.Set(true, Installer.InstalledExe);

            // Maintenance night: replace the old task with one that starts Rig.
            Maintenance.DeleteTask(OldTaskName);
            if (settings.MaintenanceEnabled) Task.Run(() => Maintenance.ScheduleAsync(settings)).Wait(TimeSpan.FromSeconds(20));
        }
        catch { return false; }

        if (!runningOld) { RemoveOldFolderLater(); return false; }

        // Start the new copy and let it tidy away the old folder once we've exited.
        App.ReleaseInstanceLock();
        Process.Start(new ProcessStartInfo(Installer.InstalledExe, "--migrated") { UseShellExecute = true, WorkingDirectory = Installer.InstallDir });
        RemoveOldFolderLater();
        return true;
    }

    /// <summary>1.x's "start with Windows" entry (Run value "PCOptimizer") becomes Rig's.</summary>
    static void MoveStartWithWindows()
    {
        using var run = Places.OpenUserKey(AutoStart.RunKey, write: true);
        if (run?.GetValue(OldRunValue) is not string old) return;
        run.DeleteValue(OldRunValue, throwOnMissingValue: false);
        bool wasInstalled = old.Contains(OldExe, StringComparison.OrdinalIgnoreCase);
        AutoStart.Set(true, wasInstalled ? Installer.InstalledExe : Environment.ProcessPath);
    }

    static void RemoveOldFolderLater()
    {
        if (!Directory.Exists(OldInstallDir)) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 4 /nobreak >nul & rmdir /s /q \"{OldInstallDir}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
        });
    }

    /// <summary>Anything 1.x left behind, for the uninstaller.</summary>
    public static void RemoveLeftovers()
    {
        try { Places.DeleteUserKey(OldUninstallKey); } catch { }
        foreach (var link in new[] { OldStartMenuLink, OldDesktopLink }) try { File.Delete(link); } catch { }
        try { using var run = Places.OpenUserKey(AutoStart.RunKey, write: true); run?.DeleteValue(OldRunValue, throwOnMissingValue: false); } catch { }
        try { Maintenance.DeleteTask(OldTaskName); } catch { }
    }
}
