using Rig.Services;

namespace Rig.Tests;

public class WorkspacePresentationTests
{
    [Theory]
    [InlineData(GameModeState.Starting, true)]
    [InlineData(GameModeState.Restoring, true)]
    [InlineData(GameModeState.Off, false)]
    [InlineData(GameModeState.On, false)]
    [InlineData(GameModeState.RecoveryNeeded, false)]
    public void Only_transitions_block_actions(GameModeState state, bool busy) =>
        Assert.Equal(busy, GameModeSummary.Create(state, true).Busy);

    [Fact]
    public void Recovery_is_an_attention_state_with_a_recovery_action()
    {
        var summary = GameModeSummary.Create(GameModeState.RecoveryNeeded, true);
        Assert.Equal(StatusTone.Attention, summary.Tone);
        Assert.Equal("Review recovery", summary.Action);
    }

    [Fact]
    public void Unavailable_checks_cannot_appear_all_clear()
    {
        var summary = MaintenanceOverview.From(new(100, [new(Severity.Unknown, "Unavailable", "Couldn't check")]));
        Assert.True(summary.Attention);
        Assert.Equal(1, summary.Unknown);
        Assert.Contains("couldn't", summary.Title);
        Assert.Equal("No checks available", MaintenanceOverview.From(new(100, [])).Title);
        Assert.True(MaintenanceOverview.From(new(100, [])).Attention);
    }

    [Fact]
    public void Only_the_exact_factory_layout_migrates()
    {
        Assert.Equal(HomeLayout.Default, HomeLayout.Upgrade(HomeLayout.LegacyDefault));
        string[] custom = ["music", "health", "pins"];
        Assert.Equal(custom, HomeLayout.Upgrade(custom));
        Assert.Empty(HomeLayout.Upgrade([]));
    }

    [Fact]
    public void Recent_tools_are_unique_bounded_and_respect_disabled_modules()
    {
        var recent = new RecentTools();
        foreach (var id in new[] { "Dashboard", "Settings", "Cleaner", "Disk", "Timers", "Clipboard", "Game", "Disk" }) recent.Visit(id);
        Assert.Equal(new[] { "Disk", "Game", "Clipboard", "Timers" }, recent.Available(_ => true).Select(m => m.Id));
        Assert.DoesNotContain(recent.Available(id => id != "Disk"), m => m.Id == "Disk");
    }

    [Fact]
    public void Profiles_match_the_installation_with_folder_boundaries_and_legacy_fallback()
    {
        var legacy = new GameProfile { Game = "Example" };
        var steam = new GameProfile { Game = "Example", MatchPath = @"C:\Steam\Example" };
        var epic = new GameProfile { Game = "Example", MatchPath = "C:/Epic/Example/" };
        var nested = new GameProfile { Game = "Example", MatchPath = @"C:\Steam\Example\edition" };
        var settings = new AppSettings { GameProfiles = [legacy, steam, epic, nested] };
        Assert.Same(steam, settings.ProfileFor("Other detected name", @"c:\steam\example\game.exe"));
        Assert.Same(epic, settings.ProfileFor("Example", @"C:\Epic\Example\game.exe"));
        Assert.Same(nested, settings.ProfileFor("Example", @"C:\Steam\Example\edition\game.exe"));
        Assert.Same(legacy, settings.ProfileFor("Example", @"C:\Steam\Example2\game.exe"));
        Assert.Same(legacy, settings.ProfileFor("Example"));
        Assert.Null(settings.ProfileFor("Other", @"C:\Other\game.exe"));
    }
}
