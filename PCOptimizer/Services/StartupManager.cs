using Microsoft.Win32;

namespace PCOptimizer.Services;

public sealed class StartupItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Location { get; init; }
    public required RegistryHive ApprovedHive { get; init; }
    public required string ApprovedKey { get; init; }
    public required string ValueName { get; init; }
    /// <summary>The executable or shortcut on disk, when it could be resolved from the command.</summary>
    public string? FilePath { get; init; }
    public System.Windows.Media.ImageSource? Icon { get; init; }
    public string Publisher { get; init; } = "";
    public string Subtitle => Publisher.Length > 0 ? Publisher : Location;

    bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (Set(ref _isEnabled, value)) OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText => IsEnabled ? "Enabled" : "Disabled";
}

/// <summary>
/// Reads Run keys and Startup folders, and toggles items through the same
/// StartupApproved flags that Task Manager uses, so nothing is ever deleted.
/// </summary>
public static class StartupManager
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupItem> Load()
    {
        var items = new List<StartupItem>();
        AddRegistry(items, RegistryHive.CurrentUser, RunKey, "Run", "Registry · current user");
        AddRegistry(items, RegistryHive.LocalMachine, RunKey, "Run", "Registry · all users");
        AddRegistry(items, RegistryHive.LocalMachine, Run32Key, "Run32", "Registry · all users (32-bit)");
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            RegistryHive.CurrentUser, "Startup folder · current user");
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            RegistryHive.LocalMachine, "Startup folder · all users");
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    static void AddRegistry(List<StartupItem> items, RegistryHive hive, string runKey, string approvedKey, string location)
    {
        using var root = Base(hive);
        using var key = root.OpenSubKey(runKey);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var command = key.GetValue(name)?.ToString() ?? "";
            var path = ExecutablePath(command);
            items.Add(new StartupItem
            {
                Name = name,
                Command = command,
                FilePath = path,
                Icon = FileIcons.For(path),
                Publisher = Publisher(path),
                Location = location,
                ApprovedHive = hive,
                ApprovedKey = approvedKey,
                ValueName = name,
                IsEnabled = ReadEnabled(root, approvedKey, name),
            });
        }
    }

    static void AddFolder(List<StartupItem> items, string folder, RegistryHive hive, string location)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        using var root = Base(hive);
        foreach (var file in Directory.GetFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new StartupItem
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = file,
                FilePath = file,
                Icon = FileIcons.For(file),
                Location = location,
                ApprovedHive = hive,
                ApprovedKey = "StartupFolder",
                ValueName = fileName,
                IsEnabled = ReadEnabled(root, "StartupFolder", fileName),
            });
        }
    }

    /// <summary>Pulls the program path out of a Run command such as "C:\x\app.exe" --minimized.</summary>
    static string? ExecutablePath(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            candidate = end > 1 ? command[1..end] : command.Trim('"');
        }
        else
        {
            int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
        }
        return File.Exists(candidate) ? candidate : null;
    }

    static string Publisher(string? path)
    {
        if (path is null) return "";
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? ""; }
        catch { return ""; }
    }

    static bool ReadEnabled(RegistryKey root, string approvedKey, string valueName)
    {
        using var key = root.OpenSubKey(ApprovedRoot + approvedKey);
        // No entry means enabled; an odd first byte (0x03) means disabled.
        return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    /// <summary>Throws UnauthorizedAccessException for all-users items when not elevated.</summary>
    public static void SetEnabled(StartupItem item, bool enabled)
    {
        using var root = Base(item.ApprovedHive);
        using var key = root.CreateSubKey(ApprovedRoot + item.ApprovedKey, writable: true);
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(item.ValueName, data, RegistryValueKind.Binary);
        item.IsEnabled = enabled;
    }
}
