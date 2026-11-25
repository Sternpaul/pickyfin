using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Pickyfin.Server.Ai;

/// <summary>
/// Where a model is asked. Model orders can mix sources: a model is written "prefix:model"
/// ("nim:nvidia/…", "cf:@cf/baai/bge-m3", "gemini:gemini-embedding-001", "jina:…", "voyage:…",
/// "mistral:…"); a model without a known prefix goes to OpenRouter. Every source speaks the
/// OpenAI format.
/// </summary>
public static class Providers
{
    /// <summary>One source of models besides OpenRouter.</summary>
    /// <param name="Batch">Texts per request.</param>
    /// <param name="PerRound">Requests per model per round (every few minutes); low for sources with tight per-minute limits.</param>
    /// <param name="Paid">No free use at all: only with "Free only" off, or in a manual run.</param>
    /// <param name="Chat">Has models that talk (for describing, checking, looking up), listed from the source.</param>
    public sealed record Source(string Prefix, string Name, string KeyUrl, int Batch, string[] Embedding, int PerRound = 8, bool Paid = false, bool Chat = false);

    public static readonly Source Nvidia = new("nim:", "NVIDIA", Nim.KeyUrl, 16, Array.Empty<string>(), Chat: true);
    public static readonly Source Cloudflare = new("cf:", "Cloudflare", "https://dash.cloudflare.com/profile/api-tokens", 16, new[]
    {
        "@cf/baai/bge-m3", "@cf/qwen/qwen3-embedding-0.6b", "@cf/google/embeddinggemma-300m", "@cf/pfnet/plamo-embedding-1b",
        "@cf/baai/bge-large-en-v1.5", "@cf/baai/bge-base-en-v1.5", "@cf/baai/bge-small-en-v1.5",
    }, Chat: true);
    // Gemini's free tier: 100 texts and 30,000 tokens a minute (see Pace): 8 films a request, 3 a round
    public static readonly Source Gemini = new("gemini:", "Gemini", "https://aistudio.google.com/apikey", 8, new[] { "gemini-embedding-001", "gemini-embedding-2" }, PerRound: 3, Chat: true);
    public static readonly Source Jina = new("jina:", "Jina", "https://jina.ai/api-dashboard", 16, new[] { "jina-embeddings-v5-text-small", "jina-embeddings-v5-text-nano" });
    // Without a card on file Voyage allows 3 requests and 10,000 tokens a minute for the whole account (see Pace): 4 films a request, one a round per model
    public static readonly Source Voyage = new("voyage:", "Voyage", "https://dashboard.voyageai.com/organization/api-keys", 4, new[] { "voyage-4-large", "voyage-4", "voyage-4-lite" }, PerRound: 1);
    // Mistral's API has no free use (a Vibe Pro plan includes some API credit). It counts tokens per request: smaller batches
    public static readonly Source Mistral = new("mistral:", "Mistral", "https://console.mistral.ai/api-keys", 8, new[] { "mistral-embed", "codestral-embed" }, Paid: true, Chat: true);

    public static readonly Source[] All = { Nvidia, Cloudflare, Gemini, Jina, Voyage, Mistral };
    /// <summary>The sources whose embedding models are listed here (NVIDIA's come from NVIDIA's own list).</summary>
    public static readonly Source[] EmbeddingOnly = { Cloudflare, Gemini, Jina, Voyage, Mistral };

    public static Source? Of(string model) => All.FirstOrDefault(p => model.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase));
    public static bool IsOpenRouter(string model) => Of(model) is null;
    public static string Name(string model) => Of(model)?.Name ?? "OpenRouter";
    /// <summary>The model's id at its source (the prefix taken off).</summary>
    public static string ApiModel(string model) => Of(model) is { } p ? model[p.Prefix.Length..] : model;
    public static int Batch(string model) => Of(model)?.Batch ?? 16;
    public static int PerRound(string model) => Of(model)?.PerRound ?? 8;

    /// <summary>
    /// Whether a model may be asked in the normal runs. With "Free only" on, OpenRouter's models
    /// must be its free ones (":free") and paid sources (Mistral) are out; the other sources are
    /// free until their allowance ends, which FreeCaps watches. A self-hosted endpoint is the
    /// admin's own and always allowed.
    /// </summary>
    public static bool IsFree(ServerSettings s, string model)
    {
        if (!s.FreeOnly) return true;
        if (Of(model) is { } p) return !p.Paid;
        return !string.IsNullOrWhiteSpace(s.CustomEndpoint)
            || model.EndsWith(":free", StringComparison.OrdinalIgnoreCase) || model.Equals("openrouter/free", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The key for this model's source; empty when none is set (Cloudflare also needs its account id).</summary>
    public static string Key(ServerSettings s, string model)
    {
        var p = Of(model);
        if (p == Cloudflare && string.IsNullOrWhiteSpace(s.CloudflareAccountId)) return "";
        return (p == Nvidia ? s.NimApiKey : p == Cloudflare ? s.CloudflareApiToken : p == Gemini ? s.GeminiApiKey
            : p == Jina ? s.JinaApiKey : p == Voyage ? s.VoyageApiKey : p == Mistral ? s.MistralApiKey : s.ApiKey).Trim();
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
        if (p == Voyage) return "https://api.voyageai.com/v1";
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

    // ---- each source's models: its own list when the key can read it, else the known ones ----
    private static readonly ConcurrentDictionary<string, (DateTime At, List<string> Models)> Lists = new();

    /// <summary>"prefix:model" for every embedding model a source offers.</summary>
    public static Task<List<string>> EmbeddingModelsAsync(HttpClient http, ServerSettings s, Source source, ILogger logger, CancellationToken ct) =>
        ModelsAsync(http, s, source, chat: false, logger, ct);

    /// <summary>"prefix:model" for every model a source offers, embedding models or the ones that talk.</summary>
    public static async Task<List<string>> ModelsAsync(HttpClient http, ServerSettings s, Source source, bool chat, ILogger logger, CancellationToken ct)
    {
        var fallback = chat ? new List<string>() : source.Embedding.Select(m => source.Prefix + m).ToList();
        if (chat && !source.Chat) return fallback;
        if (!HasKey(s, source) || source == Jina || source == Voyage || source == Nvidia) return fallback; // Jina and Voyage have no list to read; NVIDIA's is Nim's
        var cacheKey = source.Name + (chat ? "|chat" : "|embed");
        if (Lists.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromHours(6)) return cached.Models;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var key = Key(s, source.Prefix + "x");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                source == Cloudflare ? $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(s.CloudflareAccountId.Trim())}/ai/models/search?task={(chat ? "Text%20Generation" : "Text%20Embeddings")}&per_page=100"
                : source == Gemini ? "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000"
                : "https://api.mistral.ai/v1/models");
            if (source == Gemini) request.Headers.Add("x-goog-api-key", key);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var list = response.IsSuccessStatusCode ? ParseList(source, body, chat) : new();
            if (list.Count == 0)
            {
                logger.LogInformation("{Source}: couldn't read its models (HTTP {Code}); showing the known ones", source.Name, (int)response.StatusCode);
                list = fallback;
            }
            Lists[cacheKey] = (DateTime.UtcNow, list);
            return list;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("{Source}: couldn't read its models: {Error}", source.Name, error.Message);
            return fallback;
        }
    }

    /// <summary>Reads a source's model list into "prefix:model": embedding models, or the ones that talk.</summary>
    public static List<string> ParseList(Source source, string body, bool chat = false)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        IEnumerable<string?> ids;
        if (source == Cloudflare)
            ids = root.GetProperty("result").EnumerateArray().Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null);
        else if (source == Gemini)
            ids = root.GetProperty("models").EnumerateArray()
                .Where(m => m.TryGetProperty("supportedGenerationMethods", out var g)
                    && g.EnumerateArray().Any(x => chat ? x.GetString() is "generateContent" : x.GetString() is "embedContent" or "batchEmbedContents"))
                .Select(m => m.GetProperty("name").GetString()?.Replace("models/", ""));
        else if (source == Mistral)
        {
            // Mistral lists a model under each of its names ("mistral-embed" and "mistral-embed-2312"):
            // one entry per model, under its shortest name, or the same model would be paid for twice
            var groups = new List<HashSet<string>>();
            foreach (var m in root.GetProperty("data").EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var n) ? n.GetString() : null;
                if (id is null) continue;
                var embeds = id.Contains("embed", StringComparison.OrdinalIgnoreCase);
                var talks = m.TryGetProperty("capabilities", out var c) && c.TryGetProperty("completion_chat", out var cc) && cc.ValueKind == JsonValueKind.True;
                if (chat ? embeds || !talks : !embeds) continue;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
                if (m.TryGetProperty("aliases", out var a) && a.ValueKind == JsonValueKind.Array)
                    foreach (var x in a.EnumerateArray()) if (x.GetString() is { Length: > 0 } alias) names.Add(alias);
                if (m.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } canonical) names.Add(canonical);
                var same = groups.Where(g => g.Overlaps(names)).ToList();
                foreach (var g in same) { names.UnionWith(g); groups.Remove(g); }
                groups.Add(names);
            }
            // the "-latest" name when there is one (it follows Mistral's updates), else the shortest
            ids = groups.Select(g => g.OrderByDescending(x => x.EndsWith("-latest", StringComparison.OrdinalIgnoreCase)).ThenBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).First());
        }
        else ids = Array.Empty<string>();
        return ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => source.Prefix + id)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
