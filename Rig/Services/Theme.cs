using System.Windows;
using System.Windows.Media;
namespace Rig.Services;
/// <summary>
/// Dark or light colours plus an accent, written into the app's resources before any window opens.
/// Named resources update in place so existing pages follow changes immediately.
/// </summary>
public static class Theme
{
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Blue", "#4F8CFF"), ("Purple", "#9B6BFF"), ("Teal", "#22B8C8"), ("Green", "#2FBF7A"), ("Orange", "#FF8A3D"), ("Pink", "#F2559A"),
        ("Cyan", "#20C7E8"), ("Red", "#E95565"), ("Gold", "#E5B84A"), ("Mint", "#64D9AF"), ("Indigo", "#7785F5"), ("Slate", "#8799B8"),
    ];
    static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["Bg"] = ("#0F1117", "#F3F4F7"),
        ["Sidebar"] = ("#151821", "#E8EAEF"),
        ["Card"] = ("#1B1F2A", "#FFFFFF"),
        ["CardBorder"] = ("#262B38", "#DDE1E8"),
        ["Input"] = ("#11141B", "#F7F8FA"),
        ["Text"] = ("#E6E8EE", "#1A1D24"),
        ["Muted"] = ("#8A90A2", "#5D6474"),
        ["Good"] = ("#3DD68C", "#17935A"),
        ["Warn"] = ("#F5A524", "#B87400"),
        ["Danger"] = ("#F25F5C", "#D33A37"),
        ["Control"] = ("#262B38", "#E6E9EF"),
        ["ControlBorder"] = ("#343B4F", "#CBD0DA"),
        ["Subtle"] = ("#222736", "#EEF0F4"),
        ["StrongBorder"] = ("#4A5166", "#B3BAC8"),
        ["HoverBorder"] = ("#6A7390", "#8C95A8"),
        ["DisabledText"] = ("#5E667A", "#9AA1AF"),
        ["NavHover"] = ("#1C2130", "#DDE0E8"),
        ["Track"] = ("#2A3040", "#D3D8E1"),
        ["Knob"] = ("#A9AFBF", "#FFFFFF"),
        ["Thumb"] = ("#363C4D", "#C2C7D1"),
        ["ThumbPressed"] = ("#5C6480", "#959CAB"),
        ["AltRow"] = ("#161A23", "#F7F8FA"),
        ["GridLine"] = ("#232836", "#E8EAEF"),
        ["GridLineStrong"] = ("#2C3242", "#D5D9E2"),
        ["Deep"] = ("#0B0D12", "#E2E5EC"),
        ["GoodTint"] = ("#1E3A2E", "#DCF2E6"),
        ["WarnTint"] = ("#2B2617", "#FAEFD7"),
        ["DangerTint"] = ("#3A1E1E", "#FAE1E0"),
        ["InfoTint"] = ("#1B2433", "#E2EAF9"),
    };
    public static event Action? Changed;
    public static readonly string[] Modes = ["Dark", "Light", "System", "Midnight", "Graphite", "OLED"];
    public static bool ResolveDark(string mode) => mode != "Light" && (mode != "System" || Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is not 1);
    public static void Apply(ResourceDictionary resources, AppSettings settings)
    {
        Apply(resources, ResolveDark(settings.Theme), settings.Accent);
        if (!SystemParameters.HighContrast)
        {
            string? surface = settings.Theme switch { "Midnight" => "#101828", "Graphite" => "#202020", "OLED" => "#000000", _ => null };
            if (surface is not null)
            {
                var basis = Parse(surface);
                foreach (var (key, amount) in new[] { ("Bg", 0.0), ("Sidebar", .025), ("Card", .055), ("Input", .015), ("Control", .10), ("Subtle", .08), ("Deep", 0.0) })
                    Set(resources, key, Mix(basis, Colors.White, amount));
            }
            if (!settings.ThemeGlow)
                foreach (var name in new[] { "AccentGlowEffect", "NavGlowEffect" })
                    if (resources[name] is System.Windows.Media.Effects.DropShadowEffect effect) effect.Opacity = 0;
        }
        resources["CardCornerRadius"] = new CornerRadius(settings.RoundCards ? 12 : 3);
        Changed?.Invoke();
    }
    public static bool IsDark { get; private set; } = true;
    public static void Apply(ResourceDictionary resources, bool dark, string? accentHex)
    {
        IsDark = dark;
        foreach (var (key, (d, l)) in Palette) Set(resources, key, Parse(dark ? d : l));
        if (SystemParameters.HighContrast) { ApplyHighContrast(resources); return; }
        var accent = Parse(accentHex ?? Accents[0].Hex);
        // On light backgrounds the accent is darkened a little so text and icons in it stay readable.
        var ink = dark ? accent : Mix(accent, Colors.Black, 0.15);
        Set(resources, "Accent", ink);
        Set(resources, "AccentLight", dark ? Mix(accent, Colors.White, 0.3) : Mix(accent, Colors.Black, 0.1));
        Set(resources, "PrimaryText", (accent.R * .2126 + accent.G * .7152 + accent.B * .0722) > 155 ? Parse("#11141B") : Colors.White);
        Set(resources, "Selected", dark ? Mix(Parse("#1B1F2A"), accent, 0.22) : Mix(Colors.White, accent, 0.16));
        resources["AccentColor"] = accent;
        resources["PrimaryStartColor"] = Mix(accent, Colors.White, 0.18);
        resources["PrimaryEndColor"] = Mix(accent, Colors.Black, 0.15);
        resources["NavSelectedStartColor"] = dark ? Mix(Parse("#151821"), accent, 0.25) : Mix(Parse("#E8EAEF"), accent, 0.2);
        resources["NavSelectedEndColor"] = dark ? Mix(Parse("#151821"), accent, 0.08) : Mix(Parse("#E8EAEF"), accent, 0.06);
        resources["TitleStartColor"] = dark ? Colors.White : Parse("#1A1D24");
        resources["TitleEndColor"] = dark ? Mix(accent, Colors.White, 0.45) : ink;
        resources["AccentGlowColor"] = Color.FromArgb(0x1A, accent.R, accent.G, accent.B);
        resources["AccentGlowClearColor"] = Color.FromArgb(0, accent.R, accent.G, accent.B);
        // Covers a Home card while customizing.
        var veilBase = Parse(dark ? "#0F1117" : "#F3F4F7");
        resources["EditVeilBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, veilBase.R, veilBase.G, veilBase.B)));
        // Gradients and glows used by the shared styles.
        resources["TitleBrush"] = Frozen(new LinearGradientBrush((Color)resources["TitleStartColor"], (Color)resources["TitleEndColor"], 0));
        resources["PrimaryBrush"] = Frozen(new LinearGradientBrush((Color)resources["PrimaryStartColor"], (Color)resources["PrimaryEndColor"], 45));
        resources["NavSelectedBrush"] = Frozen(new LinearGradientBrush((Color)resources["NavSelectedStartColor"], (Color)resources["NavSelectedEndColor"], 0));
        resources["AccentGlowBrush"] = Frozen(new RadialGradientBrush((Color)resources["AccentGlowColor"], (Color)resources["AccentGlowClearColor"])
            { Center = new Point(1, 0), GradientOrigin = new Point(1, 0), RadiusX = 0.7, RadiusY = 0.6 });
        resources["AccentGlowEffect"] = Frozen(new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 18, ShadowDepth = 0, Opacity = dark ? 0.55 : 0.35 });
        resources["NavGlowEffect"] = Frozen(new System.Windows.Media.Effects.DropShadowEffect { Color = accent, BlurRadius = 10, ShadowDepth = 0, Opacity = dark ? 0.7 : 0.3 });
    }
    static T Frozen<T>(T f) where T : Freezable => f;
    /// <summary>Windows' contrast theme: every surface, text and border uses the system's own colours, and no gradients or glows.</summary>
    static void ApplyHighContrast(ResourceDictionary r)
    {
        IsDark = SystemColors.WindowColor.R + SystemColors.WindowColor.G + SystemColors.WindowColor.B < 384;
        Color window = SystemColors.WindowColor, text = SystemColors.WindowTextColor, gray = SystemColors.GrayTextColor,
              hilite = SystemColors.HighlightColor, hiliteText = SystemColors.HighlightTextColor, control = SystemColors.ControlColor;
        foreach (var key in Palette.Keys) Set(r, key, window);
        foreach (var key in new[] { "Text", "CardBorder", "ControlBorder", "StrongBorder", "HoverBorder", "GridLineStrong", "Knob" }) Set(r, key, text);
        foreach (var key in new[] { "Muted", "DisabledText", "GridLine" }) Set(r, key, gray);
        foreach (var key in new[] { "Control", "Subtle", "Track", "Thumb", "NavHover" }) Set(r, key, control);
        foreach (var key in new[] { "Accent", "AccentLight", "Selected", "Good", "Warn", "Danger" }) Set(r, key, hilite);
        Set(r, "PrimaryText", hiliteText);
        r["AccentColor"] = hilite;
        r["TitleBrush"] = Frozen(new SolidColorBrush(text));
        r["PrimaryBrush"] = Frozen(new SolidColorBrush(hilite));
        r["NavSelectedBrush"] = Frozen(new SolidColorBrush(hilite));
        r["AccentGlowBrush"] = Frozen(new SolidColorBrush(Colors.Transparent));
        r["EditVeilBrush"] = Frozen(new SolidColorBrush(window));
        r["AccentGlowEffect"] = Frozen(new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0 });
        r["NavGlowEffect"] = Frozen(new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0 });
        _ = hiliteText;
    }
    static void Set(ResourceDictionary resources, string key, Color c)
    {
        if (resources[key + "Brush"] is SolidColorBrush old && !old.IsFrozen) old.Color = c;
        else resources[key + "Brush"] = new SolidColorBrush(c);
    }
    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    /// <summary>Caption bar colours for DWM (COLORREF is 0x00BBGGRR).</summary>
    public static (int Caption, int Border) WindowFrame =>
        IsDark ? (0x00211815, 0x00382B26) : (0x00EFEAE8, 0x00E8E1DD);
}
