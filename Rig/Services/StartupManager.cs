using Microsoft.Win32;

namespace Rig.Services;

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
    /// <summary>Set for scheduled tasks that run at sign-in/boot (toggled with Enable/Disable-ScheduledTask).</summary>
    public string? TaskPath { get; init; }
    public bool IsTask => TaskPath is not null;

    // Resource use of the running app (all its processes), filled in after loading.
    long _memory = -1;
    double _cpuPercent;
    public long Memory => _memory;
    /// <summary>Memory now plus average CPU since the app started (a long uptime doesn't inflate it).</summary>
    public string Impact => _memory < 0 ? "" : _memory == 0 ? "Not running"
        : _memory > 300L << 20 || _cpuPercent > 2 ? "High"
        : _memory > 80L << 20 || _cpuPercent > 0.5 ? "Medium" : "Low";
    public int ImpactRank => Impact switch { "High" => 3, "Medium" => 2, "Low" => 1, _ => 0 };
    public string ImpactDetail => _memory > 0 ? $"{Fmt.Bytes(_memory)} RAM · {_cpuPercent:0.0}% CPU avg" : "";

    public void SetUsage(long memory, double cpuPercent)
    {
        _memory = memory;
        _cpuPercent = cpuPercent;
        OnPropertyChanged(nameof(Impact));
        OnPropertyChanged(nameof(ImpactRank));
        OnPropertyChanged(nameof(ImpactDetail));
    }

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

    /// <summary>
    /// Non-Microsoft scheduled tasks that start at sign-in or boot. Apps use these to launch
    /// without appearing in Task Manager's Startup tab (updaters, vendor helpers, launchers).
    /// </summary>
    public static async Task<List<StartupItem>> LoadTasksAsync()
    {
        const string script =
            "$ErrorActionPreference='SilentlyContinue'; @(Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' -and " +
            "($_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' }) } | " +
            "ForEach-Object { [pscustomobject]@{ Name=$_.TaskName; Path=$_.TaskPath; State=[string]$_.State; " +
            "Exe=[string]$_.Actions[0].Execute; Args=[string]$_.Actions[0].Arguments } }) | ConvertTo-Json -Compress";
        var (_, output) = await Shell.RunAsync("powershell", $"-NoProfile -Command \"{script.Replace("\"", "\\\"")}\"", System.Text.Encoding.UTF8);
        var items = new List<StartupItem>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(output.Trim());
            var root = doc.RootElement;
            var list = root.ValueKind == System.Text.Json.JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
            foreach (var t in list)
            {
                var name = t.GetProperty("Name").GetString() ?? "";
                var exe = Environment.ExpandEnvironmentVariables((t.GetProperty("Exe").GetString() ?? "").Trim('"'));
                var path = File.Exists(exe) ? exe : null;
                items.Add(new StartupItem
                {
                    // Per-user tasks append the user's SID; drop it for readability.
                    Name = System.Text.RegularExpressions.Regex.Replace(name, @"-S-1-5-21-[\d-]+$", ""),
                    Command = $"{t.GetProperty("Exe").GetString()} {t.GetProperty("Args").GetString()}".Trim(),
                    FilePath = path,
                    Icon = FileIcons.For(path),
                    Publisher = Publisher(path),
                    Location = "Scheduled task",
                    ApprovedHive = RegistryHive.CurrentUser,
                    ApprovedKey = "",
                    ValueName = name,
                    TaskPath = t.GetProperty("Path").GetString() ?? "\\",
                    IsEnabled = t.GetProperty("State").GetString() != "Disabled",
                });
            }
        }
        catch { /* no tasks, or PowerShell unavailable */ }
        return items;
    }

    /// <summary>Measures what each startup item's program is using right now (memory, CPU time since it started).</summary>
    public static void MeasureUsage(IEnumerable<StartupItem> items)
    {
        // Per program: total memory now, CPU seconds used, and the earliest start (for an average CPU %).
        var byPath = new Dictionary<string, (long Mem, double Cpu, DateTime Start)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var path = Native.ProcessPath(p.Id);
                    if (path is null) continue;
                    var (mem, cpu, start) = byPath.GetValueOrDefault(path, (0, 0, DateTime.MaxValue));
                    double seconds = 0;
                    DateTime started = DateTime.Now;
                    try { seconds = p.TotalProcessorTime.TotalSeconds; started = p.StartTime; } catch { }
                    byPath[path] = (mem + p.WorkingSet64, cpu + seconds, started < start ? started : start);
                }
                catch { }
            }
        }
        foreach (var item in items)
        {
            if (item.FilePath is null || !item.FilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!byPath.TryGetValue(item.FilePath, out var u))
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => item.SetUsage(0, 0));
                continue;
            }
            double alive = Math.Max(60, (DateTime.Now - u.Start).TotalSeconds) * Environment.ProcessorCount;
            System.Windows.Application.Current.Dispatcher.Invoke(() => item.SetUsage(u.Mem, 100 * u.Cpu / alive));
        }
    }

    static void SetTaskEnabled(StartupItem item, bool enabled)
    {
        static string Q(string s) => s.Replace("'", "''");
        var cmd = $"{(enabled ? "Enable" : "Disable")}-ScheduledTask -TaskPath '{Q(item.TaskPath!)}' -TaskName '{Q(item.ValueName)}' -ErrorAction Stop | Out-Null";
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -Command \"{cmd}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode != 0)
        {
            if (error.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || error.Contains("0x80070005"))
                throw new UnauthorizedAccessException(error);
            throw new InvalidOperationException(error.Trim());
        }
    }

    /// <summary>Throws UnauthorizedAccessException for all-users items when not elevated.</summary>
    public static void SetEnabled(StartupItem item, bool enabled)
    {
        if (item.IsTask)
        {
            SetTaskEnabled(item, enabled);
            item.IsEnabled = enabled;
            return;
        }
        using var root = Base(item.ApprovedHive);
        using var key = root.CreateSubKey(ApprovedRoot + item.ApprovedKey, writable: true);
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(item.ValueName, data, RegistryValueKind.Binary);
        item.IsEnabled = enabled;
    }
}
