using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Services;
using PCOptimizer.Views;

namespace PCOptimizer;

/// <summary>Everything the command palette can do.</summary>
public partial class MainWindow
{
    CommandPalette? _palette;
    List<PaletteCommand> _fixedCommands = [];
    List<PaletteCommand> _appCommands = [], _gameCommands = [], _windowCommands = [];
    DateTime _appsScanned;

    public void OpenPalette()
    {
        _palette ??= new CommandPalette(PaletteCommands);
        if (_fixedCommands.Count == 0) _fixedCommands = FixedCommands();
        _windowCommands = WindowCommands();
        _palette.Open();
        // Slow lists refresh in the background and show up on the next keystroke.
        if (DateTime.Now - _appsScanned > TimeSpan.FromMinutes(10))
        {
            _appsScanned = DateTime.Now;
            Task.Run(() =>
            {
                var apps = StartMenuApps();
                var games = InstalledGames.Find();
                Dispatcher.Invoke(() =>
                {
                    var gameNames = games.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _appCommands = apps.Where(a => !gameNames.Contains(a.Title)).ToList(); // the game entry covers it
                    _gameCommands = games.Select(g => new PaletteCommand
                    {
                        Title = g.Name, Detail = $"Play · {g.Store}", Glyph = "", Rank = 2,
                        Run = () => { InstalledGames.Start(g); if (_settings is { LaunchWithGameMode: true, AutoGameMode: false } && !_gameMode.IsActive) _ = _gameMode.ToggleAsync(); },
                    }).ToList();
                });
            });
        }
    }

    IEnumerable<PaletteCommand> PaletteCommands(string query)
    {
        if (Calculate(query) is { } result)
            yield return new PaletteCommand
            {
                Title = $"= {result}", Detail = "Enter copies the result", Glyph = "", Rank = -1, AlwaysMatch = true,
                Run = () => { try { Clipboard.SetText(result); } catch { } },
            };
        foreach (var c in _fixedCommands) yield return c;
        foreach (var c in _gameCommands) yield return c;
        foreach (var c in _appCommands) yield return c;
        foreach (var c in _windowCommands) yield return c;
    }

    void ShowPage(RadioButton nav)
    {
        ShowFromTray();
        nav.IsChecked = true;
    }

    List<PaletteCommand> FixedCommands()
    {
        var list = new List<PaletteCommand>();
        // One "go to" entry per sidebar page.
        foreach (var nav in _pages.Keys)
            list.Add(new PaletteCommand { Title = (string)nav.Content, Detail = "Open page", Glyph = (string)nav.Tag, Run = () => ShowPage(nav) });

        list.AddRange(
        [
            new() { Title = "Toggle Game Mode", Detail = "Action", Glyph = "", Run = () => _ = _gameMode.ToggleAsync() },
            new() { Title = "Clean junk now", Detail = "Junk Cleaner", Glyph = "", Run = async () => { ShowPage(NavCleaner); await CleanerPage.CleanAsync(); } },
            new() { Title = "Take a screenshot", Detail = "Clips & Screenshots", Glyph = "", Run = () => Dispatcher.InvokeAsync(async () => { await Task.Delay(400); TakeScreenshot(); }) },
            new() { Title = "Record screen", Detail = "MP4 or GIF · Clips & Screenshots", Glyph = "", Run = () => Dispatcher.InvokeAsync(async () => { await Task.Delay(300); ToggleRecording(); }) },
            new() { Title = "Save the last 30 seconds (clip)", Detail = "Clips & Screenshots", Glyph = "", Run = SaveClip },
            new() { Title = "Clipboard history", Detail = "Ctrl+Alt+V", Glyph = "", Run = () => { if (_clipboard.Enabled) (_clipboardPopup ??= new ClipboardPopup(_clipboard)).Open(); } },
            new() { Title = "Flush DNS cache", Detail = "Network fix", Glyph = "", Run = async () => { await Shell.RunAsync("ipconfig", "/flushdns"); Notify("DNS cache flushed", "Websites will look up fresh addresses."); } },
            new() { Title = "Restart Explorer", Detail = "Fixes a stuck taskbar", Glyph = "", Run = RestartExplorer },
            new() { Title = "Task Manager", Detail = "Windows", Glyph = "", Run = () => Shell.Open("taskmgr.exe") },
            new() { Title = "Windows Settings", Detail = "Windows", Glyph = "", Run = () => Shell.Open("ms-settings:") },
            new() { Title = "Check for PC Optimizer updates", Detail = "Settings", Glyph = "", Run = async () => { ShowPage(NavSettings); await SettingsPage.CheckForUpdateAsync(quiet: false); } },
        ]);
        return list;
    }

    static void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { } p.Dispose(); }
        Task.Delay(1500).ContinueWith(_ => { if (Process.GetProcessesByName("explorer").Length == 0) Shell.Open("explorer.exe"); });
    }

    /// <summary>Open windows, to switch to or end (end asks first; system processes are never offered).</summary>
    List<PaletteCommand> WindowCommands()
    {
        var list = new List<PaletteCommand>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle) || p.Id == Environment.ProcessId) continue;
                if (p.SessionId == 0 || p.ProcessName is "explorer" or "TextInputHost" or "ApplicationFrameHost" or "SystemSettings") continue;
                var name = p.ProcessName;
                var title = p.MainWindowTitle.Length > 60 ? p.MainWindowTitle[..60] + "…" : p.MainWindowTitle;
                int pid = p.Id;
                list.Add(new PaletteCommand
                {
                    Title = $"End task: {name}", Detail = title, Glyph = "", Rank = 5, NeedsQuery = true,
                    Run = () =>
                    {
                        if (MessageBox.Show($"End {name}? Anything unsaved in it will be lost.", "End task",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                        try { using var proc = Process.GetProcessById(pid); proc.Kill(entireProcessTree: true); }
                        catch (Exception ex) { MessageBox.Show($"Couldn't end {name}: {ex.Message}", "End task"); }
                    },
                });
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>
    /// Every app in the Start menu, Store apps included, from Windows' Apps folder (shell:AppsFolder).
    /// Falls back to the Start menu shortcut folders if the shell can't be asked.
    /// </summary>
    static List<PaletteCommand> StartMenuApps()
    {
        var list = new List<PaletteCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The shell's COM objects want an STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name, id = item.Path;
                    if (string.IsNullOrEmpty(name) || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                    list.Add(new PaletteCommand { Title = name, Detail = "App", Glyph = "", Rank = 3, NeedsQuery = true,
                                                  Run = () => Shell.Open($"shell:AppsFolder\\{id}") });
                }
            }
            catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
        if (list.Count > 0) return list;

        foreach (var root in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(Environment.GetFolderPath(root), "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                    list.Add(new PaletteCommand { Title = name, Detail = "App", Glyph = "", Rank = 3, NeedsQuery = true, Run = () => Shell.Open(lnk) });
                }
            }
            catch { }
        }
        return list;
    }

    /// <summary>Arithmetic typed into the palette, e.g. "1920*1080" or "(60-45)/60*100".</summary>
    static string? Calculate(string q)
    {
        if (q.Length < 3 || !System.Text.RegularExpressions.Regex.IsMatch(q, @"^[\d\s.+\-*/()%]+$") || !q.Any(c => "+-*/%".Contains(c)) || !q.Any(char.IsDigit))
            return null;
        try
        {
            var value = new System.Data.DataTable().Compute(q, null);
            return value is DBNull ? null : Convert.ToDouble(value).ToString("0.##########", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch { return null; }
    }
}
