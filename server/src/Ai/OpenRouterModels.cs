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
    private static List<string> _ranked = new();      // ids, most tokens this week first
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

    /// <summary>The free models on offer now, most used on OpenRouter this week first.</summary>
    public static List<string> Free => _ranked.Where(k => k.EndsWith(":free")).ToList();

    /// <summary>How many of the most used free models an automatic order holds.</summary>
    public const int AutoCount = 5;

    /// <summary>The order for describing films: the most used free models, or the admin's own list.</summary>
    public static List<string> DescribeOrder(ServerSettings s) =>
        s.ClassificationModelsAuto && Free.Count > 0 ? Free.Take(AutoCount).ToList() : ServerSettings.Chain(s.ClassificationModels, s.ClassificationModel);

    /// <summary>Step 3, describing again from the web text: the describing order unless set apart.</summary>
    public static List<string> LookupOrder(ServerSettings s) =>
        s.LookupModelsAuto || s.LookupModels.Count == 0 ? DescribeOrder(s) : s.LookupModels.ToList();

    /// <summary>Which model last answered for each step ("describe", "check", "lookup", "pick"), and when.</summary>
    public static readonly ConcurrentDictionary<string, (string Model, DateTime At)> LastAnswered = new();

    /// <summary>The order for the second opinion. Automatic: the same ranking, but describing's first model goes last, so another model reviews.</summary>
    public static List<string> CheckOrder(ServerSettings s)
    {
        if (!s.CheckModelsAuto || Free.Count == 0) return ServerSettings.Chain(s.CheckModels, s.CheckModel);
        var first = DescribeOrder(s).FirstOrDefault();
        var top = Free.Take(AutoCount).ToList();
        return top.Where(m => m != first).Concat(top.Where(m => m == first)).ToList();
    }

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
            using var response = await http.GetAsync("https://openrouter.ai/api/v1/models?sort=top-weekly", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var models = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var ranked = new List<string>();
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) continue;
                var json = !m.TryGetProperty("supported_parameters", out var p) || p.ValueKind != JsonValueKind.Array
                    || p.EnumerateArray().Any(x => x.GetString() is "response_format" or "structured_outputs");
                models[id] = json;
                ranked.Add(id);
            }
            if (models.Count == 0) return _models;
            _models = models;
            _ranked = ranked;
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
