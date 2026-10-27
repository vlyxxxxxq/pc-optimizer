using System.Globalization;
using System.Text.Json;

namespace Rig.Services;

public sealed class RenameRules
{
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool MatchCase { get; set; }
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    /// <summary>"none", "prefix", "suffix" or "name" (the number becomes the whole name).</summary>
    public string Numbering { get; set; } = "none";
    public int Start { get; set; } = 1;
    public int Digits { get; set; } = 2;
    /// <summary>"keep", "lower", "upper" or "title".</summary>
    public string Case { get; set; } = "keep";
    public bool LowerExtension { get; set; }
}

public sealed record RenameRow(string Folder, string Old, string New, string Status, FileIdentity? Identity = null)
{
    public bool Changes => Old != New && Status == "";
    public string Display => Status == "" ? (Old == New ? "unchanged" : "") : Status;
}

/// <summary>
/// Renames many files at once from simple rules, with a live preview. Every batch is journaled (see BatchMover):
/// files are checked again just before they're renamed, names can swap, and Undo never overwrites a file that has
/// since taken an old name; anything it can't put back stays listed in Recovery.
/// </summary>
public static class Renamer
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Why Windows wouldn't accept this file name, or "".</summary>
    public static string Problem(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Trim().Length == 0) return "empty name";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "invalid character";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "can't end with a dot or space";
        if (Reserved.Contains(name.Split('.')[0].TrimEnd())) return "reserved by Windows";
        if (name.Length > 255) return "name too long";
        if (name.StartsWith(".rig-op-", StringComparison.OrdinalIgnoreCase)) return "reserved by Rig";
        return "";
    }

    public static List<RenameRow> Plan(string folder, string filter, RenameRules r)
    {
        var all = Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFileSystemInfos().ToList() : [];
        var files = all.OfType<FileInfo>()
            .Where(f => f.Name.EndsWith(Path.GetExtension(f.Name)) && Matches(f.Name, filter))
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var rows = new List<RenameRow>();
        int n = r.Start;
        foreach (var f in files)
        {
            var stem = Path.GetFileNameWithoutExtension(f.Name);
            var ext = Path.GetExtension(f.Name);
            if (r.Find.Length > 0)
                stem = stem.Replace(r.Find, r.Replace, r.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            stem = r.Case switch
            {
                "lower" => stem.ToLower(CultureInfo.CurrentCulture),
                "upper" => stem.ToUpper(CultureInfo.CurrentCulture),
                "title" => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(stem.ToLower(CultureInfo.CurrentCulture)),
                _ => stem,
            };
            var number = n.ToString(new string('0', Math.Clamp(r.Digits, 1, 6)));
            stem = r.Numbering switch
            {
                "prefix" => $"{number} {stem}",
                "suffix" => $"{stem} {number}",
                "name" => number,
                _ => stem,
            };
            n++;
            stem = r.Prefix + stem + r.Suffix;
            if (r.LowerExtension) ext = ext.ToLowerInvariant();
            var name = stem + ext;
            rows.Add(new RenameRow(folder, f.Name, name, name == f.Name ? "" : Problem(name), FileIdentity.Of(f.FullName)));
        }
        // No two files may end up with the same name, and a new name can't take the name of anything else in the
        // folder: an untouched file in the batch, or a file or folder the filter left out.
        var others = all.Select(e => e.Name).Except(rows.Select(x => x.Old), StringComparer.OrdinalIgnoreCase).ToList();
        var finalNames = rows.Select(x => x.Status == "" ? x.New : x.Old).ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Status != "" || rows[i].Old == rows[i].New) continue;
            bool clash = finalNames.Where((_, j) => j != i).Contains(rows[i].New, StringComparer.OrdinalIgnoreCase)
                         || others.Contains(rows[i].New, StringComparer.OrdinalIgnoreCase);
            if (clash) rows[i] = rows[i] with { Status = "name already used" };
        }
        return rows;
    }

    static bool Matches(string name, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Trim() is "*" or "*.*") return true;
        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filter.Trim(), name, ignoreCase: true);
    }

    /// <summary>Renames the previewed files (two-step, so swaps work), checking each file is still the one previewed.</summary>
    public static BatchResult Apply(List<RenameRow> rows, CancellationToken cancel = default)
    {
        ImportLegacy();
        var moves = rows.Where(r => r.Changes)
            .Select(r => new BatchMover.Move(Path.Combine(r.Folder, r.Old), Path.Combine(r.Folder, r.New), r.Identity)).ToList();
        return BatchMover.Apply("rename", rows.FirstOrDefault()?.Folder ?? "", moves, twoStep: true, cancel);
    }

    public static bool CanUndo { get { ImportLegacy(); return BatchMover.Latest("rename") is not null; } }

    /// <summary>Puts the last batch back, as far as it safely can.</summary>
    public static BatchResult Undo(CancellationToken cancel = default)
    {
        ImportLegacy();
        return BatchMover.Latest("rename") is { } j ? BatchMover.Undo(j, cancel) : BatchResult.Nothing("Nothing to undo.");
    }

    /// <summary>2.0's undo file (pairs of full paths) becomes a journal, so it can still be undone after updating.</summary>
    static void ImportLegacy()
    {
        var legacy = AppSettings.DataFile("rename-undo.json");
        if (!File.Exists(legacy)) return;
        try
        {
            var pairs = JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(legacy)) ?? [];
            var j = new Journal
            {
                Kind = "rename", TwoStep = true, State = JournalState.Applied, Created = File.GetLastWriteTime(legacy),
                Folder = pairs.Count > 0 ? Path.GetDirectoryName(pairs[0][1])! : "",
                Items = pairs.Where(p => p.Length == 2).Select(p => new JournalItem { From = p[0], To = p[1], State = ItemState.Done, After = FileIdentity.Of(p[1]) }).ToList(),
            };
            if (j.Items.Count == 0 || j.Save()) File.Delete(legacy);
        }
        catch { }
    }
}
