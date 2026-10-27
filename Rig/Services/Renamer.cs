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

public sealed record RenameRow(string Folder, string Old, string New, string Status)
{
    public bool Changes => Old != New && Status == "";
    public string Display => Status == "" ? (Old == New ? "unchanged" : "") : Status;
}

/// <summary>Renames many files at once from simple rules, with a live preview and one-step undo.</summary>
public static class Renamer
{
    static string UndoFile => AppSettings.DataFile("rename-undo.json");

    public static List<RenameRow> Plan(string folder, string filter, RenameRules r)
    {
        var files = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles(string.IsNullOrWhiteSpace(filter) ? "*" : filter.Trim())
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
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
            string status = "";
            if (stem.Trim().Length == 0) status = "empty name";
            else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) status = "invalid character";
            rows.Add(new RenameRow(folder, f.Name, name, status));
        }
        // Two files can't end up with the same name, and a new name can't take an untouched file's name.
        var finalNames = rows.Select(x => x.Status == "" ? x.New : x.Old).ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Status != "" || rows[i].Old == rows[i].New) continue;
            bool clash = finalNames.Where((_, j) => j != i).Contains(rows[i].New, StringComparer.OrdinalIgnoreCase);
            if (clash) rows[i] = rows[i] with { Status = "name already used" };
        }
        return rows;
    }

    /// <summary>Renames in two steps (via temporary names), so swaps like a→b, b→a work. Returns how many were renamed.</summary>
    public static int Apply(List<RenameRow> rows)
    {
        var todo = rows.Where(r => r.Changes).ToList();
        var temp = new List<(RenameRow Row, string Temp)>();
        foreach (var r in todo)
        {
            var t = Path.Combine(r.Folder, $".rig-rename-{Guid.NewGuid():N}{Path.GetExtension(r.Old)}");
            try { File.Move(Path.Combine(r.Folder, r.Old), t); temp.Add((r, t)); } catch { }
        }
        var done = new List<(string From, string To)>();
        foreach (var (r, t) in temp)
        {
            var target = Path.Combine(r.Folder, r.New);
            try { File.Move(t, target); done.Add((Path.Combine(r.Folder, r.Old), target)); }
            catch { try { File.Move(t, Path.Combine(r.Folder, r.Old)); } catch { } } // put it back
        }
        try { File.WriteAllText(UndoFile, JsonSerializer.Serialize(done.Select(d => new[] { d.From, d.To }).ToList())); } catch { }
        return done.Count;
    }

    public static bool CanUndo => File.Exists(UndoFile);

    /// <summary>Puts the last batch back. Returns how many were restored.</summary>
    public static int Undo()
    {
        if (!File.Exists(UndoFile)) return 0;
        var pairs = JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(UndoFile)) ?? [];
        var temp = new List<(string Temp, string Original)>();
        foreach (var p in pairs)
        {
            var t = Path.Combine(Path.GetDirectoryName(p[1])!, $".rig-rename-{Guid.NewGuid():N}");
            try { File.Move(p[1], t); temp.Add((t, p[0])); } catch { }
        }
        int n = 0;
        foreach (var (t, original) in temp)
        {
            try { File.Move(t, original); n++; } catch { }
        }
        File.Delete(UndoFile);
        return n;
    }
}
