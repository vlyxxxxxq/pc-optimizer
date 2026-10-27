using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rig.Services;

/// <summary>Exports actual WPF views with sample data. Never loads personal settings or starts app services.</summary>
internal static class PreviewExport
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (bool light in new[] { false, true })
        {
            Theme.Apply(Application.Current.Resources, !light, null);
            var settings = new AppSettings();
            var home = new Views.DashboardView(preview: true);
            home.Attach(settings, _ => true);
            home.SetGameMode(false, null, null, automatic: true);
            home.SetPreviewMetrics();
            home.ShowHealth(new HealthReport(95,
            [
                new(Severity.Warn, "Review your startup apps", "Choose which apps you need at sign-in.", "Review startup", "startup"),
                new(Severity.Good, "Plenty of free space", "Your system drive has room to work."),
                new(Severity.Good, "Temporary files checked", "No cleanup needed right now."),
            ]));
            foreach (var id in new[] { "Game", "Cleaner", "Timers", "Clipboard" })
            {
                var module = Modules.Get(id);
                home.PinnedPanel.Children.Add(Views.DashboardView.ToolButton(module, () => { }));
            }
            home.SetRecentTools(new[] { Modules.Get("Library"), Modules.Get("Disk"), Modules.Get("Timers") });
            Save(home, directory, light ? "home-light.png" : "home.png", 1040, 1000);
            if (light) continue;
            var game = new Views.GameModeView();
            settings.GameProfiles.Add(new GameProfile { Game = "Example game", MatchPath = @"C:\Games\Example", PowerPlan = true, Priority = true, KeepAwake = true });
            game.Attach(new GameModeService(settings), settings, preview: true);
            Save(game, directory, "game-mode.png", 1040, 950);
            var welcome = new WelcomeWindow(new AppSettings(), preview: true);
            // Keep the actual window's resource scope while rendering its content without desktop chrome or a pointer.
            welcome.Measure(new Size(920, 650));
            welcome.Arrange(new Rect(0, 0, 920, 650));
            welcome.UpdateLayout();
            if (welcome.Content is Panel panel) panel.Background = (Brush)Application.Current.FindResource("BgBrush");
            Save((FrameworkElement)welcome.Content, directory, "setup.png", 920, 650);
        }
        File.WriteAllText(Path.Combine(directory, "preview-data.txt"), "Interface previews rendered from Rig's WPF views with sample data. No personal profile or background services were loaded.");
        var previousError = Path.Combine(directory, "export-error.txt");
        if (File.Exists(previousError)) File.Delete(previousError);
    }

    static void Save(FrameworkElement view, string directory, string name, int width, int height)
    {
        FrameworkElement visual = view.Parent is null ? new Border
        {
            Width = width, Height = height, Background = (Brush)Application.Current.FindResource("BgBrush"), Child = view,
        } : view;
        visual.SetValue(TextElement.ForegroundProperty, Application.Current.FindResource("TextBrush"));
        visual.SetValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
    }
}
