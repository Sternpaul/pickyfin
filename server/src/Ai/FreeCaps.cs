using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pickyfin.Server.Ai;

/// <summary>
/// Only free use, never a cent: each source stops itself before its free allowance ends.
/// - OpenRouter: only ":free" models are ever asked (see Providers.IsFree).
/// - Cloudflare: 9,000 of the 10,000 free neurons a day (CloudflareBudget).
/// - Voyage: 190 million of the 200 million free tokens per model, per key.
/// - Jina: 9.5 million of the 10 million free tokens, per key.
/// - Gemini: 900 texts a day per model; free only while the key's project has no billing account,
///   which Pickyfin can't see, so the page says so.
/// - NVIDIA: a trial without billing; nothing to count.
/// Counts are kept in a small file in the data folder, so a restart doesn't start from zero.
/// </summary>
public static class FreeCaps
{
    public const long VoyageTokens = 190_000_000, JinaTokens = 9_500_000;
    public const int GeminiTextsPerDay = 900;

    private static readonly object Gate = new();
    private static string? _file;
    private static Dictionary<string, long> _used = new();

    public static void Open(string folder)
    {
        lock (Gate)
        {
            if (_file is not null) return;
            _file = Path.Combine(folder, "free-usage.json");
            try
            {
                if (File.Exists(_file)) _used = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_file)) ?? new();
            }
            catch (Exception) { _used = new(); }
        }
    }

    private static string KeyId(ServerSettings s, string model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Providers.Key(s, model))))[..12];

    /// <summary>Google's free day ends at midnight Pacific time (taken as UTC−8, which is never later).</summary>
    private static DateTime GeminiDayStart => DateTime.UtcNow.AddHours(-8).Date.AddHours(8);
    public static DateTime GeminiResets => GeminiDayStart.AddDays(1);

    private static string? Counter(ServerSettings s, string model)
    {
        var p = Providers.Of(model);
        if (p == Providers.Voyage) return $"voyage|{KeyId(s, model)}|{Providers.ApiModel(model)}";
        if (p == Providers.Jina) return $"jina|{KeyId(s, model)}";
        if (p == Providers.Gemini) return $"gemini|{KeyId(s, model)}|{Providers.ApiModel(model)}|{GeminiDayStart:yyyy-MM-dd}";
        return null;
    }

    private static long Limit(string model) =>
        Providers.Of(model) == Providers.Voyage ? VoyageTokens : Providers.Of(model) == Providers.Jina ? JinaTokens : GeminiTextsPerDay;

    private static long Used(string counter) { lock (Gate) return _used.TryGetValue(counter, out var n) ? n : 0; }

    /// <summary>Why these texts would go past the free allowance; null when they fit.</summary>
    public static string? Blocked(ServerSettings s, string model, IReadOnlyCollection<string> texts)
    {
        if (Providers.Of(model) == Providers.Cloudflare)
            return CloudflareBudget.Fits(model, texts) ? null
                : $"today's free Cloudflare neurons are used up ({CloudflareBudget.UsedToday:N0} of {CloudflareBudget.DailyNeurons:N0} kept for Pickyfin)";
        if (Counter(s, model) is not { } counter) return null;
        var cost = Providers.Of(model) == Providers.Gemini ? texts.Count : CloudflareBudget.EstimateTokens(texts);
        var used = Used(counter);
        if (used + cost <= Limit(model)) return null;
        return Providers.Of(model) == Providers.Gemini
            ? $"today's free Gemini use is used up ({used:N0} of {GeminiTextsPerDay:N0} texts)"
            : $"the free {Providers.Name(model)} tokens are used up ({used:N0} of {Limit(model):N0} kept for Pickyfin)";
    }

    /// <summary>When a blocked model may try again: the next free day, or tomorrow for a used-up allowance (a new key starts afresh).</summary>
    public static DateTime RetryAt(string model) =>
        Providers.Of(model) == Providers.Cloudflare ? CloudflareBudget.Resets
        : Providers.Of(model) == Providers.Gemini ? GeminiResets
        : DateTime.UtcNow.AddDays(1);

    /// <summary>After an answer: counts what it used (the answer's own count when it gives one).</summary>
    public static void Spend(ServerSettings s, string model, IReadOnlyCollection<string> texts, int? tokens)
    {
        if (Providers.Of(model) == Providers.Cloudflare) { CloudflareBudget.Spend(model, tokens ?? CloudflareBudget.EstimateTokens(texts)); return; }
        if (Counter(s, model) is not { } counter) return;
        var cost = Providers.Of(model) == Providers.Gemini ? texts.Count : tokens ?? CloudflareBudget.EstimateTokens(texts);
        lock (Gate)
        {
            _used[counter] = (_used.TryGetValue(counter, out var n) ? n : 0) + cost;
            // old days are dropped
            foreach (var old in _used.Keys.Where(k => k.StartsWith("gemini|") && !k.EndsWith(GeminiDayStart.ToString("yyyy-MM-dd"))).ToList()) _used.Remove(old);
            if (_file is null) return;
            try { File.WriteAllText(_file, JsonSerializer.Serialize(_used)); } catch (Exception) { }
        }
    }

    /// <summary>One line per source with a key, for the AI page: how much of the free allowance is used.</summary>
    public static Dictionary<string, string> Report(ServerSettings s, IEnumerable<string> models)
    {
        var lines = new Dictionary<string, string>();
        if (Providers.HasKey(s, Providers.Cloudflare))
            lines["cf:"] = $"Used today: {CloudflareBudget.UsedToday:N0} of {CloudflareBudget.DailyNeurons:N0} neurons (free: 10,000)";
        if (Providers.HasKey(s, Providers.Gemini))
        {
            var gemini = models.Where(m => Providers.Of(m) == Providers.Gemini).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(m => $"{Providers.ApiModel(m)} {Used(Counter(s, m)!):N0}").ToList();
            lines["gemini:"] = $"Used today, of {GeminiTextsPerDay:N0} texts per model (free: 1,000): " + (gemini.Count > 0 ? string.Join(", ", gemini) : "none yet");
        }
        if (Providers.HasKey(s, Providers.Jina))
            lines["jina:"] = $"Used with this key: {Used(Counter(s, "jina:x")!):N0} of {JinaTokens:N0} tokens (free: 10,000,000)";
        if (Providers.HasKey(s, Providers.Voyage))
        {
            var voyage = models.Where(m => Providers.Of(m) == Providers.Voyage).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(m => $"{Providers.ApiModel(m)} {Used(Counter(s, m)!):N0}").ToList();
            lines["voyage:"] = $"Used with this key, of {VoyageTokens:N0} tokens per model (free: 200,000,000): " + (voyage.Count > 0 ? string.Join(", ", voyage) : "none yet");
        }
        return lines;
    }

    /// <summary>For tests.</summary>
    internal static void Reset() { lock (Gate) { _file = null; _used = new(); } }
}
