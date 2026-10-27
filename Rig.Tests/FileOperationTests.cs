using Rig.Services;

namespace Rig.Tests;

/// <summary>Renames and Downloads sorts: collisions, swaps, locks, interruptions and recovery.</summary>
public class FileOperationTests
{
    static List<string> Contents(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText).OrderBy(x => x).ToList();

    static RenameRules Replace(string find, string with) => new() { Find = find, Replace = with };

    [Fact]
    public void Rename_and_undo_round_trip()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "a.txt"), "A");
        box.Write(Path.Combine(dir, "b.txt"), "B");
        var r = Renamer.Apply(Renamer.Plan(dir, "*", new RenameRules { Prefix = "x-" }));
        Assert.Equal(2, r.Succeeded);
        Assert.True(File.Exists(Path.Combine(dir, "x-a.txt")));
        var u = Renamer.Undo();
        Assert.Equal(2, u.Succeeded);
        Assert.Equal("A", File.ReadAllText(Path.Combine(dir, "a.txt")));
        Assert.False(Renamer.CanUndo);
    }

    [Fact]
    public void Names_can_swap()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "one.txt"), "1");
        box.Write(Path.Combine(dir, "two.txt"), "2");
        // "one"→"two" and "two"→"one" via find/replace on a common pattern: use numbering instead to force a swap-like plan.
        var rows = new List<RenameRow>
        {
            new(dir, "one.txt", "two.txt", "", FileIdentity.Of(Path.Combine(dir, "one.txt"))),
            new(dir, "two.txt", "one.txt", "", FileIdentity.Of(Path.Combine(dir, "two.txt"))),
        };
        var r = Renamer.Apply(rows);
        Assert.Equal(2, r.Succeeded);
        Assert.Equal("1", File.ReadAllText(Path.Combine(dir, "two.txt")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(dir, "one.txt")));
        Renamer.Undo();
        Assert.Equal("1", File.ReadAllText(Path.Combine(dir, "one.txt")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(dir, "two.txt")));
    }

    [Fact]
    public void Case_only_rename_works()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "Photo.JPG"), "P");
        var r = Renamer.Apply(Renamer.Plan(dir, "*", new RenameRules { LowerExtension = true, Case = "lower" }));
        Assert.Equal(1, r.Succeeded);
        Assert.Contains("photo.jpg", Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_created_at_the_new_name_after_the_preview_is_not_overwritten()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "a.txt"), "A");
        var plan = Renamer.Plan(dir, "*", Replace("a", "b"));
        box.Write(Path.Combine(dir, "b.txt"), "SOMEONE ELSE");
        var r = Renamer.Apply(plan);
        Assert.Equal(0, r.Succeeded);
        Assert.Equal(1, r.Conflicted);
        Assert.Equal(["A", "SOMEONE ELSE"], Contents(dir));
    }

    [Fact]
    public void A_file_changed_after_the_preview_is_skipped()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        var f = box.Write(Path.Combine(dir, "a.txt"), "A");
        var plan = Renamer.Plan(dir, "*", Replace("a", "b"));
        File.WriteAllText(f, "CHANGED, and longer");
        var r = Renamer.Apply(plan);
        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void A_missing_file_is_skipped_and_the_rest_continue()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "a.txt"), "A");
        box.Write(Path.Combine(dir, "b.txt"), "B");
        var plan = Renamer.Plan(dir, "*", new RenameRules { Prefix = "x-" });
        File.Delete(Path.Combine(dir, "a.txt"));
        var r = Renamer.Apply(plan);
        Assert.Equal(1, r.Succeeded);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public void A_locked_file_is_reported_and_left_in_place()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        var a = box.Write(Path.Combine(dir, "a.txt"), "A");
        box.Write(Path.Combine(dir, "b.txt"), "B");
        var plan = Renamer.Plan(dir, "*", new RenameRules { Prefix = "x-" });
        BatchResult r;
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None)) r = Renamer.Apply(plan);
        Assert.Equal(1, r.Succeeded);
        Assert.Equal(1, r.Failed + r.Skipped);
        Assert.True(File.Exists(a));
        Assert.Empty(Directory.GetFiles(dir, ".rig-op-*"));
    }

    [Fact]
    public void Nothing_moves_when_the_journal_cannot_be_written()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "a.txt"), "A");
        BatchMover.FailJournalWrites = true;
        try
        {
            var r = Renamer.Apply(Renamer.Plan(dir, "*", Replace("a", "b")));
            Assert.Equal(0, r.Succeeded);
            Assert.True(File.Exists(Path.Combine(dir, "a.txt")));
        }
        finally { BatchMover.FailJournalWrites = false; }
    }

    [Fact]
    public void Failed_rename_undo_keeps_both_files_and_can_be_finished_later()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "original.txt"), "ORIGINAL");
        Renamer.Apply(Renamer.Plan(dir, "*", Replace("original", "renamed")));
        box.Write(Path.Combine(dir, "original.txt"), "NEW FILE");

        var u = Renamer.Undo();
        Assert.Equal(1, u.Conflicted);
        Assert.True(u.NeedsAttention);
        Assert.Equal(["NEW FILE", "ORIGINAL"], Contents(dir));
        Assert.Empty(Directory.GetFiles(dir, ".rig-op-*"));

        // Later: keep both side by side.
        var j = Assert.Single(BatchMover.NeedingAttention());
        var rec = BatchMover.Recover(j, keepBoth: true);
        Assert.False(rec.NeedsAttention);
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(dir, "original (restored).txt")));
        Assert.Equal("NEW FILE", File.ReadAllText(Path.Combine(dir, "original.txt")));
    }

    [Fact]
    public void Undo_leaves_a_file_that_changed_since_and_reports_it()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "a.txt"), "A");
        Renamer.Apply(Renamer.Plan(dir, "*", Replace("a", "b")));
        File.WriteAllText(Path.Combine(dir, "b.txt"), "EDITED AFTER RENAMING");
        var u = Renamer.Undo();
        Assert.Equal(1, u.Conflicted);
        Assert.Equal("EDITED AFTER RENAMING", File.ReadAllText(Path.Combine(dir, "b.txt")));
    }

    [Fact]
    public void An_interrupted_batch_is_flagged_and_recovered_after_restart()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        // As if the app died after staging the file: it sits under a temporary name, journal says "Applying".
        var temp = box.Write(Path.Combine(dir, ".rig-op-crash-0.txt"), "MID-MOVE");
        var j = new Journal
        {
            Kind = "rename", Folder = dir, State = JournalState.Applying, TwoStep = true,
            Items = [new JournalItem { From = Path.Combine(dir, "a.txt"), To = Path.Combine(dir, "b.txt"), Temp = temp, State = ItemState.Staged }],
        };
        Assert.True(j.Save());

        Assert.Equal(1, BatchMover.FlagInterrupted());
        var open = Assert.Single(BatchMover.NeedingAttention());
        var r = BatchMover.Recover(open, keepBoth: false);
        Assert.False(r.NeedsAttention);
        Assert.Equal("MID-MOVE", File.ReadAllText(Path.Combine(dir, "b.txt")));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void A_new_batch_never_replaces_an_unfinished_record()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "original.txt"), "ORIGINAL");
        Renamer.Apply(Renamer.Plan(dir, "*", Replace("original", "renamed")));
        box.Write(Path.Combine(dir, "original.txt"), "NEW FILE");
        Renamer.Undo(); // leaves a conflict
        box.Write(Path.Combine(dir, "c.txt"), "C");
        Renamer.Apply(Renamer.Plan(dir, "c.txt", Replace("c", "d")));
        Assert.Single(BatchMover.NeedingAttention());
    }

    [Fact]
    public void Plan_rejects_reserved_and_invalid_names_and_names_used_outside_the_filter()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        box.Write(Path.Combine(dir, "a.txt"), "A");
        box.Write(Path.Combine(dir, "keep.md"), "K");
        Assert.Equal("reserved by Windows", Renamer.Plan(dir, "*.txt", new RenameRules { Find = "a", Replace = "CON" }).Single().Status);
        Assert.Equal("can't end with a dot or space", Renamer.Problem("name."));
        // The filter only shows .txt, but keep.md is still in the folder.
        var rows = Renamer.Plan(dir, "*.txt", new RenameRules { Find = "a.txt", Replace = "keep.md" });
        Assert.Equal("", Renamer.Problem("x.txt"));
        Directory.CreateDirectory(Path.Combine(dir, "folder.txt"));
        var clash = Renamer.Plan(dir, "a.txt", new RenameRules { Find = "a", Replace = "folder" }).Single();
        Assert.Equal("name already used", clash.Status);
    }

    [Fact]
    public void Old_style_undo_records_still_undo()
    {
        using var box = new Sandbox();
        var dir = box.Dir("f");
        var to = box.Write(Path.Combine(dir, "new.txt"), "X");
        File.WriteAllText(Path.Combine(box.Profile, "rename-undo.json"),
            System.Text.Json.JsonSerializer.Serialize(new List<string[]> { new[] { Path.Combine(dir, "old.txt"), to } }));
        Assert.True(Renamer.CanUndo);
        Assert.Equal(1, Renamer.Undo().Succeeded);
        Assert.True(File.Exists(Path.Combine(dir, "old.txt")));
        Assert.False(File.Exists(Path.Combine(box.Profile, "rename-undo.json")));
    }

    // ---- Downloads ----

    static string Downloads(Sandbox box)
    {
        var d = box.Dir("Downloads");
        box.Env("RIG_TEST_DOWNLOADS", d);
        return d;
    }

    [Fact]
    public void Downloads_sort_and_undo_round_trip()
    {
        using var box = new Sandbox();
        var d = Downloads(box);
        box.Aged(Path.Combine(d, "a.pdf"), "A");
        box.Aged(Path.Combine(d, "b.zip"), "B");
        Assert.Equal(2, DownloadOrganizer.Apply(DownloadOrganizer.Plan()).Succeeded);
        Assert.Equal(2, DownloadOrganizer.Undo().Succeeded);
        Assert.True(File.Exists(Path.Combine(d, "a.pdf")));
        Assert.False(Directory.Exists(Path.Combine(d, "Documents")));
    }

    [Fact]
    public void Downloads_undo_conflict_keeps_both_and_the_record()
    {
        using var box = new Sandbox();
        var d = Downloads(box);
        box.Aged(Path.Combine(d, "report.pdf"), "ORGANIZED");
        DownloadOrganizer.Apply(DownloadOrganizer.Plan());
        box.Write(Path.Combine(d, "report.pdf"), "NEW DOWNLOAD");

        var u = DownloadOrganizer.Undo();
        Assert.Equal(1, u.Conflicted);
        Assert.Equal("ORGANIZED", File.ReadAllText(Path.Combine(d, "Documents", "report.pdf")));
        Assert.Equal("NEW DOWNLOAD", File.ReadAllText(Path.Combine(d, "report.pdf")));
        var j = Assert.Single(BatchMover.NeedingAttention());

        BatchMover.Recover(j, keepBoth: true);
        Assert.Equal("ORGANIZED", File.ReadAllText(Path.Combine(d, "report (restored).pdf")));
        Assert.Empty(BatchMover.NeedingAttention());
    }

    [Fact]
    public void Downloads_destination_taken_after_preview_is_not_overwritten()
    {
        using var box = new Sandbox();
        var d = Downloads(box);
        box.Aged(Path.Combine(d, "a.pdf"), "A");
        var plan = DownloadOrganizer.Plan();
        box.Write(Path.Combine(d, "Documents", "a.pdf"), "ALREADY THERE");
        var r = DownloadOrganizer.Apply(plan);
        Assert.Equal(1, r.Conflicted);
        Assert.Equal("ALREADY THERE", File.ReadAllText(Path.Combine(d, "Documents", "a.pdf")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(d, "a.pdf")));
    }
}
