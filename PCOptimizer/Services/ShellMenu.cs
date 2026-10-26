using System.Diagnostics;
using Microsoft.Win32;

namespace PCOptimizer.Services;

public sealed class MenuEntry : ObservableObject
{
    public required string Name { get; init; }
    public required string Where { get; init; }
    public required bool IsExtension { get; init; }
    /// <summary>Shell extension CLSID (extensions) or the verb's key path under Software\Classes (commands).</summary>
    public required string Id { get; init; }
    /// <summary>For commands: the hive that holds the verb (HKCU entries can be changed without admin).</summary>
    public RegistryHive Hive { get; init; } = RegistryHive.CurrentUser;
    public string Program { get; init; } = "";
    public string Company { get; init; } = "";
    public bool IsWindows { get; init; }
    public System.Windows.Media.ImageSource? Icon { get; init; }

    public string Kind => IsExtension ? "Shell extension" : "Menu command";
    public string Subtitle => $"{(Company.Length > 0 ? Company + " · " : "")}{Kind} · {Where}";
    public bool NeedsAdmin => !IsExtension && Hive == RegistryHive.LocalMachine;
    public bool CanChange => !NeedsAdmin || Elevation.IsElevated;

    bool _isOn;
    /// <summary>Bound two-way to the switch: flipping it enables/disables the entry.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn) return;
            try
            {
                ShellMenu.SetEnabled(this, value);
                _isOn = value;
                Changed?.Invoke(this);
            }
            catch (Exception ex) { Failed?.Invoke(this, ex); }
            OnPropertyChanged();
        }
    }

    public void Init(bool on) => _isOn = on;

    public static event Action<MenuEntry>? Changed;
    public static event Action<MenuEntry, Exception>? Failed;
}

/// <summary>
/// Lists the items third-party apps add to Explorer's right-click menu and switches them off reversibly:
/// shell extensions via the per-user "Shell Extensions\Blocked" list, commands via the LegacyDisable flag.
/// </summary>
public static class ShellMenu
{
    const string Blocked = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    static readonly (string Key, string Where)[] Places =
    [
        ("*", "Files"), ("AllFilesystemObjects", "Files & folders"), ("Directory", "Folders"),
        ("Folder", "Folders"), ("Directory\\Background", "Folder background"), ("Drive", "Drives"),
    ];

    // Built-in verbs that are part of Windows itself.
    static readonly HashSet<string> WindowsVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "opennewtab", "openinnewprocess", "explore", "find", "print", "printto", "runas",
        "runasuser", "cmd", "powershell", "pintohome", "pintostartscreen", "edit", "properties", "Share", "UpdateEncryptionSettings",
    };

    public static List<MenuEntry> Load()
    {
        var entries = new Dictionary<string, (MenuEntry Entry, List<string> Wheres)>(StringComparer.OrdinalIgnoreCase);
        var blocked = BlockedClsids();

        foreach (var (key, where) in Places)
        {
            // Shell extensions: shellex\ContextMenuHandlers\<name> (default value or the name is the CLSID).
            using (var handlers = Registry.ClassesRoot.OpenSubKey($@"{key}\shellex\ContextMenuHandlers"))
                foreach (var name in handlers?.GetSubKeyNames() ?? [])
                {
                    using var h = handlers!.OpenSubKey(name);
                    var clsid = (h?.GetValue("") as string)?.Trim() is { Length: > 0 } v && v.StartsWith('{') ? v
                        : name.StartsWith('{') ? name : null;
                    if (clsid is null) continue;
                    if (entries.TryGetValue(clsid, out var existing)) { existing.Wheres.Add(where); continue; }
                    var dll = ClsidDll(clsid);
                    var company = CompanyOf(dll);
                    var entry = new MenuEntry
                    {
                        Name = ClsidName(clsid) ?? name.TrimStart('{').TrimEnd('}'),
                        Where = where, IsExtension = true, Id = clsid, Program = dll ?? "", Company = company,
                        IsWindows = IsWindowsFile(dll, company), Icon = FileIcons.For(dll),
                    };
                    entry.Init(!blocked.Contains(clsid));
                    entries[clsid] = (entry, [where]);
                }

            // Commands: shell\<verb>, which lives in HKCU (per-user installs) or HKLM.
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                var shellPath = $@"Software\Classes\{key}\shell";
                using var shell = root.OpenSubKey(shellPath);
                foreach (var verb in shell?.GetSubKeyNames() ?? [])
                {
                    if (WindowsVerbs.Contains(verb)) continue;
                    var id = $@"{shellPath}\{verb}";
                    using var v = shell!.OpenSubKey(verb);
                    if (v is null) continue;
                    var command = v.OpenSubKey("command")?.GetValue("") as string ?? "";
                    var exe = ExeFrom(command);
                    // The same app often registers one verb for files, folders and backgrounds: show it once.
                    var dedupe = $"{hive}|{verb}|{exe ?? command}";
                    if (entries.TryGetValue(dedupe, out var same)) { same.Wheres.Add(where); continue; }
                    var company = CompanyOf(exe);
                    var label = (v.GetValue("MUIVerb") as string) ?? (v.GetValue("") as string) ?? verb;
                    if (label.StartsWith('@')) label = verb; // resource string reference; the key name reads better
                    var entry = new MenuEntry
                    {
                        Name = label.Replace("&", ""), Where = where, IsExtension = false, Id = id, Hive = hive,
                        Program = exe ?? command, Company = company,
                        // Program-less verbs installed for everyone are Windows' own (they run COM handlers).
                        IsWindows = IsWindowsFile(exe, company) || (exe is null && hive == RegistryHive.LocalMachine),
                        Icon = FileIcons.For(exe),
                    };
                    entry.Init(v.GetValue("LegacyDisable") is null);
                    entries[dedupe] = (entry, [where]);
                }
            }
        }

        return entries.Values
            .Select(e => e.Wheres.Count > 1
                ? new MenuEntry
                {
                    Name = e.Entry.Name, Where = string.Join(", ", e.Wheres.Distinct()), IsExtension = e.Entry.IsExtension,
                    Id = e.Entry.Id, Hive = e.Entry.Hive, Program = e.Entry.Program, Company = e.Entry.Company,
                    IsWindows = e.Entry.IsWindows, Icon = e.Entry.Icon,
                }.With(e.Entry.IsOn)
                : e.Entry)
            .OrderBy(e => e.IsWindows).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static MenuEntry With(this MenuEntry e, bool on) { e.Init(on); return e; }

    internal static void SetEnabled(MenuEntry e, bool enabled)
    {
        if (e.IsExtension)
        {
            using var k = Registry.CurrentUser.CreateSubKey(Blocked, writable: true);
            if (enabled) k.DeleteValue(e.Id, throwOnMissingValue: false);
            else k.SetValue(e.Id, "Hidden by PC Optimizer", RegistryValueKind.String);
            using var machine = Registry.LocalMachine.OpenSubKey(Blocked);
            if (enabled && machine?.GetValue(e.Id) is not null)
                throw new InvalidOperationException("It's also blocked for all users, which needs administrator mode to undo.");
            return;
        }
        using var root = RegistryKey.OpenBaseKey(e.Hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(e.Id, writable: true)
                        ?? throw new InvalidOperationException("The entry no longer exists.");
        if (enabled) key.DeleteValue("LegacyDisable", throwOnMissingValue: false);
        else key.SetValue("LegacyDisable", "", RegistryValueKind.String);
    }

    static HashSet<string> BlockedClsids()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = hive.OpenSubKey(Blocked);
            foreach (var n in k?.GetValueNames() ?? []) set.Add(n);
        }
        return set;
    }

    static string? ClsidName(string clsid)
    {
        using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
        return (k?.GetValue("") as string) is { Length: > 0 } n && !n.StartsWith('@') ? n : null;
    }

    static string? ClsidDll(string clsid)
    {
        using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
        var path = Environment.ExpandEnvironmentVariables((k?.GetValue("") as string ?? "").Trim('"'));
        return File.Exists(path) ? path : null;
    }

    static string? ExeFrom(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        string path = command.StartsWith('"') && command.IndexOf('"', 1) is var end and > 1
            ? command[1..end]
            : command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is var i and > 0 ? command[..(i + 4)] : command.Split(' ')[0];
        return File.Exists(path) ? path : null;
    }

    static string CompanyOf(string? file)
    {
        if (file is null) return "";
        try { return FileVersionInfo.GetVersionInfo(file).CompanyName?.Trim() ?? ""; } catch { return ""; }
    }

    static bool IsWindowsFile(string? file, string company) =>
        company.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        (file?.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) ?? false);
}
