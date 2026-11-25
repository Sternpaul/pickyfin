using System.Text.Json;

namespace Pickyfin.Server.Ai;

/// <summary>
/// What a model costs, in US dollars per million tokens, for a manual run's estimate and its
/// spending limit. Null when Pickyfin doesn't know the price; such a model is shown as
/// "price unknown" and the limit can't count it.
/// </summary>
public static class Prices
{
    /// <param name="Note">Said next to the price, e.g. "free within 10,000 neurons a day".</param>
    public sealed record Price(double In, double Out, string? Note = null);

    // Mistral's list prices (dollars per million tokens, input/output); the first matching prefix counts.
    // Checked against an invoice: mistral-embed 0.10, codestral-embed 0.15.
    private static readonly (string Prefix, double In, double Out)[] MistralTable =
    {
        ("codestral-embed", 0.15, 0), ("mistral-embed", 0.10, 0),
        ("ministral-3b", 0.10, 0.10), ("ministral-8b", 0.15, 0.15), ("ministral-14b", 0.20, 0.20),
        ("mistral-small", 0.15, 0.60), ("codestral", 0.30, 0.90), ("mistral-medium", 1.50, 7.50), ("mistral-large", 0.70, 2.10),
    };

    // Gemini's paid-tier prices; on a project without billing they're free
    private static readonly Dictionary<string, double> GeminiEmbedding = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-embedding-001"] = 0.15, ["gemini-embedding-2"] = 0.20,
    };

    private static Dictionary<string, (double In, double Out)> _openRouterEmbedding = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _openRouterEmbeddingAt;

    /// <summary>The price of a model; reads OpenRouter's lists when needed.</summary>
    public static async Task<Price?> ForAsync(string model, IHttpClientFactory http, ILogger logger, CancellationToken ct)
    {
        var source = Providers.Of(model);
        var id = Providers.ApiModel(model);
        if (source == Providers.Nvidia) return new(0, 0, "free (NVIDIA's trial)");
        if (source == Providers.Cloudflare)
            return id.Contains("embed", StringComparison.OrdinalIgnoreCase) || id.Contains("bge", StringComparison.OrdinalIgnoreCase)
                ? new(CloudflareBudget.NeuronsPerMillion(model) * 0.011 / 1000, 0, "free within 10,000 neurons a day; above that only on Workers Paid")
                : null;
        if (source == Providers.Gemini)
            return GeminiEmbedding.TryGetValue(id, out var g) ? new(g, 0, "free on a project without billing") : null;
        if (source == Providers.Jina) return new(0, 0, "free tokens; stops when they're used up");
        if (source == Providers.Voyage) return new(0, 0, "200 million free tokens per model; a card on file is charged after them");
        if (source == Providers.Mistral)
            return MistralTable.FirstOrDefault(t => id.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase)) is { Prefix: not null } m
                ? new(m.In, m.Out, "paid (or from a Vibe Pro plan's API credit)") : null;
        // OpenRouter
        if (model.EndsWith(":free", StringComparison.OrdinalIgnoreCase)) return new(0, 0, "free");
        await OpenRouterModels.GetAsync(http.CreateClient(), logger, ct);
        if (OpenRouterModels.Prices.TryGetValue(model, out var p)) return new(p.In, p.Out);
        await RefreshOpenRouterEmbeddingAsync(http, logger, ct);
        return _openRouterEmbedding.TryGetValue(model, out var e) ? new(e.In, e.Out) : null;
    }

    private static async Task RefreshOpenRouterEmbeddingAsync(IHttpClientFactory http, ILogger logger, CancellationToken ct)
    {
        if (DateTime.UtcNow - _openRouterEmbeddingAt < TimeSpan.FromHours(6)) return;
        _openRouterEmbeddingAt = DateTime.UtcNow;
        try
        {
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var doc = JsonDocument.Parse(await client.GetStringAsync("https://openrouter.ai/api/v1/models?output_modalities=embeddings", ct));
            var prices = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                if (m.TryGetProperty("id", out var idEl) && idEl.GetString() is { } mid && OpenRouterModels.PriceOf(m) is { } price) prices[mid] = price;
            _openRouterEmbedding = prices;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("Couldn't read OpenRouter's embedding prices: {Error}", error.Message);
        }
    }

    /// <summary>Dollars for a call.</summary>
    public static double Cost(Price price, int input, int output) => (input * price.In + output * price.Out) / 1_000_000;

    /// <summary>For tests.</summary>
    internal static void Reset() { _openRouterEmbedding = new(StringComparer.OrdinalIgnoreCase); _openRouterEmbeddingAt = default; }
}
