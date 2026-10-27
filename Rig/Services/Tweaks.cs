using System.Globalization;
using Microsoft.Win32;
using static Microsoft.Win32.RegistryHive;

namespace Rig.Services;

/// <summary>
/// One registry value a tweak controls. On is the tweaked value; Off is the Windows default
/// (null deletes the value). Absent is what Windows assumes when the value doesn't exist.
/// </summary>
public sealed record RegValue(RegistryHive Hive, string Key, string Name, object On, object? Off,
    object? Absent = null, RegistryValueKind Kind = RegistryValueKind.DWord);

public sealed class Tweak : ObservableObject
{
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool RequiresAdmin { get; init; }
    /// <summary>"Restart Explorer", "Sign out" or "Restart PC" when the change isn't instant.</summary>
    public string? Note { get; init; }
    public bool Recommended { get; init; }
    public required Func<bool> Read { get; init; }
    public required Action Apply { get; init; }
    public required Action Revert { get; init; }

    public bool CanChange => !RequiresAdmin || Elevation.IsElevated;
    public bool HasNote => Note is not null;

    public static event Action<Tweak, Exception>? Failed;
    public static event Action<Tweak>? Changed;

    bool _isOn;
    /// <summary>Bound two-way to the switch: setting it applies or reverts, then re-reads the real state.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn) return;
            try
            {
                if (value) Apply(); else Revert();
                Changed?.Invoke(this);
            }
            catch (Exception ex) { Failed?.Invoke(this, ex); }
            Refresh();
            OnPropertyChanged();
        }
    }

    public void Refresh()
    {
        bool state;
        try { state = Read(); } catch { state = false; }
        Set(ref _isOn, state, nameof(IsOn));
    }
}

public sealed record TweakGroup(string Name, string Glyph, List<Tweak> Tweaks);

public static class Tweaks
{
    const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    const string Multimedia = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string ClassicMenu = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public static List<TweakGroup> All() =>
    [
        new("Gaming & performance", "",
        [
            Reg("Turn off Xbox Game Bar background recording",
                "Stops Game DVR from constantly recording gameplay in the background. The Game Bar overlay still works.",
                recommended: true, note: null,
                V(CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1, Absent: 1),
                V(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1, Absent: 1)),
            Reg("Windows Game Mode",
                "Lets Windows prioritise the game you're playing and pause Windows Update installs during play.",
                recommended: true, note: null,
                V(CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, null, Absent: 1)),
            Reg("Hardware-accelerated GPU scheduling",
                "Lets your graphics card manage its own memory. Can lower latency on modern GPUs.",
                recommended: false, note: "Restart PC",
                V(LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1, Absent: 1)),
            Reg("Prioritise games in the system scheduler",
                "Gives the multimedia scheduler's \"Games\" task high CPU and I/O priority and reserves less CPU for background work.",
                recommended: true, note: "Restart PC",
                V(LocalMachine, Multimedia, "SystemResponsiveness", 10, 20, Absent: 20),
                V(LocalMachine, Multimedia + @"\Tasks\Games", "Priority", 6, 2, Absent: 2),
                V(LocalMachine, Multimedia + @"\Tasks\Games", "Scheduling Category", "High", "Medium", Absent: "Medium", Kind: RegistryValueKind.String),
                V(LocalMachine, Multimedia + @"\Tasks\Games", "SFIO Priority", "High", "Normal", Absent: "Normal", Kind: RegistryValueKind.String)),
            Reg("Turn off network throttling",
                "Removes the packet-rate limit Windows applies while media is playing.",
                recommended: false, note: "Restart PC",
                V(LocalMachine, Multimedia, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10, Absent: 10)),
            WithAfter(Reg("Turn off mouse acceleration",
                "Raw 1:1 mouse movement (\"Enhance pointer precision\" off). Most gamers prefer this for consistent aim.",
                recommended: true, note: null,
                V(CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "0", "1", Absent: "1", Kind: RegistryValueKind.String),
                V(CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "0", "6", Absent: "6", Kind: RegistryValueKind.String),
                V(CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "0", "10", Absent: "10", Kind: RegistryValueKind.String)),
                ApplyMouseSettings),
            Reg("Turn off the Sticky Keys shortcut",
                "No more Sticky Keys pop-up when you tap Shift five times mid-game.",
                recommended: true, note: null,
                V(CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510", Absent: "510", Kind: RegistryValueKind.String)),
            Reg("Turn off power throttling",
                "Stops Windows from slowing down background apps. Better for desktops; costs battery life on laptops.",
                recommended: false, note: "Restart PC",
                V(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, null)),
            Reg("Turn off background apps",
                "Stops Store apps from running in the background when you're not using them.",
                recommended: false, note: null,
                V(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0, Absent: 0)),
            new Tweak
            {
                Category = "Gaming & performance",
                Name = "Turn off hibernation",
                Description = "Deletes hiberfil.sys and frees several GB of disk space. Fast Startup is turned off too.",
                RequiresAdmin = true,
                Read = () => ReadValue(LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") is 0,
                Apply = () => RunChecked("powercfg", "/hibernate off"),
                Revert = () => RunChecked("powercfg", "/hibernate on"),
            },
        ]),

        new("Privacy", "",
        [
            Reg("Turn off telemetry",
                "Sends Microsoft the minimum diagnostic data your edition allows.",
                recommended: true, note: null,
                V(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null)),
            Reg("Turn off advertising ID",
                "Apps can't use your advertising ID for personalised ads.",
                recommended: true, note: null,
                V(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1, Absent: 1)),
            Reg("Turn off tailored experiences",
                "Stops Microsoft using your diagnostic data for tips, ads and recommendations.",
                recommended: true, note: null,
                V(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1, Absent: 1)),
            Reg("Turn off activity history",
                "Windows stops collecting and uploading the apps and files you use.",
                recommended: true, note: null,
                V(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),
                V(LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null)),
            Reg("Turn off location tracking",
                "Blocks apps from using your location.",
                recommended: false, note: null,
                V(LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location",
                    "Value", "Deny", "Allow", Absent: "Allow", Kind: RegistryValueKind.String)),
            Reg("Remove ads and suggestions",
                "No suggested apps in Start, tips, \"Get even more out of Windows\" or silently installed promo apps.",
                recommended: true, note: null,
                V(CurrentUser, Cdm, "SubscribedContent-338388Enabled", 0, 1, Absent: 1),
                V(CurrentUser, Cdm, "SubscribedContent-338389Enabled", 0, 1, Absent: 1),
                V(CurrentUser, Cdm, "SubscribedContent-353694Enabled", 0, 1, Absent: 1),
                V(CurrentUser, Cdm, "SubscribedContent-353696Enabled", 0, 1, Absent: 1),
                V(CurrentUser, Cdm, "SystemPaneSuggestionsEnabled", 0, 1, Absent: 1),
                V(CurrentUser, Cdm, "SilentInstalledAppsEnabled", 0, 1, Absent: 1)),
            Reg("Turn off Bing results in Start search",
                "Start searches only your PC instead of also sending what you type to Bing.",
                recommended: true, note: "Restart Explorer",
                V(CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)),
            Reg("Turn off Copilot",
                "Hides Windows Copilot through policy. Newer standalone Copilot app versions may ignore this.",
                recommended: false, note: "Restart Explorer",
                V(CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null)),
        ]),

        new("Windows & Explorer", "",
        [
            Reg("Show file extensions",
                "See .exe, .zip and so on, which also makes disguised malware easier to spot.",
                recommended: true, note: "Restart Explorer",
                V(CurrentUser, Explorer, "HideFileExt", 0, 1, Absent: 1)),
            Reg("Show hidden files",
                "Show hidden files and folders in File Explorer.",
                recommended: false, note: "Restart Explorer",
                V(CurrentUser, Explorer, "Hidden", 1, 2, Absent: 2)),
            new Tweak
            {
                Category = "Windows & Explorer",
                Name = "Classic right-click menu",
                Description = "Brings back the full Windows 10 context menu instead of \"Show more options\".",
                Note = "Restart Explorer",
                Read = () => KeyExists(CurrentUser, ClassicMenu + @"\InprocServer32"),
                Apply = () =>
                {
                    using var key = Registry.CurrentUser.CreateSubKey(ClassicMenu + @"\InprocServer32");
                    key.SetValue("", "", RegistryValueKind.String);
                },
                Revert = () => Registry.CurrentUser.DeleteSubKeyTree(ClassicMenu, throwOnMissingSubKey: false),
            },
            Reg("\"End task\" in the taskbar menu",
                "Right-click any app on the taskbar to force-close it, no Task Manager needed.",
                recommended: true, note: null,
                V(CurrentUser, Explorer + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, null)),
            Reg("Dark mode",
                "Dark theme for Windows and apps.",
                recommended: false, note: null,
                V(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0, 1, Absent: 1),
                V(CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0, 1, Absent: 1)),
            Reg("Left-aligned taskbar",
                "Put the Start button back in the corner.",
                recommended: false, note: null,
                V(CurrentUser, Explorer, "TaskbarAl", 0, 1, Absent: 1)),
        ]),
    ];

    static RegValue V(RegistryHive hive, string key, string name, object on, object? off,
        object? Absent = null, RegistryValueKind Kind = RegistryValueKind.DWord) => new(hive, key, name, on, off, Absent, Kind);

    static Tweak Reg(string name, string description, bool recommended, string? note, params RegValue[] values) => new()
    {
        Category = "",
        Name = name,
        Description = description,
        Recommended = recommended,
        Note = note,
        RequiresAdmin = values.Any(v => v.Hive == LocalMachine),
        Read = () => values.All(v => Same(ReadValue(v.Hive, v.Key, v.Name) ?? v.Absent, v.On)),
        Apply = () => { foreach (var v in values) Write(v, v.On); },
        Revert = () => { foreach (var v in values) Write(v, v.Off); },
    };

    static Tweak WithAfter(Tweak t, Action after) => new()
    {
        Category = t.Category, Name = t.Name, Description = t.Description, Recommended = t.Recommended, Note = t.Note,
        RequiresAdmin = t.RequiresAdmin, Read = t.Read,
        Apply = () => { t.Apply(); after(); },
        Revert = () => { t.Revert(); after(); },
    };

    static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    static object? ReadValue(RegistryHive hive, string key, string name)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(key);
        return k?.GetValue(name);
    }

    static bool KeyExists(RegistryHive hive, string key)
    {
        using var b = Base(hive);
        using var k = b.OpenSubKey(key);
        return k is not null;
    }

    static bool Same(object? a, object? b) =>
        a is not null && b is not null &&
        string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);

    static void Write(RegValue v, object? value)
    {
        using var b = Base(v.Hive);
        if (value is null)
        {
            using var existing = b.OpenSubKey(v.Key, writable: true);
            existing?.DeleteValue(v.Name, throwOnMissingValue: false);
        }
        else
        {
            using var k = b.CreateSubKey(v.Key, writable: true);
            k.SetValue(v.Name, value, v.Kind);
        }
    }

    /// <summary>Pushes the registry mouse values to the live session so no sign-out is needed.</summary>
    static void ApplyMouseSettings()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
        int Get(string n, int d) => int.TryParse(k?.GetValue(n) as string, out var v) ? v : d;
        int[] mouse = [Get("MouseThreshold1", 6), Get("MouseThreshold2", 10), Get("MouseSpeed", 1)];
        Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, mouse, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
    }

    static void RunChecked(string file, string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(output.Trim());
    }

    /// <summary>Restore point via PowerShell; needs admin and System Protection enabled on C:.</summary>
    public static Task<(int ExitCode, string Output)> CreateRestorePointAsync() =>
        Shell.RunAsync("powershell", "-NoProfile -Command \"Checkpoint-Computer -Description 'Rig' -RestorePointType MODIFY_SETTINGS\"");
}
