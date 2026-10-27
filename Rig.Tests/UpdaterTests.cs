using System.Security.Cryptography;
using System.Text.Json;
using Rig.Services;

namespace Rig.Tests;

public class UpdaterTests
{
    [Fact]
    public void Signatures_verify_and_tampering_is_caught()
    {
        // A throwaway key, only for this test.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        var sig = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        Assert.True(Updater.Verify(new MemoryStream(payload), sig, publicKey));
        payload[100] ^= 1;
        Assert.False(Updater.Verify(new MemoryStream(payload), sig, publicKey));
        Assert.False(Updater.Verify(new MemoryStream(payload), "not base64!", publicKey));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        payload[100] ^= 1;
        Assert.False(Updater.Verify(new MemoryStream(payload), sig, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void The_shipped_build_says_honestly_whether_signing_is_set_up()
    {
        // Until a real key is embedded (docs/SIGNING.md) this is false; it must never be a placeholder.
        Assert.Equal(Updater.SigningKey.Length > 0, Updater.SigningConfigured);
        if (Updater.SigningConfigured) Assert.NotNull(Convert.FromBase64String(Updater.SigningKey));
    }

    static string Install(Sandbox box)
    {
        var dir = box.Dir("app");
        var exe = Path.Combine(dir, "Rig.exe");
        File.WriteAllText(exe, "NEW");
        File.WriteAllText(exe + ".old", "OLD");
        File.WriteAllText(exe + ".update.json", JsonSerializer.Serialize(new Updater.PendingUpdate(exe, "2.0.0", "2.1.0", 0)));
        return exe;
    }

    [Fact]
    public void A_new_version_that_starts_keeps_the_old_one_until_healthy()
    {
        using var box = new Sandbox();
        var exe = Install(box);
        Assert.Equal(Updater.StartCheck.Trial, Updater.CheckAtStart(exe, launchOld: false));
        Assert.True(File.Exists(exe + ".old")); // not removed while on trial
        Updater.MarkHealthy(exe);
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".update.json"));
        Assert.Equal(Updater.StartCheck.Normal, Updater.CheckAtStart(exe, launchOld: false));
    }

    [Fact]
    public void A_new_version_that_keeps_failing_is_rolled_back()
    {
        using var box = new Sandbox();
        var exe = Install(box);
        Assert.Equal(Updater.StartCheck.Trial, Updater.CheckAtStart(exe, launchOld: false));
        Assert.Equal(Updater.StartCheck.Trial, Updater.CheckAtStart(exe, launchOld: false));
        Assert.Equal(Updater.StartCheck.RolledBack, Updater.CheckAtStart(exe, launchOld: false));
        Assert.Equal("OLD", File.ReadAllText(exe));
        Assert.Equal("NEW", File.ReadAllText(exe + ".failed"));
        Assert.False(File.Exists(exe + ".update.json"));
    }

    [Fact]
    public void An_interrupted_download_is_cleaned_up_and_the_old_exe_kept_on_trial()
    {
        using var box = new Sandbox();
        var exe = Install(box);
        File.WriteAllText(exe + ".new", "PARTIAL");
        Updater.CheckAtStart(exe, launchOld: false);
        Assert.False(File.Exists(exe + ".new"));
        Updater.CleanUpOld(exe);
        Assert.True(File.Exists(exe + ".old"));
    }
}
