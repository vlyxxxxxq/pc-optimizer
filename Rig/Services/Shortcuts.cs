using System.Reflection;

namespace Rig.Services;

public sealed record ShortcutDefinition(string Property, string Title, string Module);

public static class Shortcuts
{
    public static readonly ShortcutDefinition[] All =
    [
        new(nameof(AppSettings.PaletteHotkey), "Command palette", Modules.Home),
        new(nameof(AppSettings.ClipboardHotkey), "Clipboard history", "Clipboard"),
        new(nameof(AppSettings.ClipHotkey), "Save replay clip", "Clips"),
        new(nameof(AppSettings.ScreenshotHotkey), "Take screenshot", "Clips"),
        new(nameof(AppSettings.RecordHotkey), "Start / stop recording", "Clips"),
        new(nameof(AppSettings.ReplayHotkey), "Turn replay buffer on / off", "Clips"),
        new(nameof(AppSettings.CrosshairHotkey), "Show / hide crosshair", "Crosshair"),
        new(nameof(AppSettings.ColorHotkey), "Pick a colour", "ColorPicker"),
    ];
    static PropertyInfo Property(string name) => typeof(AppSettings).GetProperty(name) ?? throw new ArgumentException("Unknown shortcut");
    public static string Get(AppSettings settings, string name) => (string)Property(name).GetValue(settings)!;
    public static void Set(AppSettings settings, string name, string value) => Property(name).SetValue(settings, value);
    public static string? Conflict(AppSettings settings, string property, Hotkey proposed) =>
        All.FirstOrDefault(s => s.Property != property && Hotkey.Parse(Get(settings, s.Property)) == proposed)?.Title;
    public static void Normalize(AppSettings settings)
    {
        var defaults = new AppSettings();
        var used = new HashSet<Hotkey>();
        foreach (var item in All)
        {
            if (Get(settings, item.Property) == "") continue;
            var key = Hotkey.Parse(Get(settings, item.Property));
            if (key is not { IsUsable: true } || used.Contains(key.Value)) key = Hotkey.Parse(Get(defaults, item.Property));
            // A customized earlier entry can occupy a later default. Keep the preference but leave that action unbound.
            if (key is not null && used.Add(key.Value)) Set(settings, item.Property, key.Value.ToString());
            else Set(settings, item.Property, "");
        }
    }
}
