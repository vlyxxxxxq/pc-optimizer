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
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PCOptimizer";
    const string ShortcutName = "Rig.lnk";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Rig");
    public static string InstalledExe => Path.Combine(InstallDir, "PCOptimizer.exe");
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName);
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    /// <summary>True when this copy is the installed one.</summary>
    public static bool IsRunningInstalled =>
        string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    public static void Install(bool desktopShortcut)
    {
        Directory.CreateDirectory(InstallDir);
        if (!IsRunningInstalled) File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);

        CreateShortcut(StartMenuShortcut, InstalledExe);
        if (desktopShortcut) CreateShortcut(DesktopShortcut, InstalledExe);
        Register();
        if (AutoStart.IsEnabled) AutoStart.Set(true, InstalledExe); // point "start with Windows" at the installed copy
    }

    /// <summary>Writes the Settings → Apps entry (also refreshes the version after an update).</summary>
    public static void Register()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Rig");
        k.SetValue("DisplayVersion", Updater.CurrentVersion.ToString(3));
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
        link.Description = "Rig: clean up, tweak and boost your PC";
        link.Save();
    }

    /// <summary>Runs from Settings → Apps → Uninstall (the "--uninstall" command line).</summary>
    public static void RunUninstall()
    {
        if (MessageBox.Show("Uninstall Rig?\n\nGame Mode will stop and anything it changed is put back.",
                "Uninstall Rig", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        bool wipe = MessageBox.Show("Also delete your settings, play history and benchmark results?",
            "Uninstall Rig", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // Close other running copies so their files can be removed (they restore Game Mode changes on exit).
        foreach (var p in Process.GetProcessesByName("PCOptimizer").Where(p => p.Id != Environment.ProcessId))
        {
            try { p.CloseMainWindow(); if (!p.WaitForExit(4000)) p.Kill(); } catch { }
            finally { p.Dispose(); }
        }

        try { AutoStart.Set(false); } catch { }
        try { Maintenance.Unschedule(); } catch { }
        foreach (var link in new[] { StartMenuShortcut, DesktopShortcut }) try { File.Delete(link); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
        if (wipe)
            try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCOptimizer"), true); } catch { }

        // This EXE can't delete itself while running: a hidden cmd removes the folder a moment after we exit.
        if (Directory.Exists(InstallDir))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            });
        MessageBox.Show("Rig has been uninstalled.", "Uninstall Rig", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
