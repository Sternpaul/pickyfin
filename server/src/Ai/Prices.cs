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

    // LiteLLM's community price catalog (public, refreshed daily): every model Mistral, Gemini and
    // Cloudflare offer, not just the families above. Read once a day; only those sources are kept.
    private const string CatalogUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    private static Dictionary<string, (double In, double Out)> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _catalogAt;
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static readonly string[] CatalogSources = { "mistral/", "gemini/", "cloudflare/" };

    private static string? CatalogPrefix(string model) =>
        Providers.Of(model) == Providers.Mistral ? "mistral/" : Providers.Of(model) == Providers.Gemini ? "gemini/" : Providers.Of(model) == Providers.Cloudflare ? "cloudflare/" : null;

    /// <summary>The catalog's price for a model, or null.</summary>
    private static async Task<Price?> FromCatalogAsync(string model, IHttpClientFactory http, ILogger logger, CancellationToken ct)
    {
        if (CatalogPrefix(model) is not { } prefix) return null;
        await LoadCatalogAsync(http, logger, ct);
        var id = Providers.ApiModel(model);
        return _catalog.TryGetValue(prefix + id, out var p) ? new(p.In, p.Out) : null;
    }

    private static async Task LoadCatalogAsync(IHttpClientFactory http, ILogger logger, CancellationToken ct)
    {
        if (DateTime.UtcNow - _catalogAt < TimeSpan.FromDays(1)) return;
        await CatalogGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _catalogAt < TimeSpan.FromDays(1)) return;
            _catalogAt = DateTime.UtcNow; // tried once a day, also when it fails
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
            request.Headers.UserAgent.ParseAdd("Pickyfin");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            _catalog = Catalog(doc.RootElement);
            logger.LogInformation("Prices: read {Count} models' prices from the LiteLLM catalog", _catalog.Count);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("Prices: couldn't read the LiteLLM catalog ({Error}); using the built-in prices", error.Message);
        }
        finally { CatalogGate.Release(); }
    }

    /// <summary>Reads the catalog's "provider/model": {input_cost_per_token, output_cost_per_token} into dollars per million.</summary>
    public static Dictionary<string, (double In, double Out)> Catalog(JsonElement root)
    {
        var prices = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in root.EnumerateObject())
        {
            if (!CatalogSources.Any(p => entry.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || entry.Value.ValueKind != JsonValueKind.Object) continue;
            static double? Per(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() * 1_000_000 : null;
            if (Per(entry.Value, "input_cost_per_token") is { } input) prices[entry.Name] = (Math.Round(input, 6), Math.Round(Per(entry.Value, "output_cost_per_token") ?? 0, 6));
        }
        return prices;
    }

    // Cloudflare tells its prices itself: its model search answers in OpenRouter's format with "pricing"
    private static Dictionary<string, (double In, double Out)> _cloudflare = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _cloudflareAt;

    private static async Task<Price?> FromCloudflareAsync(string model, IHttpClientFactory http, ILogger logger, CancellationToken ct)
    {
        var s = ServerSettings.Current;
        if (!Providers.HasKey(s, model)) return null;
        if (DateTime.UtcNow - _cloudflareAt > TimeSpan.FromDays(1))
        {
            _cloudflareAt = DateTime.UtcNow;
            try
            {
                var client = http.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(s.CloudflareAccountId.Trim())}/ai/models/search?format=openrouter&per_page=500");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Providers.Key(s, model));
                using var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                _cloudflare = CloudflareList(doc.RootElement);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogInformation("Prices: couldn't read Cloudflare's prices ({Error}); using the price list", error.Message);
            }
        }
        return _cloudflare.TryGetValue(Providers.ApiModel(model), out var p) ? new(p.In, p.Out) : null;
    }

    /// <summary>Cloudflare's model search in OpenRouter's format: {"data":[{"id":"@cf/…","pricing":{…}}]} (also inside "result").</summary>
    public static Dictionary<string, (double In, double Out)> CloudflareList(JsonElement root)
    {
        var prices = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        var list = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
            : root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("data", out var rd) ? rd
            : root.TryGetProperty("result", out var ra) && ra.ValueKind == JsonValueKind.Array ? ra : default;
        if (list.ValueKind != JsonValueKind.Array) return prices;
        foreach (var m in list.EnumerateArray())
        {
            if (OpenRouterModels.PriceOf(m) is not { } price) continue;
            foreach (var name in new[] { "id", "name", "canonical_slug" })
                if (m.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } id) prices[id] = price;
        }
        return prices;
    }

    /// <summary>For tests: a catalog without fetching it.</summary>
    internal static void UseCatalog(Dictionary<string, (double In, double Out)> catalog) { _catalog = catalog; _catalogAt = DateTime.UtcNow; }

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
                ? (await FromCloudflareAsync(model, http, logger, ct) ?? new(CloudflareBudget.NeuronsPerMillion(model) * 0.011 / 1000, 0)) with { Note = "free within 10,000 neurons a day; above that only on Workers Paid" }
                : (await FromCloudflareAsync(model, http, logger, ct) ?? await FromCatalogAsync(model, http, logger, ct)) is { } c ? c with { Note = "free within 10,000 neurons a day; above that only on Workers Paid" } : null;
        if (source == Providers.Gemini)
            return (await FromCatalogAsync(model, http, logger, ct) ?? (GeminiEmbedding.TryGetValue(id, out var g) ? new(g, 0) : null)) is { } gp
                ? gp with { Note = "free on a project without billing" } : null;
        if (source == Providers.Jina) return new(0, 0, "free tokens; stops when they're used up");
        if (source == Providers.Voyage) return new(0, 0, "200 million free tokens per model; a card on file is charged after them");
        if (source == Providers.Mistral)
            return (await FromCatalogAsync(model, http, logger, ct)
                ?? (MistralTable.FirstOrDefault(t => id.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase)) is { Prefix: not null } m ? new(m.In, m.Out) : null)) is { } mp
                ? mp with { Note = "paid (or from a Vibe Pro plan's API credit)" } : null;
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
