using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Pickyfin.Server.Ai;

/// <summary>
/// Where a model is asked. Model orders can mix sources: a model is written "prefix:model"
/// ("nim:nvidia/…", "cf:@cf/baai/bge-m3", "gemini:gemini-embedding-001", "jina:…", "mistral:…");
/// a model without a known prefix goes to OpenRouter. Every source speaks the OpenAI format.
/// </summary>
public static class Providers
{
    /// <summary>One source of models besides OpenRouter.</summary>
    public sealed record Source(string Prefix, string Name, string KeyUrl, int Batch, string[] Embedding);

    public static readonly Source Nvidia = new("nim:", "NVIDIA", Nim.KeyUrl, 16, Array.Empty<string>());
    public static readonly Source Cloudflare = new("cf:", "Cloudflare", "https://dash.cloudflare.com/profile/api-tokens", 16, new[]
    {
        "@cf/baai/bge-m3", "@cf/qwen/qwen3-embedding-0.6b", "@cf/google/embeddinggemma-300m", "@cf/pfnet/plamo-embedding-1b",
        "@cf/baai/bge-large-en-v1.5", "@cf/baai/bge-base-en-v1.5", "@cf/baai/bge-small-en-v1.5",
    });
    public static readonly Source Gemini = new("gemini:", "Gemini", "https://aistudio.google.com/apikey", 16, new[] { "gemini-embedding-001", "gemini-embedding-2" });
    public static readonly Source Jina = new("jina:", "Jina", "https://jina.ai/api-dashboard", 16, new[] { "jina-embeddings-v5-text-small", "jina-embeddings-v5-text-nano" });
    // Mistral counts tokens per request, not per text: smaller batches
    public static readonly Source Mistral = new("mistral:", "Mistral", "https://console.mistral.ai/api-keys", 8, new[] { "mistral-embed" });

    public static readonly Source[] All = { Nvidia, Cloudflare, Gemini, Jina, Mistral };
    /// <summary>The sources that only make vectors here (NVIDIA also talks; its list comes from NVIDIA).</summary>
    public static readonly Source[] EmbeddingOnly = { Cloudflare, Gemini, Jina, Mistral };

    public static Source? Of(string model) => All.FirstOrDefault(p => model.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase));
    public static bool IsOpenRouter(string model) => Of(model) is null;
    public static string Name(string model) => Of(model)?.Name ?? "OpenRouter";
    /// <summary>The model's id at its source (the prefix taken off).</summary>
    public static string ApiModel(string model) => Of(model) is { } p ? model[p.Prefix.Length..] : model;
    public static int Batch(string model) => Of(model)?.Batch ?? 16;

    /// <summary>The key for this model's source; empty when none is set (Cloudflare also needs its account id).</summary>
    public static string Key(ServerSettings s, string model)
    {
        var p = Of(model);
        if (p == Cloudflare && string.IsNullOrWhiteSpace(s.CloudflareAccountId)) return "";
        return (p == Nvidia ? s.NimApiKey : p == Cloudflare ? s.CloudflareApiToken : p == Gemini ? s.GeminiApiKey
            : p == Jina ? s.JinaApiKey : p == Mistral ? s.MistralApiKey : s.ApiKey).Trim();
    }
    public static bool HasKey(ServerSettings s, string model) => Key(s, model).Length > 0;
    public static bool HasKey(ServerSettings s, Source source) => HasKey(s, source.Prefix + "x");
    /// <summary>A key that can answer a chat order (OpenRouter or NVIDIA).</summary>
    public static bool AnyKey(ServerSettings s) => !string.IsNullOrWhiteSpace(s.ApiKey) || !string.IsNullOrWhiteSpace(s.NimApiKey);
    /// <summary>A key for any source of embedding models.</summary>
    public static bool AnyEmbeddingKey(ServerSettings s) => AnyKey(s) || EmbeddingOnly.Any(p => HasKey(s, p));

    public static string BaseUrl(ServerSettings s, string model)
    {
        var p = Of(model);
        if (p == Nvidia) return Nim.BaseUrl;
        if (p == Cloudflare) return $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(s.CloudflareAccountId.Trim())}/ai/v1";
        if (p == Gemini) return "https://generativelanguage.googleapis.com/v1beta/openai";
        if (p == Jina) return "https://api.jina.ai/v1";
        if (p == Mistral) return "https://api.mistral.ai/v1";
        return "https://openrouter.ai/api/v1";
    }

    public static string ChatUrl(ServerSettings s, string model) =>
        IsOpenRouter(model) && !string.IsNullOrWhiteSpace(s.CustomEndpoint) ? s.CustomEndpoint : BaseUrl(s, model) + "/chat/completions";

    public static string EmbedUrl(ServerSettings s, string model) => BaseUrl(s, model) + "/embeddings";

    public static void Authorize(HttpRequestMessage request, ServerSettings s, string model)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key(s, model));
        if (!IsOpenRouter(model)) return;
        request.Headers.Add("HTTP-Referer", "https://github.com/Sternpaul/pickyfin");
        request.Headers.Add("X-Title", "Pickyfin");
    }

    // ---- a key that was refused: that source's models wait an hour, the others carry on ----
    private static readonly ConcurrentDictionary<string, (DateTime Until, string Why)> Refused = new();
    public static void Refuse(string model, string why) => Refused[Name(model)] = (DateTime.UtcNow.AddHours(1), why);
    public static string? RefusedWhy(string model) =>
        Refused.TryGetValue(Name(model), out var r) && r.Until > DateTime.UtcNow ? r.Why : null;
    public static void ResetRefused() { Refused.Clear(); Nim.ResetCheck(); Lists.Clear(); }

    // ---- each source's embedding models: its own list when the key can read it, else the known ones ----
    private static readonly ConcurrentDictionary<string, (DateTime At, List<string> Models)> Lists = new();

    /// <summary>"prefix:model" for every embedding model a source offers.</summary>
    public static async Task<List<string>> EmbeddingModelsAsync(HttpClient http, ServerSettings s, Source source, ILogger logger, CancellationToken ct)
    {
        var fallback = source.Embedding.Select(m => source.Prefix + m).ToList();
        if (!HasKey(s, source) || source == Jina) return fallback; // Jina has no list to read
        if (Lists.TryGetValue(source.Name, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromHours(6)) return cached.Models;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var key = Key(s, source.Prefix + "x");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                source == Cloudflare ? $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(s.CloudflareAccountId.Trim())}/ai/models/search?task=Text%20Embeddings&per_page=100"
                : source == Gemini ? "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000"
                : "https://api.mistral.ai/v1/models");
            if (source == Gemini) request.Headers.Add("x-goog-api-key", key);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var list = response.IsSuccessStatusCode ? ParseList(source, body) : new();
            if (list.Count == 0)
            {
                logger.LogInformation("{Source}: couldn't read its embedding models (HTTP {Code}); showing the known ones", source.Name, (int)response.StatusCode);
                list = fallback;
            }
            Lists[source.Name] = (DateTime.UtcNow, list);
            return list;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("{Source}: couldn't read its models: {Error}", source.Name, error.Message);
            return fallback;
        }
    }

    /// <summary>Reads a source's model list into "prefix:model", embedding models only.</summary>
    public static List<string> ParseList(Source source, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        IEnumerable<string?> ids;
        if (source == Cloudflare)
            ids = root.GetProperty("result").EnumerateArray().Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null);
        else if (source == Gemini)
            ids = root.GetProperty("models").EnumerateArray()
                .Where(m => m.TryGetProperty("supportedGenerationMethods", out var g) && g.EnumerateArray().Any(x => x.GetString() is "embedContent" or "batchEmbedContents"))
                .Select(m => m.GetProperty("name").GetString()?.Replace("models/", ""));
        else
            ids = root.GetProperty("data").EnumerateArray().Select(m => m.TryGetProperty("id", out var n) ? n.GetString() : null)
                .Where(id => id is not null && id.Contains("embed", StringComparison.OrdinalIgnoreCase));
        return ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => source.Prefix + id)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
