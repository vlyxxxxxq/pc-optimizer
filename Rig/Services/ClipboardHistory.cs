using System.Collections.ObjectModel;
using System.Windows;

namespace Rig.Services;

public sealed record ClipEntry(string Text, DateTime Copied)
{
    public string Preview => Text.Length > 300 ? Text[..300].ReplaceLineEndings(" ⏎ ") + "…" : Text.ReplaceLineEndings(" ⏎ ");
    public string When => Copied.Date == DateTime.Today ? Copied.ToString("t") : Copied.ToString("g");
}

/// <summary>
/// The last 50 things copied as text, kept in memory only (gone when the app closes).
/// Anything a password manager marks as private is skipped, the same rule Windows' own Win+V history follows.
/// </summary>
public sealed class ClipboardHistory
{
    const int Max = 50;
    public ObservableCollection<ClipEntry> Items { get; } = [];
    public bool Enabled { get; set; } = true;
    string? _ignoreNext;

    public void OnClipboardChanged()
    {
        if (!Enabled) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null || !data.GetDataPresent(DataFormats.UnicodeText)) return;
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") &&
                data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4 && ms.ToArray()[0] == 0) return;
            var text = data.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000) return;
            if (text == _ignoreNext) { _ignoreNext = null; return; }
            Add(text);
        }
        catch { /* another app holds the clipboard; skip this one */ }
    }

    void Add(string text)
    {
        var existing = Items.FirstOrDefault(i => i.Text == text);
        if (existing is not null) Items.Remove(existing);
        Items.Insert(0, new ClipEntry(text, DateTime.Now));
        while (Items.Count > Max) Items.RemoveAt(Items.Count - 1);
    }

    /// <summary>Puts an old entry back on the clipboard (moving it to the top).</summary>
    public void Use(ClipEntry entry)
    {
        Items.Remove(entry);
        Items.Insert(0, entry with { Copied = DateTime.Now });
        _ignoreNext = entry.Text;
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(entry.Text); return; }
            catch { Thread.Sleep(50); }
        }
    }

    public void Clear() => Items.Clear();
}

/// <summary>A scratch pad saved next to the settings file.</summary>
public static class QuickNotes
{
    static string FilePath => AppSettings.DataFile("notes.txt");

    public static string Load()
    {
        try { return File.Exists(FilePath) ? File.ReadAllText(FilePath) : ""; }
        catch { return ""; }
    }

    public static void Save(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, text);
        }
        catch { }
    }
}
