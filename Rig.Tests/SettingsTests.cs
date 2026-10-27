using System.Text.Json;
using Rig.Services;

namespace Rig.Tests;

public class SettingsTests
{
    [Fact]
    public void Every_setting_has_a_transfer_rule()
    {
        var missing = SettingsPolicy.Properties().Select(p => p.Name).Where(n => !SettingsPolicy.Fields.ContainsKey(n)).ToList();
        Assert.True(missing.Count == 0, "Add these to SettingsPolicy.Fields: " + string.Join(", ", missing));
    }

    [Fact]
    public void Import_carries_preferences_but_not_this_pcs_state()
    {
        var source = new AppSettings
        {
            Theme = "Light", Accent = "#9B6BFF", HomeCards = ["health"], DisabledModules = ["Crosshair"], ClipHotkey = "Ctrl+Alt+F1",
            MaintenanceEnabled = true, SeenGiveaways = [1, 2], Recovery = new GameModeRecovery { PowerPlan = "x" },
        };
        var target = new AppSettings();
        SettingsPolicy.Apply(source, target, new ImportChoices());
        Assert.Equal("Light", target.Theme);
        Assert.Equal("#9B6BFF", target.Accent);
        Assert.Equal(["health"], target.HomeCards);
        Assert.Equal(["Crosshair"], target.DisabledModules);
        Assert.Equal("Ctrl+Alt+F1", target.ClipHotkey);
        Assert.False(target.MaintenanceEnabled);
        Assert.Empty(target.SeenGiveaways);
        Assert.False(target.Recovery.Pending);
    }

    [Fact]
    public void Import_can_switch_tracking_off_but_not_on_without_consent()
    {
        var shared = new AppSettings { ScreenTimeEnabled = false, ClipboardHistoryEnabled = true, InputStatsEnabled = true };
        var mine = new AppSettings { ScreenTimeEnabled = true, ClipboardHistoryEnabled = false, InputStatsEnabled = false };
        var notes = SettingsPolicy.Apply(shared, mine, new ImportChoices(AllowTrackingOn: false));
        Assert.False(mine.ScreenTimeEnabled);       // turned off: always fine
        Assert.False(mine.ClipboardHistoryEnabled); // not turned on silently
        Assert.False(mine.InputStatsEnabled);
        Assert.Contains(notes, n => n.Contains("Clipboard history"));

        SettingsPolicy.Apply(shared, mine, new ImportChoices(AllowTrackingOn: true));
        Assert.True(mine.ClipboardHistoryEnabled);
        Assert.True(mine.InputStatsEnabled);
    }

    [Fact]
    public void Personal_history_only_with_consent()
    {
        var source = new AppSettings { ColorHistory = ["#112233"] };
        var a = new AppSettings();
        SettingsPolicy.Apply(source, a, new ImportChoices());
        Assert.Empty(a.ColorHistory);
        SettingsPolicy.Apply(source, a, new ImportChoices(PersonalData: true));
        Assert.Equal(["#112233"], a.ColorHistory);
    }

    [Fact]
    public void Full_backup_round_trip_with_personal_data_and_timers()
    {
        using var box = new Sandbox();
        File.WriteAllText(AppSettings.DataFile("timers.json"), "[{\"Kind\":\"reminder\",\"Text\":\"stretch\"}]");
        File.WriteAllText(AppSettings.DataFile("notes.txt"), "my notes");
        var mine = new AppSettings { Theme = "Light", GameProfiles = [new GameProfile { Game = "Valorant" }], ColorHistory = ["#ABCDEF"] };
        var file = Path.Combine(box.Root, "setup.pcopt");
        File.WriteAllText(file, JsonSerializer.Serialize(Backup.Create(mine, ["Tweak A"], ["App.Id"], includeData: true)));

        File.Delete(AppSettings.DataFile("timers.json"));
        File.Delete(AppSettings.DataFile("notes.txt"));
        var fresh = new AppSettings();
        var backup = Backup.Read(file);
        var result = Backup.Apply(backup, fresh, new ImportChoices(PersonalData: true, Tweaks: false), applyTweaks: false);

        Assert.True(result.SettingsSaved);
        Assert.Equal("Light", fresh.Theme);
        Assert.Equal("Valorant", Assert.Single(fresh.GameProfiles).Game);
        Assert.Equal(["#ABCDEF"], fresh.ColorHistory);
        Assert.Contains("timers.json", result.DataRestored);
        Assert.Contains("stretch", File.ReadAllText(AppSettings.DataFile("timers.json")));
        Assert.NotNull(result.SafetyCopy);
        Assert.True(File.Exists(result.SafetyCopy));
    }

    [Fact]
    public void Settings_only_import_leaves_personal_data_alone()
    {
        using var box = new Sandbox();
        File.WriteAllText(AppSettings.DataFile("notes.txt"), "mine");
        var other = new AppSettings();
        var file = Path.Combine(box.Root, "friend.pcopt");
        var shared = Backup.Create(other, [], [], includeData: false);
        shared.Data["notes.txt"] = "theirs"; // even if a file carries data, it's only used when chosen
        File.WriteAllText(file, JsonSerializer.Serialize(shared));
        Backup.Apply(Backup.Read(file), new AppSettings(), new ImportChoices(PersonalData: false), applyTweaks: false);
        Assert.Equal("mine", File.ReadAllText(AppSettings.DataFile("notes.txt")));
    }

    [Fact]
    public void Old_backups_still_import()
    {
        using var box = new Sandbox();
        var file = Path.Combine(box.Root, "old.pcopt");
        File.WriteAllText(file, """{ "Format": 1, "Version": "1.9.0", "Settings": { "AutoGameMode": false, "CustomGames": ["game.exe"] }, "Tweaks": ["X"], "Apps": [] }""");
        var b = Backup.Read(file);
        var s = new AppSettings();
        Backup.Apply(b, s, new ImportChoices(Tweaks: false), applyTweaks: false);
        Assert.False(s.AutoGameMode);
        Assert.Equal(["game.exe"], s.CustomGames);
    }

    [Fact]
    public void Future_malformed_and_oversized_backups_are_refused_with_a_reason()
    {
        using var box = new Sandbox();
        var future = box.Write("future.pcopt", """{ "Format": 99, "Version": "9.0.0", "Settings": {} }""");
        Assert.Contains("newer Rig", Assert.Throws<InvalidDataException>(() => Backup.Read(future)).Message);
        var junk = box.Write("junk.pcopt", "{ not json");
        Assert.Contains("isn't a Rig setup", Assert.Throws<InvalidDataException>(() => Backup.Read(junk)).Message);
        var big = Path.Combine(box.Root, "big.pcopt");
        using (var f = File.Create(big)) f.SetLength(Backup.MaxSize + 1);
        Assert.Contains("never that big", Assert.Throws<InvalidDataException>(() => Backup.Read(big)).Message);
        var nul = box.Write("null.pcopt", """{ "Format": 2, "Settings": null }""");
        Assert.Throws<InvalidDataException>(() => Backup.Read(nul));
    }

    [Fact]
    public void Nulls_and_out_of_range_values_are_repaired()
    {
        using var box = new Sandbox();
        File.WriteAllText(Path.Combine(box.Profile, "settings.json"),
            """{ "HomeCards": null, "GameProfiles": [null, { "Game": null }], "Crosshair": null, "Theme": "Neon", "Accent": "blue", "BreakMinutes": 1, "ClipHotkey": "Banana", "DisabledModules": ["Game", "Nope", "Input"] }""");
        var s = AppSettings.Load();
        Assert.NotNull(s.HomeCards);
        Assert.Equal("", Assert.Single(s.GameProfiles).Game);
        Assert.NotNull(s.Crosshair);
        Assert.Equal("Dark", s.Theme);
        Assert.Null(s.Accent);
        Assert.Equal(15, s.BreakMinutes);
        Assert.Equal("Ctrl+Shift+F9", s.ClipHotkey);
        Assert.Equal(["Input"], s.DisabledModules); // core and unknown modules can't be switched off
    }

    [Fact]
    public void A_corrupt_settings_file_is_kept_and_the_last_good_copy_used()
    {
        using var box = new Sandbox();
        var s = new AppSettings { Theme = "Light" };
        Assert.True(s.Save());
        s.Theme = "Dark";
        Assert.True(s.Save()); // previous (Light) becomes settings.json.bak... then corrupt the main file
        File.WriteAllText(Path.Combine(box.Profile, "settings.json"), "{ broken");
        var loaded = AppSettings.Load();
        Assert.NotNull(loaded.LoadProblem);
        Assert.Equal("Light", loaded.Theme);
        Assert.Single(Directory.GetFiles(box.Profile, "settings.corrupt-*.json"));
    }

    [Fact]
    public void A_failed_save_is_reported()
    {
        using var box = new Sandbox();
        var blocker = Path.Combine(box.Root, "blocked");
        Directory.CreateDirectory(Path.Combine(blocker, "settings.json")); // a folder where the file should go
        box.Env("RIG_SETTINGS", Path.Combine(blocker, "settings.json"));
        string? message = null;
        void Handler(string m) => message = m;
        AppSettings.SaveFailed += Handler;
        try { Assert.False(new AppSettings().Save()); }
        finally { AppSettings.SaveFailed -= Handler; }
        Assert.NotNull(message);
    }

    [Fact]
    public void Maintenance_results_never_overwrite_newer_choices()
    {
        using var box = new Sandbox();
        var app = new AppSettings { Theme = "Dark" };
        app.Save();
        // The app changes a preference while maintenance (another process) is running with an older copy.
        app.Theme = "Light";
        app.Save();
        AppSettings.Update(s => { s.LastMaintenance = new DateTime(2030, 1, 1); s.LastMaintenanceSummary = "done"; });
        var after = AppSettings.Load();
        Assert.Equal("Light", after.Theme);
        Assert.Equal("done", after.LastMaintenanceSummary);
        // And the app's next save keeps maintenance's newer result.
        app.Save();
        Assert.Equal("done", AppSettings.Load().LastMaintenanceSummary);
    }
}
