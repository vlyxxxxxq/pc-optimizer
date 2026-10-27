namespace Rig.Tests;

/// <summary>
/// A throwaway folder plus an isolated Rig profile (RIG_SETTINGS points inside it), deleted afterwards.
/// Tests run one at a time (xunit.runner.json), because the profile is process-wide.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "rig-tests", Guid.NewGuid().ToString("N")[..10]);
    readonly Dictionary<string, string?> _saved = [];

    public Sandbox()
    {
        Directory.CreateDirectory(Path.Combine(Root, "profile"));
        Env("RIG_SETTINGS", Path.Combine(Root, "profile", "settings.json"));
    }

    public string Profile => Path.Combine(Root, "profile");

    public void Env(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public string Dir(string name)
    {
        var d = Path.Combine(Root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public string Write(string path, string content)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Makes a file look like an old, finished download.</summary>
    public string Aged(string path, string content)
    {
        var f = Write(path, content);
        File.SetLastWriteTime(f, DateTime.Now.AddMinutes(-30));
        return f;
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
        try { Directory.Delete(Root, true); } catch { }
    }
}
