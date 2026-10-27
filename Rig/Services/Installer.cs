using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Rig.Services;

/// <summary>
/// Per-user install with no setup program and no admin: copies the EXE to
/// %LocalAppData%\Programs\Rig, adds Start menu (and optional desktop) shortcuts, and
/// registers in Settings → Apps with "--uninstall" as the uninstall command.
/// </summary>
public static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Rig";
    const string ShortcutName = "Rig.lnk";

    public static string InstallDir => Path.Combine(Places.LocalAppData, "Programs", "Rig");
    public static string InstalledExe => Path.Combine(InstallDir, "Rig.exe");
    static string StartMenuShortcut => Path.Combine(Places.StartMenuPrograms, ShortcutName);
    static string DesktopShortcut => Path.Combine(Places.Desktop, ShortcutName);

    /// <summary>True when this copy is the installed one.</summary>
    public static bool IsRunningInstalled =>
        string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    public static void Install(bool desktopShortcut)
    {
        Directory.CreateDirectory(InstallDir);
        if (!IsRunningInstalled) File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);

        CreateShortcuts(desktopShortcut);
        Register();
        if (AutoStart.IsEnabled) AutoStart.Set(true, InstalledExe); // point "start with Windows" at the installed copy
    }

    public static void CreateShortcuts(bool desktop)
    {
        CreateShortcut(StartMenuShortcut, InstalledExe);
        if (desktop) CreateShortcut(DesktopShortcut, InstalledExe);
    }

    /// <summary>Writes the Settings → Apps entry (also refreshes the version after an update).</summary>
    public static void Register()
    {
        using var k = Places.OpenUserKey(UninstallKey, write: true)!;
        k.SetValue("DisplayName", "Rig");
        k.SetValue("DisplayVersion", Updater.CurrentVersion.ToString());
        k.SetValue("Publisher", "Rig");
        k.SetValue("DisplayIcon", $"{InstalledExe},0");
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("URLInfoAbout", Updater.ReleasesPage.Replace("/releases", ""));
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); } catch { }
    }

    /// <summary>Keeps the Settings → Apps version current when the installed copy auto-updates.</summary>
    public static void RefreshRegistration()
    {
        if (!IsRunningInstalled) return;
        try { Register(); } catch { }
    }

    static void CreateShortcut(string path, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = $"{target},0";
        link.Description = "Rig: your PC toolkit";
        link.Save();
    }

    static IEnumerable<Process> Others() =>
        Process.GetProcessesByName("Rig").Concat(Process.GetProcessesByName("PCOptimizer")).Where(p => p.Id != Environment.ProcessId);

    /// <summary>Signals running copies to quit cleanly and waits up to 20 seconds. True when none are left.</summary>
    static bool StopRunningCopies()
    {
        try { using var quit = EventWaitHandle.OpenExisting(App.QuitSignalName); quit.Set(); } catch { /* none running for this profile */ }
        var until = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < until)
        {
            var left = Others().ToList();
            bool any = left.Count > 0;
            foreach (var p in left) p.Dispose();
            if (!any) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>Runs from Settings → Apps → Uninstall (the "--uninstall" command line).</summary>
    public static void RunUninstall()
    {
        if (MessageBox.Show("Uninstall Rig?\n\nGame Mode will stop and anything it changed is put back.",
                "Uninstall Rig", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        bool wipe = MessageBox.Show("Also delete your settings, play history and benchmark results?",
            "Uninstall Rig", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // Ask the running copy to quit properly (it puts Game Mode's changes back and finishes recordings), then wait.
        if (!StopRunningCopies())
        {
            if (MessageBox.Show("Rig is still running and may be putting Game Mode's changes back. Close it anyway?\n\n" +
                                "Anything it hasn't restored yet stays changed.", "Uninstall Rig", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            foreach (var p in Others()) { try { p.Kill(); p.WaitForExit(3000); } catch { } finally { p.Dispose(); } }
        }
        // If Game Mode couldn't put something back, keep the record (and the data folder) so the next start can.
        if (AppSettings.Load().Recovery.Pending && wipe)
        {
            wipe = false;
            MessageBox.Show("Game Mode still has changes to put back (see Game Mode → Try restoring again), so your settings folder was kept.",
                "Uninstall Rig", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        try { AutoStart.Set(false); } catch { }
        try { Maintenance.Unschedule(); } catch { }
        foreach (var link in new[] { StartMenuShortcut, DesktopShortcut }) try { File.Delete(link); } catch { }
        try { Places.DeleteUserKey(UninstallKey); } catch { }
        Migration.RemoveLeftovers();
        if (wipe)
            foreach (var name in new[] { "Rig", "PCOptimizer" })
                try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name), true); } catch { }

        // This EXE can't delete itself while running: a hidden cmd removes the folder a moment after we exit.
        if (Directory.Exists(InstallDir))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            });
        MessageBox.Show("Rig has been uninstalled.", "Uninstall Rig", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
