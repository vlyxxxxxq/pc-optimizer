namespace Rig.Services;

/// <summary>The choices made in the first-run wizard, kept separate from saved settings until setup finishes.</summary>
public sealed record SetupOptions(bool Install, bool DesktopShortcut, bool StartWithWindows,
    bool AutomaticGameMode, bool ClipboardHistory, bool ScreenTime, bool Notifications);

public sealed record SetupProgress(string Message, double Fraction);
public sealed record SetupResult(bool Installed, string? Error = null)
{
    public bool Succeeded => Error is null;
}

internal interface ISetupActions
{
    Task InstallAsync(bool desktopShortcut, IProgress<SetupProgress>? progress);
    void SetStartup(bool enabled, bool installed);
    bool Save(AppSettings settings);
}

internal sealed class WindowsSetupActions : ISetupActions
{
    public Task InstallAsync(bool desktopShortcut, IProgress<SetupProgress>? progress) =>
        Installer.InstallAsync(desktopShortcut, progress);

    public void SetStartup(bool enabled, bool installed) =>
        AutoStart.Set(enabled, installed ? Installer.InstalledExe : null);

    public bool Save(AppSettings settings) => settings.Save();
}

/// <summary>Does not report completion, or allow a relaunch, when installation, startup, or saving failed.</summary>
internal static class Setup
{
    public static async Task<SetupResult> ApplyAsync(AppSettings settings, SetupOptions options,
        bool alreadyInstalled, ISetupActions actions, IProgress<SetupProgress>? progress = null)
    {
        bool installed = alreadyInstalled;
        string stage = "install Rig";
        try
        {
            if (options.Install && !alreadyInstalled)
            {
                await actions.InstallAsync(options.DesktopShortcut, progress);
                installed = true;
            }

            stage = "set up sign-in startup";
            progress?.Report(new("Setting your startup preference…", 0.88));
            actions.SetStartup(options.StartWithWindows, installed);

            stage = "save your preferences";
            progress?.Report(new("Saving your choices…", 0.95));
            settings.AutoGameMode = options.AutomaticGameMode;
            settings.ClipboardHistoryEnabled = options.ClipboardHistory;
            settings.ScreenTimeEnabled = options.ScreenTime;
            settings.Notifications = options.Notifications;
            if (!actions.Save(settings))
                return new(installed, "Rig couldn't save your preferences. Check that your settings folder is writable, then try again.");

            progress?.Report(new("Your setup is ready.", 1));
            return new(installed);
        }
        catch (Exception ex)
        {
            return new(installed, $"Rig couldn't {stage}. {ex.Message}");
        }
    }
}
