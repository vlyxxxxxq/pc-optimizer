namespace Rig;

/// <summary>One tool in the app: a sidebar entry and its page. Core modules can't be switched off.</summary>
public sealed record ModuleInfo(string Id, string Title, string Glyph, string Category, string Description, bool Core = false)
{
    /// <summary>Automation id and pin key, kept from 1.x ("NavGame") so pins and tests carry over.</summary>
    public string NavName => "Nav" + Id;
}

public static class Modules
{
    public const string Home = "Dashboard", Settings = "Settings";

    public static readonly string[] Categories = ["Gaming", "Media", "Productivity", "Files", "System"];

    public static readonly ModuleInfo[] All =
    [
        new(Home, "Home", "", "", "Your cards: pinned tools, PC health and more", Core: true),

        new("Game", "Game Mode", "", "Gaming", "Boosts your PC while you play", Core: true),
        new("Library", "Game Library", "", "Gaming", "Installed games, news and free games"),
        new("Crosshair", "Crosshair", "", "Gaming", "A custom crosshair over your games"),
        new("Controller", "Controller Tester", "", "Gaming", "Buttons, sticks, drift and rumble"),
        new("Input", "Input Stats", "", "Gaming", "Keys, clicks and APM per game"),

        new("Photos", "Similar Photos", "\uE91B", "Media", "Find and clear out near-duplicate photos"),
        new("ColorPicker", "Color Picker & Ruler", "\uE790", "Media", "Pick any colour on screen, measure in pixels"),
        new("Clips", "Clips & Recording", "", "Media", "Clips, screenshots and screen recording"),

        new("Clipboard", "Clipboard & Notes", "", "Productivity", "Clipboard history and quick notes"),
        new("ScreenTime", "Screen Time", "", "Productivity", "Time per app and break reminders"),
        new("Layouts", "Window Layouts", "", "Productivity", "Save and restore window arrangements"),
        new("Timers", "Timers & Reminders", "\uE823", "Productivity", "Countdowns, reminders and a stopwatch"),

        new("Downloads", "Downloads", "", "Files", "Sort the Downloads folder"),
        new("Disk", "Disk Space", "", "Files", "What's using your drives"),
        new("Cleaner", "Junk Cleaner", "", "Files", "Temp files, caches and automatic cleaning"),
        new("Renamer", "Batch Rename", "\uE8AC", "Files", "Rename many files at once"),
        new("Uninstall", "Uninstaller", "", "Files", "Remove programs in bulk"),

        new("Startup", "Startup Apps", "", "System", "What launches at sign-in"),
        new("Processes", "Processes", "", "System", "What's running right now"),
        new("Tweaks", "Tweaks", "", "System", "Reversible Windows tweaks"),
        new("Menu", "Right-click Menu", "", "System", "Clean up Explorer's context menu"),
        new("Apps", "Apps", "", "System", "Install and update apps with winget"),
        new("Hardware", "Hardware", "", "System", "Specs, drive health and benchmarks"),
        new("Network", "Network", "", "System", "Ping, DNS and speed tests"),
        new("Security", "Security", "", "System", "Windows Defender status and scans"),
        new("Tools", "Tools", "", "System", "Drivers, repairs, power timer and more"),

        new(Settings, "Settings", "", "", "App settings", Core: true),
    ];

    public static ModuleInfo Get(string id) => All.First(m => m.Id == id);
}
