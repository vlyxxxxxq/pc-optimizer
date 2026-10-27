using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rig.Services;

namespace Rig.Views;

/// <summary>
/// File operations that need a decision: interrupted batches, files under a temporary name, and Undo conflicts
/// (a new file took an old name). Nothing here is ever deleted; every action moves or keeps files.
/// </summary>
public sealed class RecoveryWindow : Window
{
    readonly ListBox _journals = new() { Width = 300, Margin = new Thickness(0, 0, 16, 0) };
    readonly ItemsControl _items = new();
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    readonly Button _retry = new() { Content = "Try again", Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _keepBoth = new() { Content = "Keep both files", Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _reveal = new() { Content = "Show in folder", Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _dismiss = new() { Content = "Leave as is" };

    public RecoveryWindow()
    {
        Title = "File recovery";
        Width = 980; Height = 600; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _retry.Style = (Style)FindResource("PrimaryButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_journals, "JournalList");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_retry, "RetryButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_keepBoth, "KeepBothButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "RecoveryStatus");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_status, System.Windows.Automation.AutomationLiveSetting.Polite);

        _journals.DisplayMemberPath = nameof(Journal.Title);
        _journals.SelectionChanged += (_, _) => ShowSelected();
        _items.ItemTemplate = ItemTemplate();
        _retry.Click += (_, _) => Act(j => BatchMover.Recover(j, keepBoth: false), "Tried again");
        _keepBoth.Click += (_, _) => Act(j => BatchMover.Recover(j, keepBoth: true), "Kept both");
        _reveal.Click += (_, _) => { if (Selected?.Items.FirstOrDefault(i => File.Exists(i.Where)) is { } i) Shell.Reveal(i.Where); else if (Selected is { } j) Shell.Open(j.Folder); };
        _dismiss.Click += (_, _) =>
        {
            if (Selected is not { } j) return;
            BatchMover.Dismiss(j);
            _status.Text = j.State == JournalState.Dismissed ? "Closed. The files stay where they are now." : "A file is still under a temporary name; use Try again or Keep both first.";
            Reload();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { _retry, _keepBoth, _reveal, _dismiss } };
        var right = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(buttons);
        right.Children.Add(_status);
        right.Children.Add(new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var body = new DockPanel { Margin = new Thickness(24) };
        var heading = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 16),
            Children =
            {
                new TextBlock { Text = "File recovery", FontSize = 22, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "Renames and Downloads sorts that didn't finish cleanly. Nothing is ever deleted here: you can retry, keep both files side by side, or leave things as they are.",
                                TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 0) },
            },
        };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(_journals, Dock.Left);
        body.Children.Add(heading);
        body.Children.Add(_journals);
        body.Children.Add(right);
        Content = body;
        Reload();
    }

    Journal? Selected => _journals.SelectedItem as Journal;

    void Reload()
    {
        var id = Selected?.Id;
        // Ones needing a decision first, then the recent history for reference.
        var list = BatchMover.NeedingAttention().Concat(Journal.All().Where(j => j.State != JournalState.NeedsAttention).Take(10)).ToList();
        _journals.ItemsSource = list;
        _journals.SelectedItem = list.FirstOrDefault(j => j.Id == id) ?? list.FirstOrDefault();
        if (list.Count == 0) _status.Text = "Nothing needs attention.";
        ShowSelected();
    }

    void ShowSelected()
    {
        var j = Selected;
        _items.ItemsSource = j?.Items;
        bool open = j?.State == JournalState.NeedsAttention;
        _retry.IsEnabled = _keepBoth.IsEnabled = _dismiss.IsEnabled = open;
        _reveal.IsEnabled = j is not null;
        if (j is not null && _status.Text.Length == 0)
            _status.Text = open ? $"{j.Open} file{(j.Open == 1 ? "" : "s")} need a decision." : $"Finished ({j.State}).";
    }

    void Act(Func<Journal, BatchResult> action, string verb)
    {
        if (Selected is not { } j) return;
        var r = action(j);
        _status.Text = $"{verb}: {r.Summary("Settled")}" + (r.Details.Count > 0 ? "\n" + string.Join("\n", r.Details.Take(6)) : "");
        Reload();
    }

    static DataTemplate ItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(MarginProperty, new Thickness(0, 0, 0, 10));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(JournalItem.Name)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var state = new FrameworkElementFactory(typeof(TextBlock));
        state.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(JournalItem.State)) { StringFormat = "State: {0}" });
        state.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var where = new FrameworkElementFactory(typeof(TextBlock));
        where.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(JournalItem.Where)) { StringFormat = "Now at: {0}" });
        where.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        where.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var note = new FrameworkElementFactory(typeof(TextBlock));
        note.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(JournalItem.Note)));
        note.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        note.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        row.AppendChild(name); row.AppendChild(state); row.AppendChild(where); row.AppendChild(note);
        return new DataTemplate { VisualTree = row };
    }
}
