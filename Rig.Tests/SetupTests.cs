using Rig.Services;

namespace Rig.Tests;

public class SetupTests
{
    static readonly SetupOptions Choices = new(true, false, true, true, false, false, true);

    sealed class FakeSetup : ISetupActions
    {
        public List<string> Calls { get; } = [];
        public bool FailInstall, FailStartup, FailSave;
        public bool StartupEnabled, StartupInstalled, DesktopShortcut;
        public Task InstallAsync(bool desktopShortcut, IProgress<SetupProgress>? progress)
        {
            Calls.Add("install");
            DesktopShortcut = desktopShortcut;
            if (FailInstall) throw new IOException("The destination is locked.");
            return Task.CompletedTask;
        }
        public void SetStartup(bool enabled, bool installed)
        {
            Calls.Add("startup");
            StartupEnabled = enabled;
            StartupInstalled = installed;
            if (FailStartup) throw new UnauthorizedAccessException("Access denied.");
        }
        public bool Save(AppSettings settings) { Calls.Add("save"); return !FailSave; }
    }

    [Fact]
    public async Task Install_finishes_only_after_startup_and_preferences_succeed()
    {
        var actions = new FakeSetup();
        var settings = new AppSettings();
        var result = await Setup.ApplyAsync(settings, Choices with { DesktopShortcut = true }, false, actions);
        Assert.True(result.Succeeded);
        Assert.True(result.Installed);
        Assert.Equal(["install", "startup", "save"], actions.Calls);
        Assert.True(actions.DesktopShortcut);
        Assert.True(actions.StartupInstalled);
        Assert.False(settings.ClipboardHistoryEnabled);
        Assert.False(settings.ScreenTimeEnabled);
    }

    [Fact]
    public async Task Portable_does_not_install_and_preserves_explicit_startup_choice()
    {
        var actions = new FakeSetup();
        var result = await Setup.ApplyAsync(new(), Choices with { Install = false, StartWithWindows = false }, false, actions);
        Assert.True(result.Succeeded);
        Assert.False(result.Installed);
        Assert.Equal(["startup", "save"], actions.Calls);
        Assert.False(actions.StartupEnabled);
        Assert.False(actions.StartupInstalled);
    }

    [Fact]
    public async Task Already_installed_copy_is_not_copied_again()
    {
        var actions = new FakeSetup();
        var result = await Setup.ApplyAsync(new(), Choices, true, actions);
        Assert.True(result.Succeeded);
        Assert.True(result.Installed);
        Assert.Equal(["startup", "save"], actions.Calls);
        Assert.True(actions.StartupInstalled);
    }

    [Fact]
    public async Task Install_failure_does_not_write_startup_or_preferences()
    {
        var actions = new FakeSetup { FailInstall = true };
        var result = await Setup.ApplyAsync(new(), Choices, false, actions);
        Assert.False(result.Succeeded);
        Assert.False(result.Installed);
        Assert.Equal(["install"], actions.Calls);
        Assert.Contains("destination is locked", result.Error);
    }

    [Fact]
    public async Task Startup_failure_is_not_reported_as_setup_success()
    {
        var actions = new FakeSetup { FailStartup = true };
        var result = await Setup.ApplyAsync(new(), Choices, false, actions);
        Assert.False(result.Succeeded);
        Assert.True(result.Installed); // can retry preferences without copying the app again
        Assert.Equal(["install", "startup"], actions.Calls);
        Assert.Contains("startup", result.Error);
    }

    [Fact]
    public async Task Save_failure_does_not_allow_successful_completion()
    {
        var actions = new FakeSetup { FailSave = true };
        var result = await Setup.ApplyAsync(new(), Choices, false, actions);
        Assert.False(result.Succeeded);
        Assert.Contains("save your preferences", result.Error);
    }

    [Fact]
    public async Task Optional_tools_and_manual_game_mode_are_applied_exactly_as_chosen()
    {
        var settings = new AppSettings();
        var result = await Setup.ApplyAsync(settings, Choices with
        {
            Install = false, AutomaticGameMode = false, ClipboardHistory = true, ScreenTime = true, Notifications = false,
        }, false, new FakeSetup());
        Assert.True(result.Succeeded);
        Assert.False(settings.AutoGameMode);
        Assert.True(settings.ClipboardHistoryEnabled);
        Assert.True(settings.ScreenTimeEnabled);
        Assert.False(settings.Notifications);
    }

    [Fact]
    public async Task Payload_copy_replaces_the_app_without_leaving_staged_files()
    {
        using var box = new Sandbox();
        var source = box.Write(Path.Combine(box.Dir("download"), "Rig.exe"), "NEW APP");
        var target = box.Write(Path.Combine(box.Dir("installed"), "Rig.exe"), "OLD APP");
        await Installer.CopyPayloadAsync(source, target);
        Assert.Equal("NEW APP", File.ReadAllText(target));
        Assert.Equal("NEW APP", File.ReadAllText(source));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task Development_install_includes_assembly_and_runtime_files()
    {
        using var box = new Sandbox();
        var source = box.Write(Path.Combine(box.Dir("build"), "Rig.exe"), "launcher");
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            box.Write(Path.ChangeExtension(source, suffix), suffix);
        var target = Path.Combine(box.Dir("installed"), "Rig.exe");
        await Installer.CopyPayloadAsync(source, target);
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            Assert.Equal(suffix, File.ReadAllText(Path.ChangeExtension(target, suffix)));
        Assert.Equal("launcher", File.ReadAllText(target));
        Assert.Equal(4, Directory.GetFiles(Path.GetDirectoryName(target)!).Length);
    }

    [Fact]
    public async Task Incomplete_development_build_keeps_the_installed_app_intact()
    {
        using var box = new Sandbox();
        var source = box.Write(Path.Combine(box.Dir("build"), "Rig.exe"), "NEW");
        box.Write(Path.ChangeExtension(source, ".dll"), "assembly");
        var target = box.Write(Path.Combine(box.Dir("installed"), "Rig.exe"), "OLD");
        await Assert.ThrowsAsync<FileNotFoundException>(() => Installer.CopyPayloadAsync(source, target));
        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task Canceled_copy_keeps_the_installed_app_intact()
    {
        using var box = new Sandbox();
        var source = box.Write(Path.Combine(box.Dir("download"), "Rig.exe"), "NEW");
        var target = box.Write(Path.Combine(box.Dir("installed"), "Rig.exe"), "OLD");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer.CopyPayloadAsync(source, target, cancel: cancel.Token));
        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task Commit_failure_restores_already_replaced_files()
    {
        using var box = new Sandbox();
        var source = box.Write(Path.Combine(box.Dir("build"), "Rig.exe"), "NEW APP");
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            box.Write(Path.ChangeExtension(source, suffix), "NEW " + suffix);
        var target = box.Write(Path.Combine(box.Dir("installed"), "Rig.exe"), "OLD APP");
        box.Write(Path.ChangeExtension(target, ".dll"), "OLD ASSEMBLY");
        Directory.CreateDirectory(Path.ChangeExtension(target, ".deps.json")); // refuses the second commit
        await Assert.ThrowsAsync<IOException>(() => Installer.CopyPayloadAsync(source, target));
        Assert.Equal("OLD APP", File.ReadAllText(target));
        Assert.Equal("OLD ASSEMBLY", File.ReadAllText(Path.ChangeExtension(target, ".dll")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.setup-*"));
    }
}
