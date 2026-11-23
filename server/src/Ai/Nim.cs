using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Pickyfin.Server.Ai;

/// <summary>
/// Where a model is asked. Model orders can mix OpenRouter and NVIDIA NIM (build.nvidia.com):
/// an NVIDIA model is written "nim:owner/model", anything else goes to OpenRouter.
/// </summary>
public static class Nim
{
    public const string Prefix = "nim:";
    public const string BaseUrl = "https://integrate.api.nvidia.com/v1";
    public const string KeyUrl = "https://build.nvidia.com/settings/api-keys";

    public static bool Is(string model) => model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    /// <summary>The model's id at its provider ("nim:" taken off).</summary>
    public static string ApiModel(string model) => Is(model) ? model[Prefix.Length..] : model;
    public static string ProviderName(string model) => Is(model) ? "NVIDIA" : "OpenRouter";

    /// <summary>The key for this model's provider; empty when none is set.</summary>
    public static string Key(ServerSettings s, string model) => (Is(model) ? s.NimApiKey : s.ApiKey).Trim();
    public static bool HasKey(ServerSettings s, string model) => Key(s, model).Length > 0;
    public static bool AnyKey(ServerSettings s) => !string.IsNullOrWhiteSpace(s.ApiKey) || !string.IsNullOrWhiteSpace(s.NimApiKey);

    public static string ChatUrl(ServerSettings s, string model) =>
        Is(model) ? BaseUrl + "/chat/completions"
        : string.IsNullOrWhiteSpace(s.CustomEndpoint) ? "https://openrouter.ai/api/v1/chat/completions" : s.CustomEndpoint;

    public static string EmbedUrl(string model) => Is(model) ? BaseUrl + "/embeddings" : "https://openrouter.ai/api/v1/embeddings";

    public static void Authorize(HttpRequestMessage request, ServerSettings s, string model)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key(s, model));
        if (Is(model)) return;
        request.Headers.Add("HTTP-Referer", "https://github.com/Sternpaul/pickyfin");
        request.Headers.Add("X-Title", "Pickyfin");
    }

    // ---- a key that was refused: that provider's models wait, the others carry on ----
    private static readonly ConcurrentDictionary<string, (DateTime Until, string Why)> Refused = new();
    public static void Refuse(string model, string why) => Refused[ProviderName(model)] = (DateTime.UtcNow.AddHours(1), why);
    public static string? RefusedWhy(string model) =>
        Refused.TryGetValue(ProviderName(model), out var r) && r.Until > DateTime.UtcNow ? r.Why : null;
    public static void ResetRefused() { Refused.Clear(); _checkedFor = ""; NotServed.Clear(); }

    // ---- models NVIDIA lists but doesn't serve for this key (404 "Function …: Not found for account") ----
    private static readonly ConcurrentDictionary<string, DateTime> NotServed = new(StringComparer.OrdinalIgnoreCase);
    private static string _checkedFor = "";
    private static DateTime _checkedAt;
    private static Task? _check;
    private static readonly object CheckGate = new();

    public static bool IsNotServed(string model) => NotServed.ContainsKey(model);
    public static List<string> NotServedList => NotServed.Keys.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
    public static bool SaysNotServed(int code, string? error) =>
        code == 404 && error is not null && error.Contains("not found for account", StringComparison.OrdinalIgnoreCase);
    public static void MarkNotServed(string model) => NotServed[model] = DateTime.UtcNow;

    /// <summary>
    /// Once per key and day: one tiny request to each listed model, so the lists only offer what
    /// answers for this key. Only a clear "not found for account" counts; a slow or busy model stays.
    /// </summary>
    public static Task CheckServedAsync(HttpClient http, ServerSettings s, ILogger logger)
    {
        var key = s.NimApiKey.Trim();
        if (key.Length == 0 || _models is null) return Task.CompletedTask;
        lock (CheckGate)
        {
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
            if (_check is not null && _checkedFor == fingerprint && (!_check.IsCompleted || DateTime.UtcNow - _checkedAt < TimeSpan.FromDays(1))) return _check;
            _checkedFor = fingerprint;
            _checkedAt = DateTime.UtcNow;
            var models = _models.Where(m => IsChat(m) || IsEmbedding(m)).ToList();
            return _check = Task.Run(async () =>
            {
                var gone = 0;
                await Task.WhenAll(models.Select(async model =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var body = IsEmbedding(model)
                        ? JsonSerializer.Serialize(new { model = ApiModel(model), input = new[] { "test" }, input_type = "passage" })
                        : JsonSerializer.Serialize(new { model = ApiModel(model), messages = new[] { new { role = "user", content = "Reply with: ok" } }, max_tokens = 1 });
                    using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + (IsEmbedding(model) ? "/embeddings" : "/chat/completions"))
                    { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    try
                    {
                        using var response = await http.SendAsync(request, timeout.Token);
                        var text = await response.Content.ReadAsStringAsync(timeout.Token);
                        if (SaysNotServed((int)response.StatusCode, text)) { MarkNotServed(model); Interlocked.Increment(ref gone); }
                        else NotServed.TryRemove(model, out _);
                    }
                    catch (Exception) { /* slow or unreachable: not a sign it isn't served */ }
                }));
                logger.LogInformation("NVIDIA: {Served} of {Count} listed models answer for this key; not served: {Missing}",
                    models.Count - gone, models.Count, gone == 0 ? "none" : string.Join(", ", NotServedList.Select(ApiModel)));
            });
        }
    }

    // ---- the models NVIDIA hosts (public list, no key needed), cached for six hours ----
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<string>? _models; // with the "nim:" prefix
    private static DateTime _fetched, _failed;

    /// <summary>Models that make vectors; everything else that isn't a filter or detector can talk.</summary>
    public static bool IsEmbedding(string model)
    {
        var id = ApiModel(model).ToLowerInvariant();
        return id.Contains("embed") || id.EndsWith("/nvclip");
    }
    private static readonly string[] NotChat = { "safety", "guard", "pii", "detector", "gliner", "calibration", "starcoder", "translate", "reward", "parse", "rerank" };
    public static bool IsChat(string model)
    {
        var id = ApiModel(model).ToLowerInvariant();
        return !IsEmbedding(model) && !NotChat.Any(id.Contains);
    }

    public static IReadOnlyList<string>? Models => _models;
    public static List<string> Chat => (_models ?? new()).Where(m => IsChat(m) && !IsNotServed(m)).ToList();
    public static List<string> Embedding => (_models ?? new()).Where(m => IsEmbedding(m) && !IsNotServed(m)).ToList();

    /// <summary>Null when the list couldn't be fetched yet; then nothing is filtered.</summary>
    public static async Task<IReadOnlyList<string>?> GetAsync(HttpClient http, ILogger logger, CancellationToken ct)
    {
        if (_models is not null && DateTime.UtcNow - _fetched < TimeSpan.FromHours(6)) return _models;
        if (DateTime.UtcNow - _failed < TimeSpan.FromHours(1)) return _models;
        await Gate.WaitAsync(ct);
        try
        {
            if (_models is not null && DateTime.UtcNow - _fetched < TimeSpan.FromHours(6)) return _models;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.GetAsync(BaseUrl + "/models", timeout.Token);
            response.EnsureSuccessStatusCode();
            var list = Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (list.Count == 0) return _models;
            _models = list;
            _fetched = DateTime.UtcNow;
            return _models;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("Couldn't fetch NVIDIA's model list: {Error}", ex.Message);
            _failed = DateTime.UtcNow;
            return _models;
        }
        finally { Gate.Release(); }
    }

    /// <summary>Reads {"data":[{"id":"owner/model"}, …]} into "nim:owner/model", sorted.</summary>
    public static List<string> Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => Prefix + id)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>For tests.</summary>
    internal static void SetModels(List<string>? models) { _models = models; _fetched = models is null ? default : DateTime.UtcNow; }
}
