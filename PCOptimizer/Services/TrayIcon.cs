using System.Windows;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PCOptimizer.Services;

/// <summary>Notification-area icon with a menu; balloon tips show as native Windows notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripMenuItem _gameItem;

    public event EventHandler? OpenRequested;
    public event EventHandler? GameModeRequested;
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkMenuColors()),
            BackColor = Drawing.Color.FromArgb(0x1B, 0x1F, 0x2A),
            ForeColor = Drawing.Color.FromArgb(0xE6, 0xE8, 0xEE),
            ShowImageMargin = false,
            Padding = new WinForms.Padding(2, 4, 2, 4),
        };
        menu.Items.Add("Open PC Optimizer", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _gameItem = new WinForms.ToolStripMenuItem("Game Mode", null, (_, _) => GameModeRequested?.Invoke(this, EventArgs.Empty))
        {
            CheckOnClick = false,
        };
        menu.Items.Add(_gameItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        foreach (WinForms.ToolStripItem item in menu.Items) item.Padding = new WinForms.Padding(8, 4, 16, 4);

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        _icon = new WinForms.NotifyIcon
        {
            Icon = new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize),
            Text = "PC Optimizer",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(this, EventArgs.Empty);
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetGameMode(bool on, string? game)
    {
        _gameItem.Text = on ? "Turn off Game Mode" : "Turn on Game Mode";
        var text = on ? $"PC Optimizer · Game Mode on{(game is null ? "" : $" ({game})")}" : "PC Optimizer";
        _icon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon tooltip limit
    }

    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, WinForms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    sealed class DarkMenuColors : WinForms.ProfessionalColorTable
    {
        static readonly Drawing.Color Bg = Drawing.Color.FromArgb(0x1B, 0x1F, 0x2A);
        static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x2A, 0x35, 0x50);
        static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x2C, 0x32, 0x42);

        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Line;
    }
}
