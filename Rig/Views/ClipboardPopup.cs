using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Rig.Services;

namespace Rig.Views;

/// <summary>
/// Ctrl+Alt+V picker: opens at the mouse, type to filter, Enter (or click) pastes into the app you were in.
/// </summary>
public sealed class ClipboardPopup : Window
{
    readonly ClipboardHistory _history;
    readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6, 4, 6, 4) };
    readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), MaxHeight = 360 };
    readonly ICollectionView _view;
    IntPtr _target;

    public ClipboardPopup(ClipboardHistory history)
    {
        _history = history;
        Title = "Clipboard history";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = (Brush)FindResource("TextBrush");

        _view = new CollectionViewSource { Source = history.Items }.View;
        _view.Filter = o => _search.Text.Length == 0 || ((ClipEntry)o).Text.Contains(_search.Text, StringComparison.OrdinalIgnoreCase);
        _list.ItemsSource = _view;
        _list.ItemContainerStyle = (Style)FindResource("PlainListItem");
        _list.ItemTemplate = ItemTemplateFor();
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

        Content = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Clipboard history · Enter to paste · Esc to close", FontSize = 11,
                                    Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(2, 0, 0, 8) },
                    _search,
                    _list,
                },
            },
        };

        _search.TextChanged += (_, _) => { _view.Refresh(); _list.SelectedIndex = 0; };
        PreviewKeyDown += OnKey;
        _list.MouseLeftButtonUp += (_, _) => Paste();
        Deactivated += (_, _) => Hide();
    }

    static DataTemplate ItemTemplateFor()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ClipEntry.Preview)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(MaxHeightProperty, 36.0);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.AppendChild(text);
        return new DataTemplate { VisualTree = border };
    }

    /// <summary>Remembers the window to paste into, then opens near the mouse.</summary>
    public void Open()
    {
        _target = Native.GetForegroundWindow();
        _search.Text = "";
        var mouse = System.Windows.Forms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this);
        Left = Math.Min(mouse.X / dpi.DpiScaleX, SystemParameters.VirtualScreenWidth - Width - 10);
        Top = Math.Min(mouse.Y / dpi.DpiScaleY, SystemParameters.WorkArea.Bottom - 440);
        Show();
        Activate();
        _list.SelectedIndex = 0;
        _search.Focus();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Hide(); e.Handled = true; break;
            case Key.Enter: Paste(); e.Handled = true; break;
            case Key.Down: _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; break;
            case Key.Up: _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; break;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    async void Paste()
    {
        if (_list.SelectedItem is not ClipEntry entry) return;
        _history.Use(entry);
        Hide();
        if (_target == IntPtr.Zero) return;
        SetForegroundWindow(_target);
        await Task.Delay(120); // let the target window take focus before the keystroke
        KeySender.Chord(Key.LeftCtrl, Key.V);
    }
}
