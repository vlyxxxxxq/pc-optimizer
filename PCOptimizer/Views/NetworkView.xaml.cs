using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCOptimizer.Services;

namespace PCOptimizer.Views;

public partial class NetworkView : UserControl
{
    const int History = 60;

    static readonly Dictionary<string, string> Presets = new()
    {
        ["Cloudflare"] = "1.1.1.1",
        ["Google"] = "8.8.8.8",
        ["Discord"] = "discord.com",
        ["Steam"] = "steamcommunity.com",
        ["Epic / Fortnite"] = "epicgames.com",
        ["Riot"] = "riotgames.com",
        ["Your router"] = "",
    };

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly List<long?> _samples = [];
    bool _pinging, _loaded;
    CancellationTokenSource? _speedCts;

    public NetworkView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await PingOnceAsync();
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopPing(); };
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        RenderConnection();
        var gateway = NetworkTools.Connection()?.Gateway.Split(',')[0] ?? "";
        HostPresets.ItemsSource = Presets.Select(p => p.Key == "Your router" ? new KeyValuePair<string, string>(p.Key, gateway) : p)
            .Where(p => p.Value.Length > 0).ToList();
    }

    void RenderConnection()
    {
        var c = NetworkTools.Connection();
        if (c is null)
        {
            ConnTitle.Text = "No internet connection found";
            ConnDetail.Text = "";
            return;
        }
        ConnIcon.Text = c.Kind == "Wi-Fi" ? "" : "";
        ConnTitle.Text = $"{c.Kind} · {c.Adapter} · {c.Speed} link" + (c.Vpn is { } v ? $" · through VPN ({v})" : "");
        ConnDetail.Text = $"IP {c.LocalIp}   ·   Gateway {c.Gateway}   ·   DNS {c.Dns}";
        if (c.Vpn is not null)
            ConnDetail.Text += "\nYour VPN is on: ping, DNS and speed results go through it. Turn it off to test your real connection.";
        else if (c.Kind == "Wi-Fi")
            ConnDetail.Text += "   ·   Tip: Ethernet usually gives lower, steadier ping for games.";
    }

    // ---------- Ping ----------

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string host) return;
        HostBox.Text = host;
        StopPing();
        StartPing();
    }

    void Ping_Click(object sender, RoutedEventArgs e)
    {
        if (_timer.IsEnabled) StopPing(); else StartPing();
    }

    void StartPing()
    {
        if (HostBox.Text.Trim().Length == 0) return;
        _samples.Clear();
        PingButton.Content = "Stop";
        PingVerdict.Text = $"Pinging {HostBox.Text.Trim()} every second…";
        _timer.Start();
    }

    void StopPing()
    {
        _timer.Stop();
        PingButton.Content = "Start";
    }

    async Task PingOnceAsync()
    {
        if (_pinging) return;
        _pinging = true;
        try
        {
            _samples.Add(await NetworkTools.PingAsync(HostBox.Text.Trim()));
            if (_samples.Count > History) _samples.RemoveAt(0);
            RenderPing();
        }
        finally { _pinging = false; }
    }

    void RenderPing()
    {
        var ok = _samples.Where(s => s is not null).Select(s => (double)s!.Value).ToList();
        double loss = 100.0 * _samples.Count(s => s is null) / Math.Max(1, _samples.Count);
        PingNow.Text = _samples.LastOrDefault() is { } now ? $"{now} ms" : "timeout";
        PingLoss.Text = $"{loss:0}%";
        PingLoss.Foreground = (Brush)FindResource(loss == 0 ? "TextBrush" : loss < 3 ? "WarnBrush" : "DangerBrush");
        if (ok.Count == 0) return;

        double avg = ok.Average();
        // Jitter: average change between consecutive successful pings.
        double jitter = ok.Count > 1 ? ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average() : 0;
        PingAvg.Text = $"{avg:0} ms";
        PingRange.Text = $"{ok.Min():0} / {ok.Max():0}";
        PingJitter.Text = $"{jitter:0.0} ms";
        PingJitter.Foreground = (Brush)FindResource(jitter < 5 ? "TextBrush" : jitter < 15 ? "WarnBrush" : "DangerBrush");

        PingVerdict.Text = (avg, jitter, loss) switch
        {
            (_, _, >= 3) => "Packet loss detected: expect rubber-banding. Try Ethernet, or restart your router.",
            (_, >= 15, _) => "Unstable connection: ping is jumping around a lot. Wi-Fi interference or a busy network is the usual cause.",
            ( < 40, < 5, _) => "Excellent for online games.",
            ( < 80, < 10, _) => "Good for online games.",
            _ => "Playable, but you may notice delay in fast games. A closer server helps most.",
        };

        double w = PingChart.ActualWidth, h = PingChart.ActualHeight;
        if (w <= 0) return;
        double scale = Math.Max(50, Math.Ceiling(ok.Max() / 50) * 50);
        ChartScale.Text = $"{scale:0} ms";
        double step = w / (History - 1);
        int offset = History - _samples.Count;
        var pts = new PointCollection();
        for (int i = 0; i < _samples.Count; i++)
        {
            double v = _samples[i] ?? scale; // timeouts spike to the top
            pts.Add(new Point((offset + i) * step, h - 1 - v / scale * (h - 2)));
        }
        PingLine.Points = pts;
        if (pts.Count > 0)
            PingFill.Points = new PointCollection(pts) { new Point(pts[^1].X, h), new Point(pts[0].X, h) };
    }

    // ---------- DNS ----------

    async void Dns_Click(object sender, RoutedEventArgs e)
    {
        DnsButton.IsEnabled = false;
        var progress = new Progress<string>(s => DnsText.Text = s);
        try
        {
            var results = await NetworkTools.BenchmarkDnsAsync(progress);
            DnsList.ItemsSource = results;
            var best = results.FirstOrDefault(r => r.Failed < 5);
            var current = results.FirstOrDefault(r => r.IsCurrent);
            bool othersBlocked = results.Where(r => !r.IsCurrent).All(r => r.Failed >= 5);
            DnsText.Text = best is null ? "No DNS server answered. Check your connection."
                : othersBlocked && NetworkTools.VpnAdapter() is not null
                    ? "Your VPN only lets its own DNS through, so other servers can't be tested (or used) while it's on."
                : current is not null && ReferenceEquals(best, current) ? $"You're already on the fastest one ({best.Provider.Name})."
                : $"{best.Provider.Name} is fastest here ({best.AvgText})" +
                  (current is not null ? $" vs {current.AvgText} for what you use now." : ".");
        }
        finally { DnsButton.IsEnabled = true; }
    }

    async void UseDns_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not DnsResult r) return;
        if (r.Provider.Name == "Your current DNS") return;
        if (MessageBox.Show($"Switch this PC's DNS to {r.Provider.Name} ({r.Provider.Primary})?\n\n" +
                            "Windows will ask for administrator permission. You can switch back any time with \"Back to automatic DNS\".",
                "Change DNS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        DnsText.Text = await NetworkTools.SetDnsAsync(r.Provider)
            ? $"Now using {r.Provider.Name}. Run the test again to confirm."
            : "DNS wasn't changed (the administrator prompt was declined or failed).";
        RenderConnection();
    }

    async void AutoDns_Click(object sender, RoutedEventArgs e)
    {
        DnsText.Text = await NetworkTools.SetDnsAsync(null)
            ? "Back to automatic DNS from your router."
            : "DNS wasn't changed (the administrator prompt was declined or failed).";
        RenderConnection();
    }

    // ---------- Speed ----------

    async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (_speedCts is not null) { _speedCts.Cancel(); return; }
        _speedCts = new CancellationTokenSource();
        SpeedButton.Content = "Cancel";
        SpeedDetail.Text = "Downloading…";
        var progress = new Progress<double>(m => SpeedText.Text = m.ToString("0"));
        try
        {
            double mbps = await NetworkTools.DownloadSpeedAsync(progress, _speedCts.Token);
            SpeedText.Text = mbps.ToString("0");
            SpeedDetail.Text = $"{mbps / 8:0.0} MB/s · " + (mbps switch
            {
                >= 500 => "Very fast. A 100 GB game downloads in about half an hour.",
                >= 100 => "Fast. Great for game downloads and streaming.",
                >= 25 => "OK for gaming and HD streaming; big game downloads take a while.",
                _ => "Slow. Big downloads will take hours.",
            });
        }
        catch (OperationCanceledException) { SpeedDetail.Text = "Cancelled."; }
        catch (Exception ex) { SpeedDetail.Text = $"Couldn't run the test: {ex.Message}"; }
        finally
        {
            _speedCts.Dispose();
            _speedCts = null;
            SpeedButton.Content = "Run speed test";
        }
    }
}
