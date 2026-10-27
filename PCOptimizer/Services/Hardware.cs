using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PCOptimizer.Services;

public sealed record SpecRow(string Label, string Value);
public sealed record SpecSection(string Title, string Glyph, List<SpecRow> Rows);

public sealed record DriveHealth(string Name, string Type, string Size, string Health, string Detail)
{
    public bool Healthy => Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase);
}

public sealed record HardwareReport(List<SpecSection> Sections, List<DriveHealth> Drives);

public static class Hardware
{
    // One PowerShell/CIM round trip for everything WMI knows (no System.Management package needed).
    const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1 Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L3CacheSize
        $board = Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product
        $bios = Get-CimInstance Win32_BIOS | Select-Object SMBIOSBIOSVersion, @{n='Date';e={$_.ReleaseDate.ToString('yyyy-MM-dd')}}
        $ram = @(Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber, DeviceLocator)
        $slots = (Get-CimInstance Win32_PhysicalMemoryArray | Measure-Object MemoryDevices -Sum).Sum
        $os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, BuildNumber, @{n='Installed';e={$_.InstallDate.ToString('yyyy-MM-dd')}}
        $disks = @(Get-PhysicalDisk | ForEach-Object {
            $r = $_ | Get-StorageReliabilityCounter
            [pscustomobject]@{ Name = $_.FriendlyName; Media = [string]$_.MediaType; Bus = [string]$_.BusType; Size = $_.Size;
                               Health = [string]$_.HealthStatus; Wear = $r.Wear; Temp = $r.Temperature; Hours = $r.PowerOnHours }
        })
        [pscustomobject]@{ cpu = $cpu; board = $board; bios = $bios; ram = $ram; slots = $slots; os = $os; disks = $disks } |
            ConvertTo-Json -Depth 4 -Compress
        """;

    public static async Task<HardwareReport> LoadAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), "pcoptimizer-hw.ps1");
        await File.WriteAllTextAsync(file, Script);
        var (_, output) = await Shell.RunAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\"", Encoding.UTF8);
        try { File.Delete(file); } catch { }

        using var doc = JsonDocument.Parse(output.Trim());
        var j = doc.RootElement;
        string S(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString().Trim() : "";
        long L(JsonElement e, string p) => long.TryParse(S(e, p), out var v) ? v : 0;
        var sections = new List<SpecSection>();

        var cpu = j.GetProperty("cpu");
        sections.Add(new("Processor", "",
        [
            new("Model", S(cpu, "Name")),
            new("Cores / threads", $"{S(cpu, "NumberOfCores")} cores · {S(cpu, "NumberOfLogicalProcessors")} threads"),
            new("Base clock", $"{L(cpu, "MaxClockSpeed") / 1000.0:0.00} GHz"),
            new("L3 cache", L(cpu, "L3CacheSize") > 0 ? $"{L(cpu, "L3CacheSize") / 1024.0:0} MB" : "—"),
        ]));

        var sticks = j.GetProperty("ram").EnumerateArray().ToList();
        long totalRam = sticks.Sum(s => L(s, "Capacity"));
        long configured = sticks.Select(s => L(s, "ConfiguredClockSpeed")).DefaultIfEmpty(0).Max();
        long rated = sticks.Select(s => L(s, "Speed")).DefaultIfEmpty(0).Max();
        var ramRows = new List<SpecRow>
        {
            new("Installed", $"{Fmt.Bytes(totalRam)} · {sticks.Count} of {S(j, "slots")} slots used"),
            new("Speed", configured > 0 ? $"{configured} MT/s" + (rated > configured ? $" (rated {rated})" : "") : "—"),
        };
        ramRows.AddRange(sticks.Select((s, i) => new SpecRow($"Stick {i + 1}",
            $"{Fmt.Bytes(L(s, "Capacity"))} · {S(s, "Manufacturer")} {S(s, "PartNumber")}".Trim())));
        sections.Add(new("Memory", "", ramRows));

        var gpuRows = new List<SpecRow>();
        foreach (var g in GpuInfo.Load())
            gpuRows.Add(new(GpuInfo.IsIntegrated(g.Name) ? "Integrated" : "Graphics card",
                $"{g.Name}\n{(Vram(g.Name) is { } v and > 0 ? Fmt.Bytes(v) + " VRAM · " : "")}driver {g.Version}"));
        sections.Add(new("Graphics", "", gpuRows));

        var board = j.GetProperty("board");
        var bios = j.GetProperty("bios");
        sections.Add(new("Motherboard", "",
        [
            new("Board", $"{S(board, "Manufacturer")} {S(board, "Product")}"),
            new("BIOS", $"{S(bios, "SMBIOSBIOSVersion")} ({S(bios, "Date")})"),
        ]));

        var os = j.GetProperty("os");
        sections.Add(new("Windows", "",
        [
            new("Edition", S(os, "Caption")),
            new("Build", S(os, "BuildNumber")),
            new("Installed", S(os, "Installed")),
        ]));

        var drives = new List<DriveHealth>();
        foreach (var d in j.GetProperty("disks").EnumerateArray())
        {
            var media = S(d, "Media") is "SSD" or "4" ? "SSD" : S(d, "Media") is "HDD" or "3" ? "HDD" : "Drive";
            var bus = S(d, "Bus") is "NVMe" or "17" ? "NVMe" : S(d, "Bus") is "SATA" or "11" ? "SATA" : S(d, "Bus") is "USB" or "7" ? "USB" : S(d, "Bus");
            var detail = new List<string>();
            if (L(d, "Wear") is var wear and > 0) detail.Add($"{wear}% worn");
            if (L(d, "Temp") is var temp and > 0) detail.Add($"{temp} °C");
            if (L(d, "Hours") is var hours and > 0) detail.Add($"{hours:N0} hours on");
            if (detail.Count == 0) detail.Add("Wear and temperature need administrator mode");
            var health = S(d, "Health") switch { "0" or "Healthy" => "Healthy", "1" or "Warning" => "Warning", "2" or "Unhealthy" => "Unhealthy", var h => h };
            drives.Add(new DriveHealth(S(d, "Name"), $"{bus} {media}".Trim(), Fmt.Bytes(L(d, "Size")), health, string.Join(" · ", detail)));
        }
        return new HardwareReport(sections, drives);
    }

    /// <summary>Dedicated VRAM from the display driver's registry entry (WMI caps this at 4 GB).</summary>
    static long? Vram(string gpuName)
    {
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
        {
            using var k = cls!.OpenSubKey(sub);
            if (k?.GetValue("DriverDesc") as string != gpuName) continue;
            return k.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long l => l,
                byte[] b when b.Length >= 8 => BitConverter.ToInt64(b),
                _ => null,
            };
        }
        return null;
    }
}

public sealed record BenchResult(DateTime Date, double CpuSingle, double CpuMulti, double MemoryGBs,
    double DiskReadMBs, double DiskWriteMBs, string Drive)
{
    [System.Text.Json.Serialization.JsonIgnore] public string When => Date.ToString("g");
    [System.Text.Json.Serialization.JsonIgnore] public string Line =>
        $"CPU {CpuSingle:N0} / {CpuMulti:N0} · RAM {MemoryGBs:0.0} GB/s · {Drive} read {DiskReadMBs:N0} / write {DiskWriteMBs:N0} MB/s";
}

/// <summary>
/// Quick built-in benchmarks. Scores are relative: they're for comparing this PC with itself over time
/// (before/after a tweak, a new driver, a dusty fan), not with other benchmark tools.
/// </summary>
public static class Benchmark
{
    static string HistoryPath => AppSettings.DataFile("benchmarks.json");

    /// <summary>CPU points = MB/s of SHA-256 hashing (one thread, then all threads), 3 s each.</summary>
    public static (double Single, double Multi) Cpu(CancellationToken ct)
    {
        static double Run(int threads, CancellationToken ct)
        {
            long total = 0;
            var until = Stopwatch.StartNew();
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, _ =>
            {
                var data = new byte[1 << 20];
                Random.Shared.NextBytes(data);
                long mine = 0;
                while (until.Elapsed < TimeSpan.FromSeconds(3)) { SHA256.HashData(data); mine += data.Length; }
                Interlocked.Add(ref total, mine);
            });
            return total / 1e6 / until.Elapsed.TotalSeconds;
        }
        return (Run(1, ct), Run(Environment.ProcessorCount, ct));
    }

    /// <summary>Memory copy bandwidth in GB/s (256 MB blocks, 2 s).</summary>
    public static double Memory(CancellationToken ct)
    {
        var a = new byte[256 << 20];
        var b = new byte[256 << 20];
        Random.Shared.NextBytes(a);
        var sw = Stopwatch.StartNew();
        long copied = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            ct.ThrowIfCancellationRequested();
            Buffer.BlockCopy(a, 0, b, 0, a.Length);
            copied += a.Length * 2L; // read + write
        }
        return copied / 1e9 / sw.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Sequential write then read of a 1 GB file with the Windows cache bypassed (unbuffered I/O with
    /// sector-aligned buffers), so it measures the drive and not RAM. The test file is deleted afterwards.
    /// </summary>
    public static unsafe (double ReadMBs, double WriteMBs) Disk(string folder, IProgress<string> progress, CancellationToken ct)
    {
        const int block = 8 << 20;           // 8 MB, a multiple of any sector size
        const long size = 1L << 30;          // 1 GB
        const FileOptions NoBuffering = (FileOptions)0x20000000;
        var path = Path.Combine(folder, $"pcoptimizer-bench-{Guid.NewGuid():N}.tmp");
        var buffer = (byte*)NativeMemory.AlignedAlloc(block, 4096);
        try
        {
            var span = new Span<byte>(buffer, block);
            Random.Shared.NextBytes(span);

            progress.Report("Disk: writing 1 GB…");
            var sw = Stopwatch.StartNew();
            // Unbuffered writes skip Windows' cache; one flush at the end makes the drive commit everything
            // (forcing write-through on every block would measure a worst case no real app hits).
            using (var h = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       NoBuffering, preallocationSize: size))
            {
                for (long off = 0; off < size; off += block)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, span, off);
                }
                RandomAccess.FlushToDisk(h);
            }
            double write = size / 1e6 / sw.Elapsed.TotalSeconds;

            progress.Report("Disk: reading 1 GB…");
            sw.Restart();
            using (var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None, NoBuffering | FileOptions.SequentialScan))
                for (long off = 0; off < size; off += block)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, span, off);
                }
            double read = size / 1e6 / sw.Elapsed.TotalSeconds;
            return (read, write);
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            try { File.Delete(path); } catch { }
        }
    }

    public static List<BenchResult> History()
    {
        try { return File.Exists(HistoryPath) ? JsonSerializer.Deserialize<List<BenchResult>>(File.ReadAllText(HistoryPath)) ?? [] : []; }
        catch { return []; }
    }

    public static void Save(BenchResult r)
    {
        var all = History();
        all.Insert(0, r);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(all.Take(50)));
        }
        catch { }
    }
}
