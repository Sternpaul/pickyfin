using System.Collections.Concurrent;
using System.Text.Json;

namespace Pickyfin.Server.Ai;

/// <summary>
/// OpenRouter's live model list (public, no key needed), cached for six hours, so models it
/// no longer offers are skipped instead of tried, and JSON mode is only asked of models that support it.
/// </summary>
public static class OpenRouterModels
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, bool>? _models; // id → supports JSON mode
    private static DateTime _fetched, _failed;
    private static readonly ConcurrentDictionary<string, DateTime> GoneUntil = new(StringComparer.OrdinalIgnoreCase);

    public sealed record ModelStats(int Ok, int Failed, string? LastError, DateTime? LastOk, DateTime? LastFailed);
    private static readonly ConcurrentDictionary<string, ModelStats> Results = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How each model did since the server started (a call = up to three tries).</summary>
    public static IReadOnlyDictionary<string, ModelStats> Stats => Results;

    public static void Record(string model, string? error) =>
        Results.AddOrUpdate(model,
            _ => error is null ? new(1, 0, null, DateTime.UtcNow, null) : new(0, 1, error, null, DateTime.UtcNow),
            (_, s) => error is null ? s with { Ok = s.Ok + 1, LastOk = DateTime.UtcNow } : s with { Failed = s.Failed + 1, LastError = error, LastFailed = DateTime.UtcNow });

    public static void MarkGone(string model) => GoneUntil[model] = DateTime.UtcNow.AddHours(6);
    public static bool IsGone(string model) => GoneUntil.TryGetValue(model, out var until) && until > DateTime.UtcNow;

    /// <summary>The free models on offer now, biggest context first (for the model pickers).</summary>
    public static List<string> Free => _models?.Keys.Where(k => k.EndsWith(":free")).Order().ToList() ?? new();

    /// <summary>Null when the list can't be fetched; then nothing is filtered.</summary>
    public static async Task<Dictionary<string, bool>?> GetAsync(HttpClient http, ILogger logger, CancellationToken ct)
    {
        if (_models is not null && DateTime.UtcNow - _fetched < TimeSpan.FromHours(6)) return _models;
        if (DateTime.UtcNow - _failed < TimeSpan.FromHours(1)) return _models;
        await Gate.WaitAsync(ct);
        try
        {
            if (_models is not null && DateTime.UtcNow - _fetched < TimeSpan.FromHours(6)) return _models;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.GetAsync("https://openrouter.ai/api/v1/models", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var models = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                var json = !m.TryGetProperty("supported_parameters", out var p) || p.ValueKind != JsonValueKind.Array
                    || p.EnumerateArray().Any(x => x.GetString() is "response_format" or "structured_outputs");
                models[id] = json;
            }
            if (models.Count == 0) return _models;
            _models = models;
            _fetched = DateTime.UtcNow;
            return _models;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("Couldn't fetch OpenRouter's model list: {Error}", ex.Message);
            _failed = DateTime.UtcNow; // try again in an hour
            return _models;
        }
        finally { Gate.Release(); }
    }
}
