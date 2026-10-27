using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Services;

public sealed record NewsItem(string Title, string Url, DateTime Date, string Summary, string Source)
{
    public string When => (DateTime.Now - Date).TotalDays < 1 ? "Today" : (DateTime.Now - Date).TotalDays < 2 ? "Yesterday" : Date.ToString("d MMM yyyy");
    public bool IsRecent => DateTime.Now - Date < TimeSpan.FromDays(7);
}

public sealed record Giveaway(int Id, string Title, string Platform, string Worth, string Url, DateTime? Ends, string Image)
{
    public string Detail => $"{Platform}{(Worth is "" or "N/A" ? "" : $" · worth {Worth}")}{(Ends is { } e ? $" · ends {e:d MMM}" : "")}";
}

/// <summary>
/// Public feeds that need no account or key: Steam's news API for patch notes, and GamerPower's giveaway list
/// for free-to-keep PC games (Steam, Epic, GOG and others).
/// </summary>
public static class GameFeeds
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static GameFeeds() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Rig");

    /// <summary>Latest news and patch notes for a Steam game (newest first).</summary>
    public static async Task<List<NewsItem>> SteamNewsAsync(string appId, int count = 8)
    {
        var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count={count}&maxlength=400&format=json&feeds=steam_community_announcements"; // the developers' own posts
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var list = new List<NewsItem>();
        foreach (var n in doc.RootElement.GetProperty("appnews").GetProperty("newsitems").EnumerateArray())
        {
            list.Add(new NewsItem(
                n.GetProperty("title").GetString() ?? "",
                n.GetProperty("url").GetString() ?? "",
                DateTimeOffset.FromUnixTimeSeconds(n.GetProperty("date").GetInt64()).LocalDateTime,
                CleanSummary(n.TryGetProperty("contents", out var c) ? c.GetString() ?? "" : ""),
                n.TryGetProperty("feedlabel", out var f) ? f.GetString() ?? "" : ""));
        }
        return list;
    }

    /// <summary>Steam news bodies contain BBCode/HTML and image placeholders; keep readable text only.</summary>
    static string CleanSummary(string s)
    {
        s = Regex.Replace(s, @"\{STEAM_CLAN_IMAGE\}\S*|\[img\].*?\[/img\]|<[^>]+>|\[/?[a-z0-9*]+(=[^\]]*)?\]", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(System.Net.WebUtility.HtmlDecode(s), @"\s+", " ").Trim();
        return s.Length > 280 ? s[..280].TrimEnd() + "…" : s;
    }

    /// <summary>Free-to-keep PC games running right now.</summary>
    public static async Task<List<Giveaway>> FreeGamesAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://www.gamerpower.com/api/giveaways?platform=pc&type=game&sort-by=date"));
        var list = new List<Giveaway>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var g in doc.RootElement.EnumerateArray())
        {
            string S(string n) => g.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            DateTime? ends = DateTime.TryParse(S("end_date"), out var e) ? e : null;
            if (ends is { } d && d < DateTime.Now) continue;
            // GamerPower titles look like "Game Name (Epic Games) Giveaway".
            var title = Regex.Replace(S("title"), @"\s*\([^)]*\)\s*Giveaway$|\s*Giveaway$", "", RegexOptions.IgnoreCase).Trim();
            list.Add(new Giveaway(g.GetProperty("id").GetInt32(), title, Platform(S("platforms")), S("worth"), S("open_giveaway_url"), ends, S("thumbnail")));
        }
        return list;
    }

    static string Platform(string platforms) =>
        platforms.Contains("Epic", StringComparison.OrdinalIgnoreCase) ? "Epic Games"
        : platforms.Contains("Steam", StringComparison.OrdinalIgnoreCase) ? "Steam"
        : platforms.Contains("GOG", StringComparison.OrdinalIgnoreCase) ? "GOG"
        : platforms.Split(',').FirstOrDefault()?.Trim() ?? "PC";
}
