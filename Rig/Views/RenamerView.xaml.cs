using System.Windows;
using System.Windows.Controls;
using Rig.Services;

namespace Rig.Views;

public partial class RenamerView : UserControl
{
    string? _folder;
    List<RenameRow> _rows = [];

    public RenamerView()
    {
        InitializeComponent();
        // Tests point this at a folder of dummy files.
        if (Environment.GetEnvironmentVariable("RIG_TEST_RENAME") is { Length: > 0 } test) SetFolder(test);
    }

    public void EnsureLoaded()
    {
        UndoButton.IsEnabled = Renamer.CanUndo;
        RenderRecovery();
    }

    void RenderRecovery()
    {
        int n = BatchMover.NeedingAttention().Count;
        RecoveryButton.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecoveryButton.Content = $"Recovery ({n})";
    }

    void Recovery_Click(object sender, RoutedEventArgs e)
    {
        new RecoveryWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        Refresh();
    }

    void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with the files to rename" };
        if (dialog.ShowDialog() == true) SetFolder(dialog.FolderName);
    }

    void SetFolder(string folder)
    {
        _folder = folder;
        FolderText.Text = folder;
        Refresh();
    }

    RenameRules Rules() => new()
    {
        Find = FindBox.Text, Replace = ReplaceBox.Text, MatchCase = MatchCaseCheck.IsChecked == true,
        Prefix = PrefixBox.Text, Suffix = SuffixBox.Text,
        Numbering = (NumberingBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none",
        Start = int.TryParse(StartBox.Text, out var s) ? s : 1, Digits = int.TryParse(DigitsBox.Text, out var d) ? d : 2,
        Case = (CaseBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "keep", LowerExtension = LowerExtCheck.IsChecked == true,
    };

    void Rules_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) Refresh(); }

    void Refresh()
    {
        if (_folder is null) return;
        try { _rows = Renamer.Plan(_folder, FilterBox.Text, Rules()); }
        catch (Exception ex) { SummaryText.Text = $"Can't read that folder: {ex.Message}"; return; }
        Preview.ItemsSource = _rows;
        int changes = _rows.Count(r => r.Changes), problems = _rows.Count(r => r.Status != "");
        SummaryText.Text = _rows.Count == 0 ? "No files match" : $"{changes} of {_rows.Count} files will be renamed" + (problems > 0 ? $" · {problems} skipped" : "");
        RenameButton.IsEnabled = changes > 0;
        RenameButton.Content = changes > 0 ? $"Rename {changes}" : "Rename";
        UndoButton.IsEnabled = Renamer.CanUndo;
        RenderRecovery();
    }

    async void Rename_Click(object sender, RoutedEventArgs e)
    {
        RenameButton.IsEnabled = UndoButton.IsEnabled = false;
        var rows = _rows;
        var result = await Task.Run(() => Renamer.Apply(rows));
        FindBox.Clear(); ReplaceBox.Clear(); PrefixBox.Clear(); SuffixBox.Clear();
        NumberingBox.SelectedIndex = 0; CaseBox.SelectedIndex = 0; LowerExtCheck.IsChecked = false;
        Refresh();
        Report(result, "Renamed");
    }

    async void Undo_Click(object sender, RoutedEventArgs e)
    {
        RenameButton.IsEnabled = UndoButton.IsEnabled = false;
        var result = await Task.Run(() => Renamer.Undo());
        Refresh();
        Report(result, "Put back");
    }

    void Report(BatchResult result, string verb)
    {
        SummaryText.Text = result.Summary(verb);
        SummaryText.ToolTip = result.Details.Count > 0 ? string.Join("\n", result.Details.Take(20)) : null;
        RenderRecovery();
    }
}
