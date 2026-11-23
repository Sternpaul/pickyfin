using System.Text.Json;

namespace Pickyfin.Server.Ai;

/// <summary>
/// Cloudflare gives 10,000 "neurons" a day for free, for the whole account, reset at 00:00 UTC.
/// On the Workers Paid plan anything above is billed, so Pickyfin stops itself well before:
/// it counts what it sends (Cloudflare's tokens when the answer says, an estimate otherwise)
/// and keeps the count in a small file, so a restart doesn't start from zero.
/// </summary>
public static class CloudflareBudget
{
    /// <summary>Of the free 10,000: room left for estimates and anything else on the account.</summary>
    public const double DailyNeurons = 9000;

    // Cloudflare's price list, neurons per million input tokens
    private static readonly Dictionary<string, double> PerMillion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@cf/baai/bge-small-en-v1.5"] = 1841, ["@cf/baai/bge-base-en-v1.5"] = 6058, ["@cf/baai/bge-large-en-v1.5"] = 18582,
        ["@cf/baai/bge-m3"] = 1075, ["@cf/pfnet/plamo-embedding-1b"] = 1689, ["@cf/qwen/qwen3-embedding-0.6b"] = 1075,
        ["@cf/google/embeddinggemma-300m"] = 154, ["@cf/google/embeddinggemma-2"] = 154,
    };
    /// <summary>A model not on the list counts as the dearest one.</summary>
    public static double NeuronsPerMillion(string model) => PerMillion.TryGetValue(Providers.ApiModel(model), out var n) ? n : 18582;

    private static readonly object Gate = new();
    private static string? _file;
    private static string _day = "";
    private static double _used;

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");
    /// <summary>When the free neurons come back: the next 00:00 UTC.</summary>
    public static DateTime Resets => DateTime.UtcNow.Date.AddDays(1);

    /// <summary>Where the count is kept (the data folder); read once.</summary>
    public static void Open(string folder)
    {
        lock (Gate)
        {
            if (_file is not null) return;
            _file = Path.Combine(folder, "cloudflare-neurons.json");
            try
            {
                if (!File.Exists(_file)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(_file));
                if (doc.RootElement.GetProperty("Day").GetString() == Today)
                    (_day, _used) = (Today, doc.RootElement.GetProperty("Neurons").GetDouble());
            }
            catch (Exception) { /* a broken file counts as nothing used; the margin covers it */ }
        }
    }

    public static double UsedToday { get { lock (Gate) return _day == Today ? _used : 0; } }

    /// <summary>Tokens a text is likely to be: about three characters each (on the safe side).</summary>
    public static int EstimateTokens(IEnumerable<string> texts) => texts.Sum(t => t.Length / 3 + 1);

    public static double Neurons(string model, int tokens) => tokens * NeuronsPerMillion(model) / 1_000_000;

    /// <summary>Whether sending these texts stays inside today's free neurons.</summary>
    public static bool Fits(string model, IReadOnlyCollection<string> texts) =>
        UsedToday + Neurons(model, EstimateTokens(texts)) <= DailyNeurons;

    public static void Spend(string model, int tokens)
    {
        lock (Gate)
        {
            if (_day != Today) (_day, _used) = (Today, 0);
            _used += Neurons(model, tokens);
            if (_file is null) return;
            try { File.WriteAllText(_file, JsonSerializer.Serialize(new { Day = _day, Neurons = Math.Round(_used, 2) })); }
            catch (Exception) { }
        }
    }

    /// <summary>For tests.</summary>
    internal static void Reset() { lock (Gate) { _file = null; _day = ""; _used = 0; } }
}
