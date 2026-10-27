using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Services;

/// <summary>An instant answer in the command palette; Enter copies Copy.</summary>
public sealed record QuickAnswer(string Title, string Detail, string Copy, string Glyph);

/// <summary>
/// Things the palette can answer as you type: unit conversions ("5 ft in cm"), currency ("100 usd to eur", rates
/// from the European Central Bank via frankfurter.app, no key), emoji ("emoji fire" or ":fire") and the time
/// somewhere else ("time in tokyo").
/// </summary>
public static class QuickAnswers
{
    public static IEnumerable<QuickAnswer> For(string query)
    {
        var q = query.Trim();
        if (q.Length < 2) yield break;
        if (Convert(q) is { } conversion) yield return conversion;
        foreach (var e in Emoji(q)) yield return e;
        if (TimeIn(q) is { } time) yield return time;
    }

    // ---------- Units and currency ----------

    static readonly Regex ConvertPattern = new(@"^(-?[\d.,]+)\s*([a-zA-Z°µ²""'/]+(?:\s?[a-zA-Z²]+)?)\s+(?:in|to|as|into|=)\s+([a-zA-Z°µ²""'/]+(?:\s?[a-zA-Z²]+)?)$", RegexOptions.IgnoreCase);

    /// <summary>Unit → (kind, factor to the base unit). Temperatures are handled separately.</summary>
    static readonly Dictionary<string, (string Kind, double Factor)> Units = Build();

    static Dictionary<string, (string, double)> Build()
    {
        var d = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
        void Add(string kind, double factor, params string[] names) { foreach (var n in names) d[n] = (kind, factor); }
        Add("length", 0.001, "mm", "millimeter", "millimeters", "millimetre", "millimetres");
        Add("length", 0.01, "cm", "centimeter", "centimeters", "centimetre", "centimetres");
        Add("length", 1, "m", "meter", "meters", "metre", "metres");
        Add("length", 1000, "km", "kilometer", "kilometers", "kilometre", "kilometres");
        Add("length", 0.0254, "in", "inch", "inches", "\"");
        Add("length", 0.3048, "ft", "foot", "feet", "'");
        Add("length", 0.9144, "yd", "yard", "yards");
        Add("length", 1609.344, "mi", "mile", "miles");
        Add("mass", 0.001, "g", "gram", "grams");
        Add("mass", 0.000001, "mg", "milligram", "milligrams");
        Add("mass", 1, "kg", "kilo", "kilos", "kilogram", "kilograms");
        Add("mass", 0.028349523125, "oz", "ounce", "ounces");
        Add("mass", 0.45359237, "lb", "lbs", "pound", "pounds");
        Add("mass", 6.35029318, "st", "stone", "stones");
        Add("mass", 1000, "t", "tonne", "tonnes");
        Add("volume", 0.001, "ml", "milliliter", "milliliters", "millilitre", "millilitres");
        Add("volume", 1, "l", "liter", "liters", "litre", "litres");
        Add("volume", 3.785411784, "gal", "gallon", "gallons");
        Add("volume", 0.946352946, "qt", "quart", "quarts");
        Add("volume", 0.473176473, "pt", "pint", "pints");
        Add("volume", 0.2365882365, "cup", "cups");
        Add("volume", 0.0295735295625, "floz", "fl oz");
        Add("volume", 0.01478676478125, "tbsp", "tablespoon", "tablespoons");
        Add("volume", 0.00492892159375, "tsp", "teaspoon", "teaspoons");
        Add("speed", 1 / 3.6, "kmh", "km/h", "kph");
        Add("speed", 0.44704, "mph");
        Add("speed", 1, "m/s", "mps");
        Add("speed", 0.514444, "kn", "knot", "knots");
        Add("data", 1, "b", "byte", "bytes");
        Add("data", 1e3, "kb", "kilobyte", "kilobytes");
        Add("data", 1e6, "mb", "megabyte", "megabytes");
        Add("data", 1e9, "gb", "gigabyte", "gigabytes");
        Add("data", 1e12, "tb", "terabyte", "terabytes");
        Add("data", 1024, "kib");
        Add("data", 1024d * 1024, "mib");
        Add("data", 1024d * 1024 * 1024, "gib");
        Add("data", 1024d * 1024 * 1024 * 1024, "tib");
        Add("time", 0.001, "ms", "millisecond", "milliseconds");
        Add("time", 1, "s", "sec", "secs", "second", "seconds");
        Add("time", 60, "min", "mins", "minute", "minutes");
        Add("time", 3600, "h", "hr", "hrs", "hour", "hours");
        Add("time", 86400, "day", "days");
        Add("time", 604800, "week", "weeks");
        Add("area", 1, "m2", "m²", "sqm");
        Add("area", 1e6, "km2", "km²");
        Add("area", 0.09290304, "ft2", "ft²", "sqft");
        Add("area", 4046.8564224, "acre", "acres");
        Add("area", 10000, "ha", "hectare", "hectares");
        return d;
    }

    static readonly Dictionary<string, string> Temperatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = "C", ["°c"] = "C", ["celsius"] = "C", ["f"] = "F", ["°f"] = "F", ["fahrenheit"] = "F", ["k"] = "K", ["kelvin"] = "K",
    };

    static QuickAnswer? Convert(string q)
    {
        var m = ConvertPattern.Match(q);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        string from = m.Groups[2].Value.Trim(), to = m.Groups[3].Value.Trim();

        if (Temperatures.TryGetValue(from, out var tf) && Temperatures.TryGetValue(to, out var tt))
        {
            double c = tf switch { "F" => (value - 32) * 5 / 9, "K" => value - 273.15, _ => value };
            double r = tt switch { "F" => c * 9 / 5 + 32, "K" => c + 273.15, _ => c };
            return Answer(value, $"°{tf}".Replace("°K", "K"), r, $"°{tt}".Replace("°K", "K"), "Temperature");
        }
        if (Units.TryGetValue(from, out var uf) && Units.TryGetValue(to, out var ut) && uf.Kind == ut.Kind)
            return Answer(value, from, value * uf.Factor / ut.Factor, to, char.ToUpper(uf.Kind[0]) + uf.Kind[1..]);

        if (from.Length == 3 && to.Length == 3 && from.All(char.IsLetter) && to.All(char.IsLetter))
        {
            var rates = Rates();
            if (rates is null) { _ = LoadRatesAsync(); return new QuickAnswer("Getting exchange rates…", "Keep typing in a second", "", ""); }
            if (rates.TryGetValue(from.ToUpperInvariant(), out var rf) && rates.TryGetValue(to.ToUpperInvariant(), out var rt))
                return new QuickAnswer($"{value:#,0.##} {from.ToUpperInvariant()} = {value / rf * rt:#,0.##} {to.ToUpperInvariant()}",
                    $"Exchange rate from {_ratesDate} (ECB) · Enter copies", (value / rf * rt).ToString("0.##", CultureInfo.CurrentCulture), "");
        }
        return null;
    }

    static QuickAnswer Answer(double value, string from, double result, string to, string kind)
    {
        string r = Math.Abs(result) >= 1e6 || (Math.Abs(result) < 0.001 && result != 0) ? result.ToString("0.###e0") : result.ToString("#,0.####");
        return new QuickAnswer($"{value:#,0.####} {from} = {r} {to}", $"{kind} · Enter copies the number", r.Replace(",", ""), "");
    }

    static Dictionary<string, double>? _rates;
    static string _ratesDate = "";
    static DateTime _ratesAt;
    static Task? _loading;

    static Dictionary<string, double>? Rates() => _rates is not null && DateTime.Now - _ratesAt < TimeSpan.FromHours(12) ? _rates : null;

    /// <summary>Rates per 1 EUR, loaded once and kept for 12 hours.</summary>
    static Task LoadRatesAsync() => _loading ??= Task.Run(async () =>
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Rig");
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.frankfurter.app/latest"));
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 1 };
            foreach (var p in doc.RootElement.GetProperty("rates").EnumerateObject()) rates[p.Name] = p.Value.GetDouble();
            _ratesDate = doc.RootElement.GetProperty("date").GetString() ?? "";
            _rates = rates;
            _ratesAt = DateTime.Now;
        }
        catch { }
        finally { _loading = null; }
    });

    // ---------- Emoji ----------

    static readonly (string Emoji, string Names)[] EmojiList =
    [
        ("😀", "grin smile happy"), ("😂", "joy laugh tears lol"), ("🤣", "rofl laugh rolling"), ("😊", "blush smile happy"), ("😍", "heart eyes love"),
        ("😘", "kiss love"), ("😎", "cool sunglasses"), ("🤔", "thinking hmm"), ("😐", "neutral meh"), ("🙄", "eye roll"), ("😴", "sleep tired zzz"),
        ("😭", "sob cry crying"), ("😢", "cry sad tear"), ("😡", "angry mad rage"), ("🤯", "mind blown exploding"), ("😱", "scream shock"),
        ("🥶", "cold freezing"), ("🥵", "hot sweating"), ("🤡", "clown"), ("💀", "skull dead dying"), ("👻", "ghost boo"), ("🤖", "robot bot"),
        ("😈", "devil evil"), ("🥳", "party celebrate"), ("🥺", "pleading puppy eyes"), ("😬", "grimace awkward"), ("🤫", "shush quiet"),
        ("🤝", "handshake deal"), ("👍", "thumbs up like yes ok"), ("👎", "thumbs down dislike no"), ("👏", "clap applause"), ("🙏", "pray please thanks"),
        ("👋", "wave hello hi bye"), ("✌️", "peace victory"), ("🤞", "fingers crossed luck"), ("💪", "muscle strong flex"), ("👀", "eyes look"),
        ("🧠", "brain smart"), ("❤️", "heart love red"), ("🧡", "orange heart"), ("💛", "yellow heart"), ("💚", "green heart"), ("💙", "blue heart"),
        ("💜", "purple heart"), ("🖤", "black heart"), ("💔", "broken heart"), ("🔥", "fire lit hot flame"), ("✨", "sparkles shiny"), ("⭐", "star"),
        ("🌟", "glowing star"), ("⚡", "lightning zap bolt"), ("💥", "boom collision"), ("💯", "hundred 100 perfect"), ("✅", "check done yes tick"),
        ("❌", "cross no wrong x"), ("⚠️", "warning caution"), ("❓", "question"), ("❗", "exclamation"), ("🎉", "tada party celebrate confetti"),
        ("🎁", "gift present"), ("🎂", "cake birthday"), ("🏆", "trophy win winner"), ("🥇", "gold medal first"), ("🎮", "game controller gaming"),
        ("🕹️", "joystick arcade"), ("🎧", "headphones music"), ("🎵", "music note"), ("🎤", "microphone mic sing"), ("📷", "camera photo"),
        ("🎬", "clapper movie film"), ("📺", "tv television"), ("💻", "laptop computer pc"), ("🖥️", "desktop computer monitor pc"), ("⌨️", "keyboard"),
        ("🖱️", "mouse"), ("📱", "phone mobile"), ("🔋", "battery"), ("🔌", "plug power"), ("💾", "floppy save disk"), ("📁", "folder"),
        ("📝", "memo note write"), ("📌", "pin pushpin"), ("📎", "paperclip"), ("🔒", "lock locked"), ("🔑", "key"), ("🛠️", "tools hammer wrench"),
        ("⚙️", "gear settings cog"), ("🧹", "broom clean"), ("🗑️", "trash bin delete"), ("📈", "chart up increase stonks"), ("📉", "chart down decrease"),
        ("💰", "money bag"), ("💸", "money flying spend"), ("🛒", "cart shopping"), ("⏰", "alarm clock"), ("⏳", "hourglass wait"), ("📅", "calendar date"),
        ("🚀", "rocket launch fast"), ("✈️", "plane airplane travel"), ("🚗", "car"), ("🏠", "house home"), ("🌍", "earth globe world"), ("☀️", "sun sunny"),
        ("🌙", "moon night"), ("⛈️", "storm thunder"), ("❄️", "snow snowflake cold"), ("🌈", "rainbow"), ("🌊", "wave ocean water"), ("🍕", "pizza"),
        ("🍔", "burger hamburger"), ("🍟", "fries"), ("🌮", "taco"), ("🍜", "ramen noodles"), ("🍣", "sushi"), ("🍩", "donut"), ("🍪", "cookie"),
        ("☕", "coffee tea hot"), ("🍺", "beer"), ("🥤", "drink soda"), ("🍎", "apple"), ("🍌", "banana"), ("🐶", "dog puppy"), ("🐱", "cat kitten"),
        ("🦊", "fox"), ("🐸", "frog"), ("🐍", "snake"), ("🐐", "goat goated"), ("🦈", "shark"), ("🐉", "dragon"), ("🍥", "fish cake naruto"),
        ("🍃", "leaf wind"), ("🌸", "cherry blossom flower"), ("⚔️", "swords fight battle"), ("🛡️", "shield"), ("🎯", "target bullseye aim"),
        ("🧊", "ice cube"), ("🫡", "salute"), ("🤷", "shrug idk"), ("🤦", "facepalm"), ("🙌", "raised hands yay"), ("🫠", "melting"), ("🥲", "smiling tear"),
    ];

    static IEnumerable<QuickAnswer> Emoji(string q)
    {
        string? term = q.StartsWith(':') && q.Length > 2 ? q[1..] : q.StartsWith("emoji ", StringComparison.OrdinalIgnoreCase) ? q[6..] : null;
        if (string.IsNullOrWhiteSpace(term)) yield break;
        term = term.Trim().ToLowerInvariant();
        foreach (var (e, names) in EmojiList.Where(x => x.Names.Split(' ').Any(n => n.StartsWith(term))).Take(6))
            yield return new QuickAnswer($"{e}  {names.Split(' ')[0]}", "Emoji · Enter copies it", e, "");
    }

    // ---------- Time zones ----------

    static readonly Dictionary<string, string> Cities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["london"] = "GMT Standard Time", ["uk"] = "GMT Standard Time", ["dublin"] = "GMT Standard Time", ["paris"] = "Romance Standard Time",
        ["berlin"] = "W. Europe Standard Time", ["amsterdam"] = "W. Europe Standard Time", ["rome"] = "W. Europe Standard Time", ["madrid"] = "Romance Standard Time",
        ["stockholm"] = "W. Europe Standard Time", ["athens"] = "GTB Standard Time", ["istanbul"] = "Turkey Standard Time", ["moscow"] = "Russian Standard Time",
        ["dubai"] = "Arabian Standard Time", ["india"] = "India Standard Time", ["mumbai"] = "India Standard Time", ["delhi"] = "India Standard Time",
        ["bangkok"] = "SE Asia Standard Time", ["singapore"] = "Singapore Standard Time", ["hong kong"] = "China Standard Time", ["beijing"] = "China Standard Time",
        ["shanghai"] = "China Standard Time", ["tokyo"] = "Tokyo Standard Time", ["japan"] = "Tokyo Standard Time", ["seoul"] = "Korea Standard Time",
        ["korea"] = "Korea Standard Time", ["sydney"] = "AUS Eastern Standard Time", ["melbourne"] = "AUS Eastern Standard Time", ["perth"] = "W. Australia Standard Time",
        ["auckland"] = "New Zealand Standard Time", ["new york"] = "Eastern Standard Time", ["nyc"] = "Eastern Standard Time", ["est"] = "Eastern Standard Time",
        ["toronto"] = "Eastern Standard Time", ["miami"] = "Eastern Standard Time", ["chicago"] = "Central Standard Time", ["cst"] = "Central Standard Time",
        ["texas"] = "Central Standard Time", ["denver"] = "Mountain Standard Time", ["mst"] = "Mountain Standard Time", ["phoenix"] = "US Mountain Standard Time",
        ["los angeles"] = "Pacific Standard Time", ["la"] = "Pacific Standard Time", ["pst"] = "Pacific Standard Time", ["seattle"] = "Pacific Standard Time",
        ["san francisco"] = "Pacific Standard Time", ["vancouver"] = "Pacific Standard Time", ["hawaii"] = "Hawaiian Standard Time", ["alaska"] = "Alaskan Standard Time",
        ["mexico city"] = "Central Standard Time (Mexico)", ["sao paulo"] = "E. South America Standard Time", ["brazil"] = "E. South America Standard Time",
        ["buenos aires"] = "Argentina Standard Time", ["cairo"] = "Egypt Standard Time", ["johannesburg"] = "South Africa Standard Time", ["lagos"] = "W. Central Africa Standard Time",
        ["utc"] = "UTC", ["gmt"] = "GMT Standard Time",
    };

    static readonly Regex TimePattern = new(@"^(?:time in|what time is it in|time)\s+(.+)$|^(.+?)\s+time$", RegexOptions.IgnoreCase);

    static QuickAnswer? TimeIn(string q)
    {
        var m = TimePattern.Match(q);
        if (!m.Success) return null;
        var place = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
        if (!Cities.TryGetValue(place, out var id)) return null;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            var there = TimeZoneInfo.ConvertTime(DateTime.Now, tz);
            var diff = there - DateTime.Now;
            string offset = Math.Abs(diff.TotalMinutes) < 1 ? "same as here" : $"{(diff > TimeSpan.Zero ? "+" : "−")}{Math.Abs(diff.TotalHours):0.#} h from here";
            return new QuickAnswer($"{there:t} in {CultureInfo.CurrentCulture.TextInfo.ToTitleCase(place)}", $"{there:dddd d MMM} · {offset}", there.ToString("t"), "");
        }
        catch { return null; }
    }
}
