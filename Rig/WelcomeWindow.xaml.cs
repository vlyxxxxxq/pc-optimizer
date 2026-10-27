using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Rig.Services;

namespace Rig;

/// <summary>Installation, essentials, and optional tools; nothing is applied until the final review is accepted.</summary>
public partial class WelcomeWindow : Window
{
    readonly AppSettings _settings;
    readonly bool _preview, _alreadyInstalled;
    readonly FrameworkElement[] _steps;
    int _step;
    bool _busy, _complete, _installationFinished;

    /// <summary>True only after the entire setup succeeded. The caller then launches the installed copy.</summary>
    public bool Installed { get; private set; }

    public WelcomeWindow(AppSettings settings, bool preview = false)
    {
        InitializeComponent();
        _settings = settings;
        _preview = preview;
        _alreadyInstalled = !preview && Installer.IsRunningInstalled;
        _steps = [Step1, Step2, Step3];
        VersionText.Text = $"Version {Updater.CurrentVersion}";
        PreviewBadge.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        InstallOptions.Visibility = _alreadyInstalled ? Visibility.Collapsed : Visibility.Visible;
        InstalledNotice.Visibility = _alreadyInstalled ? Visibility.Visible : Visibility.Collapsed;
        AutoGame.IsChecked = settings.AutoGameMode;
        Notify.IsChecked = settings.Notifications;
        // First-run tracking is an explicit choice, even if the stored model's defaults change later.
        ClipboardChoice.IsChecked = ScreenTimeChoice.IsChecked = false;
        DataLocationText.Text = "Settings and enabled history are saved in %AppData%\\Rig, including in portable mode.";
        InstallChoice.Checked += Mode_Changed;
        PortableChoice.Checked += Mode_Changed;
        foreach (var toggle in new[] { StartWithWindows, AutoGame, Notify, ClipboardChoice, ScreenTimeChoice })
            toggle.Click += (_, _) => UpdateSummary();
        SourceInitialized += (_, _) =>
        {
            // Only the content scrolls; the controls remain reachable on small work areas.
            var area = SystemParameters.WorkArea;
            MinHeight = Math.Min(MinHeight, Math.Max(320, area.Height - 32));
            MinWidth = Math.Min(MinWidth, Math.Max(600, area.Width - 32));
            Height = Math.Min(Height, area.Height - 32);
            Width = Math.Min(Width, area.Width - 32);
            int dark = Theme.IsDark ? 1 : 0;
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };
        SizeChanged += (_, _) =>
        {
            RailColumn.Width = new GridLength(ActualWidth < 830 ? 190 : 224);
            bool compact = ActualHeight < 600;
            StepRail.Margin = new Thickness(0, compact ? 32 : 54, 0, 16);
            RailHelp.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var detail in new[] { StepDetail1, StepDetail2, StepDetail3 })
                detail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        };
        UpdateLocation();
        Render();
    }

    bool WantsInstall => _alreadyInstalled || InstallChoice.IsChecked == true;

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        // Portable should not add a sign-in entry unless the user later explicitly enables it.
        StartWithWindows.IsChecked = WantsInstall;
        UpdateLocation();
        UpdateSummary();
    }

    void UpdateLocation()
    {
        DesktopShortcut.Visibility = WantsInstall ? Visibility.Visible : Visibility.Collapsed;
        LocationLabel.Text = WantsInstall ? "APP LOCATION" : "RUNS FROM";
        LocationText.Text = WantsInstall
            ? (_preview ? @"%LocalAppData%\Programs\Rig" : Installer.InstallDir)
            : Path.GetDirectoryName(Environment.ProcessPath) ?? "The folder containing Rig.exe";
        LocationHint.Text = WantsInstall
            ? "Installed only for your Windows account. Your personal files stay where they are."
            : "Keep this EXE somewhere permanent if you enable Start with Windows. Settings are saved in %AppData%\\Rig.";
    }

    void UpdateSummary()
    {
        SummaryText.Text = string.Join("  ·  ",
            _alreadyInstalled ? "Already installed" : WantsInstall ? "Install for your account" : "Run portable",
            StartWithWindows.IsChecked == true ? "Start at sign-in" : "Manual startup",
            AutoGame.IsChecked == true ? "Automatic Game Mode on" : "Manual Game Mode");
    }

    void Render()
    {
        for (int i = 0; i < _steps.Length; i++) _steps[i].Visibility = !_busy && !_complete && i == _step ? Visibility.Visible : Visibility.Collapsed;
        WorkingStep.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        CompleteStep.Visibility = _complete ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = !_complete && !_busy && _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _complete ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsEnabled = NextButton.IsEnabled = !_busy;
        StepCounter.Text = _complete ? "SETUP COMPLETE" : _busy ? "FINISHING SETUP" : $"SETUP · {_step + 1} OF 3";
        NextButton.Content = _complete ? (_preview ? "Close preview" : "Open Rig") : _busy ? "Setting up…"
            : _step < 2 ? "Continue" : WantsInstall && !_alreadyInstalled ? "Install Rig" : "Finish setup";
        var dots = new[] { StepDot1, StepDot2, StepDot3 };
        var numbers = new[] { StepNumber1, StepNumber2, StepNumber3 };
        var labels = new[] { StepLabel1, StepLabel2, StepLabel3 };
        for (int i = 0; i < dots.Length; i++)
        {
            bool done = _complete || i < _step, current = !_complete && i == _step;
            dots[i].Background = (Brush)FindResource(done ? "GoodTintBrush" : current ? "InfoTintBrush" : "ControlBrush");
            numbers[i].Text = done ? "✓" : (i + 1).ToString();
            numbers[i].Foreground = (Brush)FindResource(done ? "GoodBrush" : current ? "AccentLightBrush" : "MutedBrush");
            labels[i].Foreground = (Brush)FindResource(done || current ? "TextBrush" : "MutedBrush");
        }
        UpdateSummary();
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0 || _busy) return;
        _step--;
        ErrorPanel.Visibility = Visibility.Collapsed;
        Render();
        StepScroll.ScrollToTop();
    }

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_complete) { DialogResult = !_preview; return; }
        if (_step < 2)
        {
            _step++;
            Render();
            StepScroll.ScrollToTop();
            return;
        }
        await FinishAsync();
    }

    async Task FinishAsync()
    {
        var options = new SetupOptions(WantsInstall, DesktopShortcut.IsChecked == true, StartWithWindows.IsChecked == true,
            AutoGame.IsChecked == true, ClipboardChoice.IsChecked == true, ScreenTimeChoice.IsChecked == true, Notify.IsChecked == true);
        _busy = true;
        ErrorPanel.Visibility = Visibility.Collapsed;
        SetupBar.Value = 0;
        ProgressText.Text = _preview ? "Previewing your setup. Nothing is installed or saved." : "Preparing your setup…";
        Render();
        StepScroll.ScrollToTop();
        var progress = new Progress<SetupProgress>(p => { SetupBar.Value = p.Fraction; ProgressText.Text = p.Message; });
        SetupResult result;
        if (_preview)
        {
            // Render the flow without invoking installation, startup, settings, or app services.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            result = new(options.Install);
        }
        else result = await Setup.ApplyAsync(_settings, options, _alreadyInstalled || _installationFinished, new WindowsSetupActions(), progress);
        _busy = false;
        _installationFinished = result.Installed;
        if (!result.Succeeded)
        {
            Render();
            ErrorText.Text = result.Error + " Your setup has not finished. You can retry with the button below.";
            PortableRetryButton.Visibility = !_alreadyInstalled && !result.Installed && options.Install ? Visibility.Visible : Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            NextButton.Content = "Try again";
            ErrorPanel.BringIntoView();
            return;
        }
        Installed = !_preview && result.Installed;
        _complete = true;
        CompleteText.Text = _preview ? "This is a preview. No files, startup entries, or preferences were changed."
            : (Installed ? "Rig is installed for your Windows account." : "Rig will run from its current folder.") + " "
              + (options.StartWithWindows ? "It will be ready in the tray when you sign in." : "Open it whenever you need it.");
        Render();
        StepScroll.ScrollToTop();
        NextButton.Focus();
    }

    void PortableRetry_Click(object sender, RoutedEventArgs e)
    {
        PortableChoice.IsChecked = true;
        _step = 0;
        ErrorPanel.Visibility = Visibility.Collapsed;
        Render();
        StepScroll.ScrollToTop();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }
}
