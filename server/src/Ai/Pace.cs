namespace Pickyfin.Server.Ai;

/// <summary>
/// Per-minute limits of the free tiers, kept by waiting instead of running into "429 too many":
/// before a request, Pickyfin waits until it fits in the last minute's requests and tokens.
/// 90% of each limit, since tokens are estimated (about three characters each).
/// - Voyage without a card: 3 requests and 10,000 tokens a minute
/// - Gemini free tier: 100 texts and 30,000 tokens a minute per model (each text counts as a request)
/// - Jina free key: 100 requests and 100,000 tokens a minute
/// - NVIDIA: about 40 requests a minute
/// </summary>
public static class Pace
{
    private sealed record Limit(int Requests, int Tokens, bool EachTextCounts);

    private static Limit? For(string model)
    {
        var p = Providers.Of(model);
        if (p == Providers.Voyage) return new(3, 9_000, false);
        if (p == Providers.Gemini) return new(90, 27_000, true);
        if (p == Providers.Jina) return new(90, 90_000, false);
        if (p == Providers.Nvidia) return new(36, int.MaxValue, false);
        return null;
    }

    private static readonly Dictionary<string, List<(DateTime At, int Requests, int Tokens)>> Windows = new();

    /// <summary>Waits until this request fits in its source's last minute (never longer than a minute).</summary>
    public static async Task WaitAsync(string model, int texts, int tokens, CancellationToken ct)
    {
        if (For(model) is not { } limit) return;
        var requests = limit.EachTextCounts ? texts : 1;
        // Gemini and NVIDIA count per model; Voyage and Jina per account
        var source = Providers.Of(model) == Providers.Gemini || Providers.Of(model) == Providers.Nvidia ? model : Providers.Name(model);
        while (true)
        {
            TimeSpan wait;
            lock (Windows)
            {
                var now = DateTime.UtcNow;
                if (!Windows.TryGetValue(source, out var window)) Windows[source] = window = new();
                window.RemoveAll(w => now - w.At >= TimeSpan.FromMinutes(1));
                // an empty minute always lets one request through, however big
                if (window.Count == 0 || (window.Sum(w => w.Requests) + requests <= limit.Requests && window.Sum(w => (long)w.Tokens) + tokens <= limit.Tokens))
                {
                    window.Add((now, requests, tokens));
                    return;
                }
                wait = window[0].At.AddMinutes(1) - now + TimeSpan.FromMilliseconds(250);
            }
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(250), ct);
        }
    }

    /// <summary>For tests.</summary>
    internal static void Reset() { lock (Windows) Windows.Clear(); }
}
