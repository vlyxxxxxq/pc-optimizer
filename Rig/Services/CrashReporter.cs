using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Rig.Services;

/// <summary>
/// Catches anything that would otherwise silently kill the app: writes a log to
/// %AppData%\Rig\logs and shows a dialog with "Copy details" so friends can send it.
/// </summary>
public static class CrashReporter
{
    public static string LogFolder => Path.Combine(
        Environment.GetEnvironmentVariable("RIG_SETTINGS") is { Length: > 0 } s
            ? Path.GetDirectoryName(s)! // a test profile keeps its logs next to its settings
            : AppSettings.DataFolder,
        "logs");

    static bool _showing;

    public static void Install(Application app)
    {
        // UI-thread errors: keep the app alive after reporting (most are recoverable).
        app.DispatcherUnhandledException += (_, e) =>
        {
            Report(e.Exception, fatal: false);
            e.Handled = true;
        };
        // Background-thread errors end the process; log them so there's a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report(ex, fatal: true);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception); // fire-and-forget task faults: log only, don't interrupt
            e.SetObserved();
        };
    }

    /// <summary>Builds the report text: version, Windows build, and the full exception.</summary>
    public static string Details(Exception ex) => new StringBuilder()
        .AppendLine($"Rig {Updater.CurrentVersion.ToString(3)} ({(Updater.IsLite ? "lite" : "full")})")
        .AppendLine($"Windows {Environment.OSVersion.Version} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} · {(Elevation.IsElevated ? "admin" : "standard")}")
        .AppendLine($"Time {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
        .AppendLine()
        .Append(ex)
        .ToString();

    static string? Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            var path = Path.Combine(LogFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, Details(ex));
            // Keep the folder small.
            foreach (var old in new DirectoryInfo(LogFolder).GetFiles("crash-*.txt").OrderByDescending(f => f.Name).Skip(20))
                old.Delete();
            return path;
        }
        catch { return null; }
    }

    static void Report(Exception ex, bool fatal)
    {
        var log = Write(ex);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _showing) return;
        try
        {
            if (dispatcher.CheckAccess()) Show(ex, log, fatal);
            else dispatcher.Invoke(() => Show(ex, log, fatal));
        }
        catch { /* the UI itself is gone; the log file is all we can do */ }
    }

    static void Show(Exception ex, string? log, bool fatal)
    {
        _showing = true;
        var details = Details(ex);
        var brush = (string key) => (Brush)Application.Current.Resources[key];

        var text = new TextBox
        {
            Text = details, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"),
            FontSize = 11, Height = 180, Margin = new Thickness(0, 12, 0, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var copy = new Button { Content = "Copy details", Margin = new Thickness(0, 0, 8, 0) };
        var folder = new Button { Content = "Open log folder", Margin = new Thickness(0, 0, 8, 0) };
        var issue = new Button { Content = "Report on GitHub", Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = fatal ? "Close" : "Keep going", Style = (Style)Application.Current.Resources["PrimaryButton"] };

        var window = new Window
        {
            Title = "Rig hit a problem", Width = 620, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = brush("BgBrush"), Foreground = brush("TextBrush"),
            FontFamily = new FontFamily("Segoe UI"), Topmost = true,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Children =
                {
                    new TextBlock { Text = "Something went wrong", FontSize = 18, FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = (fatal ? "Rig has to close. " : "Rig can keep running, but the last action may not have finished. ") +
                               "If you send the details below to whoever shared the app, it helps fix it. They contain no personal files.",
                        TextWrapping = TextWrapping.Wrap, Foreground = brush("MutedBrush"), Margin = new Thickness(0, 6, 0, 0),
                    },
                    text,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy, folder, issue, close } },
                },
            },
        };
        copy.Click += (_, _) => { try { Clipboard.SetText(details); copy.Content = "Copied!"; } catch { } };
        folder.Click += (_, _) => Shell.Open(log is not null ? Path.GetDirectoryName(log)! : LogFolder);
        issue.Click += (_, _) =>
        {
            var raw = $"Crash: {ex.GetType().Name}: {ex.Message}";
            var title = Uri.EscapeDataString(raw.Length > 120 ? raw[..120] : raw);
            Shell.Open($"{Updater.ReleasesPage.Replace("/releases", "")}/issues/new?title={title}&body={Uri.EscapeDataString("Paste the copied details here.")}");
        };
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
        _showing = false;
        if (fatal) Environment.Exit(1);
    }
}
