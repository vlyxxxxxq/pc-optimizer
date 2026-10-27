using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Rig.Services;

namespace Rig;

/// <summary>First run: a short tour, three preferences, then install or portable.</summary>
public partial class WelcomeWindow : Window
{
    readonly AppSettings _settings;
    readonly FrameworkElement[] _steps;
    int _step;

    /// <summary>Set when the user chose to install: the caller relaunches from the installed copy.</summary>
    public bool Installed { get; private set; }

    sealed record Highlight(string Glyph, string Title, string Text);

    public WelcomeWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Highlights.ItemsSource = new[]
        {
            new Highlight("", "Game Mode", "Boosts your PC the moment a game starts, then puts everything back."),
            new Highlight("", "Clean up", "Junk files, big folders, duplicates and apps you don't use."),
            new Highlight("", "Speed up", "Startup apps, reversible Windows tweaks and a tidier right-click menu."),
            new Highlight("", "Know your PC", "Health score, hardware specs, benchmarks and network checks."),
        };
        // The installed copy (or a portable one that already chose) skips the install question.
        _steps = Installer.IsRunningInstalled ? [Step1, Step2] : [Step1, Step2, Step3];
        InstallChoice.Checked += (_, _) => Render();
        PortableChoice.Checked += (_, _) => Render();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };
        Render();
    }

    void Render()
    {
        for (int i = 0; i < _steps.Length; i++) _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Hidden;
        bool last = _step == _steps.Length - 1;
        NextButton.Content = last ? (_steps[_step] == Step3 && InstallChoice.IsChecked == true ? "Install" : "Get started") : "Next";

        Dots.Children.Clear();
        for (int i = 0; i < _steps.Length; i++)
            Dots.Children.Add(new Rectangle
            {
                Width = i == _step ? 22 : 8, Height = 8, RadiusX = 4, RadiusY = 4, Margin = new Thickness(0, 0, 6, 0),
                Fill = (Brush)FindResource(i == _step ? "AccentBrush" : "CardBorderBrush"),
            });
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0) { _step--; Render(); }
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < _steps.Length - 1) { _step++; Render(); return; }
        Finish();
    }

    void Finish()
    {
        _settings.AutoGameMode = AutoGame.IsChecked == true;
        _settings.Notifications = Notify.IsChecked == true;
        _settings.Save();

        bool install = _steps.Contains(Step3) && InstallChoice.IsChecked == true;
        try
        {
            if (install)
            {
                Installer.Install(DesktopShortcut.IsChecked == true);
                Installed = true;
            }
            AutoStart.Set(StartWithWindows.IsChecked == true, install ? Installer.InstalledExe : null);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't finish installing:\n{ex.Message}\n\nRig will run from where it is instead.",
                "Rig", MessageBoxButton.OK, MessageBoxImage.Warning);
            Installed = false;
        }
        DialogResult = true;
    }
}
