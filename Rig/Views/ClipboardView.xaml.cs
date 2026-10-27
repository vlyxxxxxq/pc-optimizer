using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Rig.Services;

namespace Rig.Views;

public partial class ClipboardView : UserControl
{
    ClipboardHistory? _history;
    AppSettings? _settings;
    readonly DispatcherTimer _saveNotes = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _loadingNotes;
    public event EventHandler? HotkeysPaused, HotkeysChanged;

    public ClipboardView()
    {
        InitializeComponent();
        _saveNotes.Tick += (_, _) =>
        {
            _saveNotes.Stop();
            QuickNotes.Save(Notes.Text);
            NotesSaved.Text = $"Saved {DateTime.Now:t}";
        };
        _loadingNotes = true;
        Notes.Text = QuickNotes.Load();
        _loadingNotes = false;
    }

    public void Attach(AppSettings settings, ClipboardHistory history)
    {
        _settings = settings;
        _ = new ShortcutCapture(ClipboardKeyButton, settings, nameof(AppSettings.ClipboardHotkey), () => HotkeysPaused?.Invoke(this, EventArgs.Empty), () => HotkeysChanged?.Invoke(this, EventArgs.Empty));
        _history = history;
        HistorySwitch.IsChecked = settings.ClipboardHistoryEnabled;
        var view = CollectionViewSource.GetDefaultView(history.Items);
        view.Filter = o => SearchBox.Text.Length == 0 || ((ClipEntry)o).Text.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase);
        History.ItemsSource = view;
        RenderText();
    }

    void RenderText() => HistoryText.Text = _settings?.ClipboardHistoryEnabled == true
        ? "Keeps the last 50 bits of text you copy, in memory only: it's gone when Rig closes. Passwords copied from password managers are never saved."
        : "Off. Nothing you copy is recorded.";

    void HistorySwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _history is null) return;
        _settings.ClipboardHistoryEnabled = _history.Enabled = HistorySwitch.IsChecked == true;
        if (!_history.Enabled) _history.Clear();
        _settings.Save();
        RenderText();
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        (History.ItemsSource as ICollectionView)?.Refresh();
    }

    void Clear_Click(object sender, RoutedEventArgs e) => _history?.Clear();

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipEntry entry) _history?.Use(entry);
    }

    void History_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (History.SelectedItem is ClipEntry entry) _history?.Use(entry);
    }

    void Notes_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingNotes) return;
        NotesSaved.Text = "Editing…";
        _saveNotes.Stop();
        _saveNotes.Start();
    }

    /// <summary>Writes any pending note edit straight away (on exit).</summary>
    public void FlushNotes()
    {
        if (_saveNotes.IsEnabled) { _saveNotes.Stop(); QuickNotes.Save(Notes.Text); }
    }
}
