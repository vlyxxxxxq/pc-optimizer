using System.Windows;

namespace Rig;

public partial class App : Application
{
    static Mutex? _instanceLock;
    EventWaitHandle? _showSignal, _quitSignal;

    /// <summary>
    /// Name of the "please quit" signal for this settings profile. The uninstaller (and updates) use it so the running
    /// copy exits properly: Game Mode puts everything back and recordings are finished before files are removed.
    /// </summary>
    public static string QuitSignalName
    {
        get
        {
            var p = Environment.GetEnvironmentVariable("RIG_SETTINGS");
            var profile = p is { Length: > 0 } ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(p.ToLowerInvariant())))[..12] : "";
            return $@"Local\Rig.Quit{profile}";
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services.CrashReporter.Install(this);

        // Command-line modes that don't open the main window.
        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Services.Installer.RunUninstall();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--maintenance", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Services.Maintenance.RunHeadlessAsync().ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }

        // One copy per settings profile: a second launch just brings the running one to the front.
        // A separate profile (RIG_SETTINGS) runs alongside, so testing never touches the real app.
        var profile = Environment.GetEnvironmentVariable("RIG_SETTINGS") is { Length: > 0 } p
            ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(p.ToLowerInvariant())))[..12]
            : "";
        _instanceLock = new Mutex(true, $@"Local\Rig.Instance{profile}", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Rig.Show{profile}");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown; // the tray keeps us alive when the window is closed
        bool tray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        Services.Installer.RefreshRegistration();

        // First run: welcome tour. Choosing "Install" relaunches from the installed copy.
        var settings = Services.AppSettings.Load();
        Services.Theme.Apply(Resources, settings.Theme != "Light", settings.Accent);
        if (Services.Migration.Run(settings, e.Args)) { Shutdown(); return; } // continues from the new Rig folder
        if (settings.IsFirstRun && !tray)
        {
            var welcome = new WelcomeWindow(settings);
            welcome.ShowDialog();
            if (welcome.Installed && !Services.Installer.IsRunningInstalled)
            {
                ReleaseInstanceLock();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Services.Installer.InstalledExe) { UseShellExecute = true });
                Shutdown();
                return;
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        if (!tray) window.Show();

        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.Invoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "ShowSignal" }.Start();
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
        new Thread(() =>
        {
            if (_quitSignal.WaitOne()) Dispatcher.Invoke(() => { (MainWindow as MainWindow)?.Cleanup(); Shutdown(); });
        }) { IsBackground = true, Name = "QuitSignal" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow as MainWindow)?.Cleanup();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.Cleanup();
        base.OnSessionEnding(e);
    }

    /// <summary>Starts a fresh copy (same profile) and exits this one.</summary>
    public static void Restart()
    {
        ReleaseInstanceLock();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        Current.Shutdown();
    }

    /// <summary>Lets a relaunched (e.g. elevated) copy start before this one has fully exited.</summary>
    public static void ReleaseInstanceLock()
    {
        try { _instanceLock?.ReleaseMutex(); } catch { }
        _instanceLock?.Dispose();
        _instanceLock = null;
    }
}
