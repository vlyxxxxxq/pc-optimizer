using Rig.Services;

namespace Rig.Tests;

/// <summary>The four findings from the review, as written, before any fix.</summary>
public class ReproTests
{
    [Fact]
    public void Rename_undo_with_a_new_file_at_the_old_name_keeps_both_files_and_the_record()
    {
        using var box = new Sandbox();
        var dir = box.Dir("files");
        box.Write(Path.Combine(dir, "original.txt"), "ORIGINAL");
        var plan = Renamer.Plan(dir, "*", new RenameRules { Find = "original", Replace = "renamed" });
        Assert.Single(plan, r => r.Changes);
        Renamer.Apply(plan);
        box.Write(Path.Combine(dir, "original.txt"), "NEW FILE");

        Renamer.Undo();

        var contents = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        Assert.Contains("ORIGINAL", contents);
        Assert.Contains("NEW FILE", contents);
        // Nothing left stranded under a temporary name, or else the record to recover it is still there.
        bool stranded = Directory.GetFiles(dir).Any(f => Path.GetFileName(f).StartsWith(".rig-rename-"));
        Assert.True(!stranded || Renamer.CanUndo, "the original was left under a temporary name and the undo record was deleted");
    }

    [Fact]
    public void Downloads_undo_with_a_new_file_at_the_old_place_keeps_the_record()
    {
        using var box = new Sandbox();
        var downloads = box.Dir("Downloads");
        box.Env("RIG_TEST_DOWNLOADS", downloads);
        box.Aged(Path.Combine(downloads, "report.pdf"), "ORGANIZED");
        DownloadOrganizer.Apply(DownloadOrganizer.Plan());
        Assert.True(File.Exists(Path.Combine(downloads, "Documents", "report.pdf")));
        box.Write(Path.Combine(downloads, "report.pdf"), "NEW DOWNLOAD");

        DownloadOrganizer.Undo();

        // The organized copy couldn't go back; it must still be undoable/recoverable later.
        Assert.True(File.Exists(Path.Combine(downloads, "Documents", "report.pdf")));
        Assert.True(BatchMover.NeedingAttention().Count == 1, "the recovery record was discarded although the file wasn't restored");
    }

    [Fact]
    public void Different_flat_colours_are_not_offered_for_deletion()
    {
        using var box = new Sandbox();
        var dir = box.Dir("photos");
        foreach (var (name, color) in new[] { ("red", System.Drawing.Color.Red), ("green", System.Drawing.Color.Lime), ("blue", System.Drawing.Color.Blue) })
        {
            using var bmp = new System.Drawing.Bitmap(200, 200);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(color);
            bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        var result = SimilarPhotos.Find(dir, false, null, CancellationToken.None);
        Assert.DoesNotContain(result.Groups.SelectMany(g => g.Photos), p => p.Remove);
    }

    [Fact]
    public void Backup_import_carries_newer_preferences()
    {
        using var box = new Sandbox();
        var source = new AppSettings
        {
            Theme = "Light", Accent = "#9B6BFF", HomeCards = ["health"], DisabledModules = ["Crosshair"],
            ScreenTimeEnabled = false, ClipboardHistoryEnabled = false, ClipHotkey = "Ctrl+Alt+F1",
        };
        var target = new AppSettings();
        target.CopyFrom(source);
        Assert.Equal("Light", target.Theme);
        Assert.Equal("#9B6BFF", target.Accent);
        Assert.Equal(["health"], target.HomeCards);
        Assert.Equal(["Crosshair"], target.DisabledModules);
        Assert.False(target.ScreenTimeEnabled);
        Assert.False(target.ClipboardHistoryEnabled);
        Assert.Equal("Ctrl+Alt+F1", target.ClipHotkey);
    }

    [Fact]
    public void Timers_are_part_of_the_personal_data_backup()
    {
        Assert.Contains("timers.json", AppSettings.DataFiles);
    }

    [Fact]
    public void Explicit_null_collections_are_normalized_on_load()
    {
        using var box = new Sandbox();
        File.WriteAllText(Path.Combine(box.Profile, "settings.json"), """{ "HomeCards": null, "DisabledModules": null, "CustomGames": null }""");
        var s = AppSettings.Load();
        Assert.NotNull(s.HomeCards);
        Assert.NotNull(s.DisabledModules);
        Assert.NotNull(s.CustomGames);
    }
}
