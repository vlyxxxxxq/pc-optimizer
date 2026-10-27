using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Services;

/// <summary>Enough about a file to tell whether it's still the same file: size, write time and a hash of its start.</summary>
public sealed record FileIdentity(long Size, DateTime WriteUtc, string Head)
{
    public static FileIdentity? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(64 * 1024, info.Length)];
            int read = 0;
            while (read < buffer.Length && s.Read(buffer, read, buffer.Length - read) is var n and > 0) read += n;
            return new FileIdentity(info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)))[..16]);
        }
        catch { return null; }
    }

    public bool Matches(string path) => Of(path) is { } now && now == this;
}

public enum ItemState { Pending, Staged, Done, Skipped, Conflict, Failed, UndoStaged, Undone }
public enum JournalState { Applying, Applied, Undoing, Undone, NeedsAttention, Dismissed }

public sealed class JournalItem
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Where the file sits between the two steps of a two-step move (so swaps like a↔b work).</summary>
    public string? Temp { get; set; }
    public ItemState State { get; set; }
    public string? Note { get; set; }
    /// <summary>The file as previewed; a different file at From isn't moved.</summary>
    public FileIdentity? Before { get; set; }
    /// <summary>The file after moving; Undo only moves it back if it's still that file.</summary>
    public FileIdentity? After { get; set; }
    [JsonIgnore] public string Name => Path.GetFileName(From);
    [JsonIgnore] public string Where => State is ItemState.Staged or ItemState.UndoStaged && Temp is not null ? Temp : State == ItemState.Done ? To : From;
}

/// <summary>
/// A durable record of one batch of file moves (a rename or a Downloads sort). It is written before the first file
/// moves and after every step, so an interrupted batch, a failed Undo or a conflict can always be finished or put
/// right later. A record is only marked finished when every file is accounted for; it is never deleted while work
/// is unresolved.
/// </summary>
public sealed class Journal
{
    public string Id { get; set; } = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
    /// <summary>"rename" or "downloads".</summary>
    public string Kind { get; set; } = "";
    public string Folder { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public JournalState State { get; set; }
    /// <summary>Moves go via a temporary name first (renames, where names can swap).</summary>
    public bool TwoStep { get; set; }
    public List<JournalItem> Items { get; set; } = [];

    [JsonIgnore] public string Title => $"{(Kind == "rename" ? "Batch rename" : "Downloads sort")} · {Created:g}";
    [JsonIgnore] public int Open => Items.Count(i => i.State is ItemState.Staged or ItemState.UndoStaged or ItemState.Conflict or ItemState.Failed);
    [JsonIgnore] public bool CanUndo => State is JournalState.Applied or JournalState.NeedsAttention && Items.Any(i => i.State == ItemState.Done);

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string Directory => AppSettings.DataFile("journals");
    string FilePath => Path.Combine(Directory, $"{Kind}-{Id}.json");

    /// <summary>Writes the record atomically (temp file, then replace). False if it couldn't be written.</summary>
    public bool Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    public static List<Journal> All()
    {
        var list = new List<Journal>();
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return list;
            foreach (var f in System.IO.Directory.GetFiles(Directory, "*.json"))
                try { if (JsonSerializer.Deserialize<Journal>(File.ReadAllText(f), Options) is { } j) list.Add(j); } catch { }
        }
        catch { }
        return list.OrderByDescending(j => j.Created).ToList();
    }

    /// <summary>Keeps the folder small: finished records beyond the newest 20 are removed.</summary>
    public static void Prune()
    {
        foreach (var j in All().Where(j => j.State is JournalState.Undone or JournalState.Dismissed or JournalState.Applied).Skip(20))
            try { File.Delete(j.FilePath); } catch { }
    }
}

/// <summary>What a batch did, for an honest summary and a details list.</summary>
public sealed record BatchResult(int Succeeded, int Skipped, int Conflicted, int Failed, bool NeedsAttention, List<string> Details, Journal? Journal)
{
    public static BatchResult Nothing(string why) => new(0, 0, 0, 1, false, [why], null);

    public string Summary(string verb)
    {
        var parts = new List<string> { $"{verb} {Succeeded} file{(Succeeded == 1 ? "" : "s")}" };
        if (Skipped > 0) parts.Add($"{Skipped} skipped");
        if (Conflicted > 0) parts.Add($"{Conflicted} in the way");
        if (Failed > 0) parts.Add($"{Failed} failed");
        return string.Join(", ", parts) + "." + (NeedsAttention ? " Some files need attention: see Recovery." : "");
    }
}

/// <summary>Applies, undoes and recovers journaled batches of file moves.</summary>
public static class BatchMover
{
    public sealed record Move(string From, string To, FileIdentity? Before);

    /// <summary>Test hook: makes the journal unwritable, to check that nothing moves when it can't be recorded.</summary>
    internal static bool FailJournalWrites;

    static bool Save(Journal j) => !FailJournalWrites && j.Save();

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public static BatchResult Apply(string kind, string folder, IReadOnlyList<Move> moves, bool twoStep, CancellationToken cancel = default)
    {
        if (moves.Count == 0) return new BatchResult(0, 0, 0, 0, false, [], null);
        var j = new Journal
        {
            Kind = kind, Folder = folder, State = JournalState.Applying, TwoStep = twoStep,
            Items = moves.Select(m => new JournalItem { From = m.From, To = m.To, Before = m.Before }).ToList(),
        };
        if (!Save(j)) return BatchResult.Nothing("Couldn't write the recovery record, so nothing was changed.");

        var sources = j.Items.Select(i => i.From).ToList();
        foreach (var item in j.Items)
        {
            if (cancel.IsCancellationRequested) { item.State = ItemState.Skipped; item.Note = "canceled"; continue; }
            if (Check(item, sources) is { } problem) { (item.State, item.Note) = problem; continue; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.To)!);
                if (twoStep)
                {
                    item.Temp = Path.Combine(Path.GetDirectoryName(item.From)!, $".rig-op-{j.Id}-{j.Items.IndexOf(item)}{Path.GetExtension(item.From)}");
                    item.State = ItemState.Staged;
                    if (!Save(j)) { item.State = ItemState.Skipped; item.Note = "couldn't record it"; item.Temp = null; break; }
                    File.Move(item.From, item.Temp);
                }
                else
                {
                    File.Move(item.From, item.To);
                    item.State = ItemState.Done;
                    item.After = FileIdentity.Of(item.To);
                }
            }
            catch (Exception ex) { item.State = ItemState.Failed; item.Note = Reason(ex); if (twoStep) item.Temp = null; }
            Save(j);
        }
        // Second step: staged files go to their new names (or back where they were if the name was taken meanwhile).
        foreach (var item in j.Items.Where(i => i.State == ItemState.Staged)) Finish(item, toNewName: true);
        return Done(j, JournalState.Applied);
    }

    /// <summary>Why an item can't move right now, or null.</summary>
    static (ItemState, string)? Check(JournalItem item, List<string> sources)
    {
        if (!File.Exists(item.From)) return (ItemState.Skipped, "no longer there");
        if (item.Before is not null && !item.Before.Matches(item.From)) return (ItemState.Skipped, "changed since the preview");
        if ((File.Exists(item.To) || Directory.Exists(item.To)) && !SamePath(item.To, item.From) && !sources.Any(s => SamePath(s, item.To)))
            return (ItemState.Conflict, "the new name is taken");
        return null;
    }

    /// <summary>Second step of a two-step move: from the temporary name to the target, or back if that's taken.</summary>
    static void Finish(JournalItem item, bool toNewName)
    {
        var target = toNewName ? item.To : item.From;
        var fallback = toNewName ? item.From : item.To;
        try
        {
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                File.Move(item.Temp!, target);
                item.State = toNewName ? ItemState.Done : ItemState.Undone;
                if (toNewName) item.After = FileIdentity.Of(target);
                item.Temp = null;
            }
            else if (!File.Exists(fallback))
            {
                File.Move(item.Temp!, fallback);
                item.State = toNewName ? ItemState.Conflict : ItemState.Done;
                item.Note = $"{Path.GetFileName(target)} was taken, so it stayed as it was";
                item.Temp = null;
            }
            else item.Note = $"{Path.GetFileName(target)} and {Path.GetFileName(fallback)} are both taken; kept as {Path.GetFileName(item.Temp)}";
        }
        catch (Exception ex) { item.Note = Reason(ex); }
    }

    /// <summary>Puts a batch back. Files changed or replaced since are left alone and reported; the record stays until resolved.</summary>
    public static BatchResult Undo(Journal j, CancellationToken cancel = default)
    {
        j.State = JournalState.Undoing;
        if (!Save(j)) return BatchResult.Nothing("Couldn't write the recovery record, so nothing was changed.");
        var done = j.Items.Where(i => i.State == ItemState.Done).Reverse().ToList();
        foreach (var item in done)
        {
            if (cancel.IsCancellationRequested) break;
            if (!File.Exists(item.To)) { item.State = ItemState.Failed; item.Note = $"{Path.GetFileName(item.To)} is no longer there"; Save(j); continue; }
            if (item.After is not null && !item.After.Matches(item.To)) { item.State = ItemState.Conflict; item.Note = $"{Path.GetFileName(item.To)} changed since, so it wasn't moved"; Save(j); continue; }
            try
            {
                if (j.TwoStep)
                {
                    item.Temp = Path.Combine(Path.GetDirectoryName(item.To)!, $".rig-op-{j.Id}-u{j.Items.IndexOf(item)}{Path.GetExtension(item.To)}");
                    item.State = ItemState.UndoStaged;
                    if (!Save(j)) { item.State = ItemState.Done; item.Temp = null; break; }
                    File.Move(item.To, item.Temp);
                }
                else if (File.Exists(item.From) || Directory.Exists(item.From))
                {
                    item.State = ItemState.Conflict;
                    item.Note = $"another file is now called {Path.GetFileName(item.From)}; this one stayed at {Path.GetFileName(item.To)}";
                }
                else { File.Move(item.To, item.From); item.State = ItemState.Undone; }
            }
            catch (Exception ex) { item.Note = Reason(ex); if (item.State == ItemState.UndoStaged) { item.State = ItemState.Done; item.Temp = null; } }
            Save(j);
        }
        foreach (var item in j.Items.Where(i => i.State == ItemState.UndoStaged))
        {
            Finish(item, toNewName: false);
            // Couldn't go back because something else has the old name: it's back at its new name, reported as a conflict.
            if (item.State == ItemState.Done && item.Note is not null) item.State = ItemState.Conflict;
        }
        if (j.Kind == "downloads") RemoveEmptyFolders(j);
        return Done(j, JournalState.Undone);
    }

    /// <summary>
    /// Finishes what an interrupted batch left half-done and retries conflicts. A file that still can't go to
    /// its place is kept next to it under a "(restored)" name when keepBoth is set.
    /// </summary>
    public static BatchResult Recover(Journal j, bool keepBoth)
    {
        foreach (var item in j.Items)
        {
            try
            {
                switch (item.State)
                {
                    case ItemState.Staged when item.Temp is not null && File.Exists(item.Temp):
                        Finish(item, toNewName: true);
                        break;
                    case ItemState.UndoStaged when item.Temp is not null && File.Exists(item.Temp):
                        Finish(item, toNewName: false);
                        break;
                    case ItemState.Pending when j.State == JournalState.Applying:
                        item.State = ItemState.Skipped; item.Note = "interrupted before it moved";
                        break;
                }
                if (keepBoth && item.Temp is not null && File.Exists(item.Temp))
                {
                    var dest = FreeName(item.From, "restored");
                    File.Move(item.Temp, dest);
                    item.Temp = null; item.State = ItemState.Undone; item.Note = $"kept as {Path.GetFileName(dest)}";
                }
                else if (keepBoth && item.State == ItemState.Conflict && File.Exists(item.To) && j.State is JournalState.Undoing or JournalState.Undone or JournalState.NeedsAttention
                         && (item.After is null || item.After.Matches(item.To)) && File.Exists(item.From))
                {
                    var dest = FreeName(item.From, "restored");
                    File.Move(item.To, dest);
                    item.State = ItemState.Undone; item.Note = $"restored as {Path.GetFileName(dest)}";
                }
            }
            catch (Exception ex) { item.Note = Reason(ex); }
            Save(j);
        }
        bool undoing = j.Items.Any(i => i.State is ItemState.Undone or ItemState.UndoStaged);
        return Done(j, undoing ? JournalState.Undone : JournalState.Applied);
    }

    /// <summary>Accepts things as they are and closes the record (files stay where they are now).</summary>
    public static void Dismiss(Journal j)
    {
        if (j.Items.Any(i => i.Temp is not null && File.Exists(i.Temp))) return; // never forget a file under a temporary name
        j.State = JournalState.Dismissed;
        Save(j);
    }

    /// <summary>Records left mid-way by a crash or power cut are flagged so the recovery list shows them.</summary>
    public static int FlagInterrupted()
    {
        int n = 0;
        foreach (var j in Journal.All().Where(j => j.State is JournalState.Applying or JournalState.Undoing))
        {
            j.State = JournalState.NeedsAttention;
            if (Save(j)) n++;
        }
        return n;
    }

    public static List<Journal> NeedingAttention() => Journal.All().Where(j => j.State == JournalState.NeedsAttention).ToList();

    /// <summary>The batch Undo would act on: the newest of this kind that still has moved files.</summary>
    public static Journal? Latest(string kind) => Journal.All().FirstOrDefault(j => j.Kind == kind && j.CanUndo);

    static BatchResult Done(Journal j, JournalState finished)
    {
        bool open = j.Items.Any(i => i.State is ItemState.Staged or ItemState.UndoStaged or ItemState.Pending || i.Temp is not null && File.Exists(i.Temp));
        bool problems = j.Items.Any(i => i.State is ItemState.Conflict or ItemState.Failed);
        j.State = open || (problems && finished == JournalState.Undone) ? JournalState.NeedsAttention : finished;
        Save(j);
        Journal.Prune();
        var target = finished == JournalState.Undone ? ItemState.Undone : ItemState.Done;
        var details = j.Items.Where(i => i.Note is not null).Select(i => $"{i.Name}: {i.Note}").ToList();
        return new BatchResult(j.Items.Count(i => i.State == target), j.Items.Count(i => i.State == ItemState.Skipped),
            j.Items.Count(i => i.State == ItemState.Conflict), j.Items.Count(i => i.State == ItemState.Failed),
            j.State == JournalState.NeedsAttention, details, j);
    }

    static void RemoveEmptyFolders(Journal j)
    {
        foreach (var dir in j.Items.Select(i => Path.GetDirectoryName(i.To)!).Distinct(StringComparer.OrdinalIgnoreCase))
            try { if (!SamePath(dir, j.Folder) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
    }

    public static string FreeName(string path, string tag)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var candidate = Path.Combine(dir, $"{stem} ({tag}){ext}");
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++) candidate = Path.Combine(dir, $"{stem} ({tag} {n}){ext}");
        return candidate;
    }

    static string Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        IOException io when io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) => "in use by another program",
        FileNotFoundException => "no longer there",
        _ => ex.Message,
    };
}
