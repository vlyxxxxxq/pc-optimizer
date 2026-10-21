using System.Windows;

namespace PCOptimizer;

public partial class App : Application
{
    static Mutex? _instanceLock;
    EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // One copy only: a second launch just brings the running one to the front.
        _instanceLock = new Mutex(true, @"Local\PCOptimizer.Instance", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PCOptimizer.Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown; // the tray keeps us alive when the window is closed
        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) window.Show();

        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.Invoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "ShowSignal" }.Start();
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

    /// <summary>Lets a relaunched (e.g. elevated) copy start before this one has fully exited.</summary>
    public static void ReleaseInstanceLock()
    {
        try { _instanceLock?.ReleaseMutex(); } catch { }
        _instanceLock?.Dispose();
        _instanceLock = null;
    }
}
