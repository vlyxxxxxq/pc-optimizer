using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCOptimizer.Services;

public sealed record DnsProvider(string Name, string Primary, string Secondary);

public sealed record DnsResult(DnsProvider Provider, double AvgMs, int Failed, bool IsCurrent)
{
    public string AvgText => Failed >= 5 ? "No reply" : $"{AvgMs:0} ms";
}

public sealed record ConnectionInfo(string Adapter, string Kind, string Speed, string LocalIp, string Gateway, string Dns)
{
    /// <summary>Name of the VPN tunnel carrying traffic, if one is connected.</summary>
    public string? Vpn { get; init; }
}

public static class NetworkTools
{
    public static readonly DnsProvider[] Providers =
    [
        new("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new("Google", "8.8.8.8", "8.8.4.4"),
        new("Quad9", "9.9.9.9", "149.112.112.112"),
        new("OpenDNS", "208.67.222.222", "208.67.220.220"),
        new("AdGuard (blocks ads)", "94.140.14.14", "94.140.15.15"),
        new("Control D", "76.76.2.0", "76.76.10.0"),
    ];

    /// <summary>Popular sites so the DNS test isn't skewed by one domain's caching.</summary>
    static readonly string[] TestDomains =
    [
        "google.com", "youtube.com", "discord.com", "steampowered.com", "twitch.tv",
        "reddit.com", "epicgames.com", "riotgames.com", "amazon.com", "wikipedia.org",
    ];

    static readonly string[] VpnHints = ["vpn", "wireguard", "wintun", "tap-", "tap ", "openvpn", "mullvad", "nordlynx",
        "tailscale", "zerotier", "proton", "expressvpn", "surfshark", "cloudflare warp", "hamachi"];

    static bool IsVpn(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel ||
        VpnHints.Any(h => n.Description.Contains(h, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(h, StringComparison.OrdinalIgnoreCase));

    static IEnumerable<NetworkInterface> Routed() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                                     !g.Address.Equals(IPAddress.Any)));

    /// <summary>The physical adapter (Ethernet/Wi-Fi) your internet comes in on, ignoring VPN tunnels.</summary>
    public static NetworkInterface? ActiveAdapter() =>
        Routed().Where(n => !IsVpn(n))
            .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .ThenByDescending(n => n.Speed)
            .FirstOrDefault()
        // Some VPNs remove the physical adapter's default route; fall back to any physical adapter that's up.
        ?? NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up && !IsVpn(n) &&
            n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet);

    /// <summary>A connected VPN tunnel, if traffic is going through one.</summary>
    public static NetworkInterface? VpnAdapter() => Routed().FirstOrDefault(IsVpn);

    public static ConnectionInfo? Connection()
    {
        var n = ActiveAdapter();
        if (n is null) return null;
        var vpn = VpnAdapter();
        // While a VPN is on, IP/router/DNS that matter are the tunnel's.
        var ip = (vpn ?? n).GetIPProperties();
        string v4(IEnumerable<IPAddress> a) => string.Join(", ", a.Where(x => x.AddressFamily == AddressFamily.InterNetwork));
        string speed = n.Speed >= 1_000_000_000 ? $"{n.Speed / 1e9:0.#} Gbps" : $"{n.Speed / 1e6:0} Mbps";
        string kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
        return new ConnectionInfo(n.Description, kind, speed,
            v4(ip.UnicastAddresses.Select(u => u.Address)), v4(ip.GatewayAddresses.Select(g => g.Address)), v4(ip.DnsAddresses))
        {
            Vpn = vpn?.Description,
        };
    }

    public static string[] CurrentDns() =>
        (VpnAdapter() ?? ActiveAdapter())?.GetIPProperties().DnsAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray() ?? [];

    /// <summary>One ping in ms, or null on timeout/failure.</summary>
    public static async Task<long?> PingAsync(string host)
    {
        try
        {
            using var p = new Ping();
            var reply = await p.SendPingAsync(host, 2000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch { return null; }
    }

    /// <summary>Times raw DNS lookups against each provider (bypassing Windows' DNS cache).</summary>
    public static async Task<List<DnsResult>> BenchmarkDnsAsync(IProgress<string> progress)
    {
        var current = CurrentDns();
        var providers = Providers.ToList();
        // Also measure whatever this PC uses now (often the router), unless it's one of the list.
        if (current.FirstOrDefault() is { } cur && providers.All(p => p.Primary != cur))
            providers.Insert(0, new DnsProvider("Your current DNS", cur, current.ElementAtOrDefault(1) ?? ""));

        var results = new List<DnsResult>();
        foreach (var provider in providers)
        {
            progress.Report($"Testing {provider.Name}…");
            // All lookups at once: an unreachable server (e.g. blocked by a VPN) costs one timeout, not ten.
            var replies = await Task.WhenAll(TestDomains.Select(d => QueryAsync(provider.Primary, d)));
            var times = replies.Where(r => r is not null).Select(r => r!.Value).ToList();
            int failed = replies.Length - times.Count;
            results.Add(new DnsResult(provider, times.Count > 0 ? times.Average() : double.MaxValue, failed,
                current.Contains(provider.Primary)));
        }
        return results.OrderBy(r => r.AvgMs).ToList();
    }

    /// <summary>Sends one A-record query over UDP and returns the round trip in ms.</summary>
    static async Task<double?> QueryAsync(string server, string domain)
    {
        var query = new List<byte> { (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        query.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // end of name, type A, class IN

        try
        {
            using var udp = new UdpClient();
            udp.Connect(IPAddress.Parse(server), 53);
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query.ToArray());
            var receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(1000)) != receive) return null;
            sw.Stop();
            var answer = receive.Result.Buffer;
            return answer.Length > 3 && answer[0] == query[0] && answer[1] == query[1] ? sw.Elapsed.TotalMilliseconds : null;
        }
        catch { return null; }
    }

    /// <summary>Points the active adapter at a DNS provider (or back to automatic/DHCP when null). Needs admin: shows UAC.</summary>
    public static async Task<bool> SetDnsAsync(DnsProvider? provider)
    {
        var adapter = ActiveAdapter()?.Name;
        if (adapter is null) return false;
        string script = provider is null
            ? $"netsh interface ipv4 set dnsservers name=\"{adapter}\" source=dhcp & ipconfig /flushdns"
            : $"netsh interface ipv4 set dnsservers name=\"{adapter}\" static {provider.Primary} primary & " +
              (provider.Secondary.Length > 0 ? $"netsh interface ipv4 add dnsservers name=\"{adapter}\" {provider.Secondary} index=2 & " : "") +
              "ipconfig /flushdns";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC declined
    }

    /// <summary>Downloads from Cloudflare's speed test endpoint and returns Mbps.</summary>
    public static async Task<double> DownloadSpeedAsync(IProgress<double> progress, CancellationToken ct)
    {
        const long bytes = 50_000_000;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var sw = Stopwatch.StartNew();
        using var response = await http.GetAsync($"https://speed.cloudflare.com/__down?bytes={bytes}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            progress.Report(total * 8 / 1e6 / sw.Elapsed.TotalSeconds);
        }
        return total * 8 / 1e6 / sw.Elapsed.TotalSeconds;
    }
}
