using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// Looks a film up on the web, so the AI can tag films from real descriptions instead of
/// guessing. Two search engines: Exa (under one cent per film, needs a key) or Parallel
/// (its MCP server: free without a key at lower rate limits; with a key, billed to the
/// Parallel account). Spending is counted per month and stops at the budget on the AI page.
/// </summary>
public sealed class WebLookup
{
    private static readonly string[] FilmSites = { "en.wikipedia.org", "letterboxd.com", "themoviedb.org", "rottentomatoes.com", "mubi.com", "imdb.com" };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<WebLookup> _logger;
    private readonly string _usagePath;
    private static readonly object Gate = new();

    public WebLookup(IHttpClientFactory http, ILogger<WebLookup> logger, DataDirectory data)
    {
        _http = http;
        _logger = logger;
        _usagePath = Path.Combine(data.Path, "web-lookups.json");
    }

    public sealed record Usage(string Month, int Lookups, double Dollars, string? Day = null, int DayLookups = 0)
    {
        public int Today => Day == DateTime.UtcNow.ToString("yyyy-MM-dd") ? DayLookups : 0;
    }

    public Usage ThisMonth
    {
        get
        {
            var month = DateTime.UtcNow.ToString("yyyy-MM");
            lock (Gate)
            {
                try
                {
                    var saved = File.Exists(_usagePath) ? JsonSerializer.Deserialize<Usage>(File.ReadAllText(_usagePath)) : null;
                    return saved is not null && saved.Month == month ? saved : new Usage(month, 0, 0);
                }
                catch { return new Usage(month, 0, 0); }
            }
        }
    }

    private void Count(double dollars)
    {
        var now = ThisMonth;
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        lock (Gate) File.WriteAllText(_usagePath, JsonSerializer.Serialize(now with
        {
            Lookups = now.Lookups + 1, Dollars = now.Dollars + dollars,
            Day = today, DayLookups = now.Today + 1,
        }));
    }

    public static bool UsesParallel => ServerSettings.Current.WebSearchProvider == "parallel";
    /// <summary>The Parallel search mode for this lookup: advanced only with a key.</summary>
    public static string ParallelModeInUse => !string.IsNullOrWhiteSpace(ServerSettings.Current.ParallelApiKey) && ServerSettings.Current.ParallelMode == "advanced" ? "advanced" : "fast";
    public static string ProviderName => UsesParallel ? $"Parallel ({ParallelModeInUse})" : "Exa";
    public static bool Configured => UsesParallel || !string.IsNullOrWhiteSpace(ServerSettings.Current.ExaApiKey);
    /// <summary>Parallel without a key costs nothing, so the monthly limit doesn't apply.</summary>
    public static bool Free => UsesParallel && string.IsNullOrWhiteSpace(ServerSettings.Current.ParallelApiKey);

    /// <summary>Why a lookup can't run now, or null when it can.</summary>
    public string? Blocked()
    {
        if (!Configured) return "No Exa key set (AI page).";
        if (Free) return null;
        if (ThisMonth.Dollars >= ServerSettings.Current.WebLookupBudget) return $"This month's web lookup budget (${ServerSettings.Current.WebLookupBudget:0.##}) is used up.";
        return null;
    }

    /// <summary>A few paragraphs about the film from film sites (or anywhere, if those have nothing). Null when nothing useful was found.</summary>
    public async Task<string?> FindAsync(MovieMetadata movie, CancellationToken cancellationToken)
    {
        if (Blocked() is { } why) throw new InvalidOperationException(why);
        var query = $"{movie.Title} ({movie.ReleaseYear}) film" + (string.IsNullOrWhiteSpace(movie.Director) ? "" : $" directed by {movie.Director}")
            + (string.IsNullOrWhiteSpace(movie.OriginalTitle) ? "" : $" original title {movie.OriginalTitle}");

        List<(string Title, string Url, string Text)> found;
        if (UsesParallel)
            found = await ParallelAsync(movie, cancellationToken);
        else
        {
            found = await SearchAsync(query, FilmSites, cancellationToken);
            if (found.Count == 0) found = await SearchAsync(query, null, cancellationToken);
        }
        if (found.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var (title, url, text) in found)
            sb.Append($"[{title} – {new Uri(url).Host}] {text}\n\n");
        return sb.ToString().Trim();
    }

    // ---- Parallel, through its MCP server (https://mcp.parallel.ai/mcp): web_search returns pages with passages
    private const string ParallelMcp = "https://mcp.parallel.ai/mcp";
    private static string? _mcpSession;
    private static int _mcpId;
    private static readonly SemaphoreSlim McpGate = new(1, 1);

    private async Task<List<(string Title, string Url, string Text)>> ParallelAsync(MovieMetadata movie, CancellationToken cancellationToken)
    {
        var who = string.IsNullOrWhiteSpace(movie.Director) ? "" : $", directed by {movie.Director}";
        var original = string.IsNullOrWhiteSpace(movie.OriginalTitle) ? "" : $" (original title {movie.OriginalTitle})";
        var arguments = new
        {
            objective = $"What the {movie.ReleaseYear} film {movie.Title}{original}{who} is about: plot, genre, tone, mood, themes, style and critical reception. Prefer Wikipedia, Letterboxd, TMDB, IMDb, Rotten Tomatoes and film reviews.",
            search_queries = new[] { $"{movie.Title} {movie.ReleaseYear} film", $"{movie.Title} {movie.ReleaseYear} review" }
                .Concat(string.IsNullOrWhiteSpace(movie.OriginalTitle) ? Array.Empty<string>() : new[] { $"{movie.OriginalTitle} {movie.ReleaseYear}" }).ToArray(),
        };

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        JsonElement result;
        await McpGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (_mcpSession is null) await McpStartAsync(client, cancellationToken);
                var (status, message) = await McpPostAsync(client, new { jsonrpc = "2.0", id = Interlocked.Increment(ref _mcpId), method = "tools/call", @params = new { name = "web_search", arguments } }, cancellationToken);
                if (status is 400 or 404 && attempt == 0) { _mcpSession = null; continue; } // the session ended: start a new one
                if (status == 429) throw new InvalidOperationException("Parallel: rate limited (429). Free use has lower limits; a Parallel key raises them.");
                if (status is < 200 or >= 300 || message is null) throw new InvalidOperationException($"Parallel: HTTP {status}");
                if (message.Value.TryGetProperty("error", out var error))
                    throw new InvalidOperationException($"Parallel: {(error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString())}");
                result = message.Value.GetProperty("result");
                break;
            }
        }
        finally { McpGate.Release(); }

        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Parallel: " + string.Join(" ", result.GetProperty("content").EnumerateArray().Select(c => c.TryGetProperty("text", out var t) ? t.GetString() : "")).Trim());

        // Cost, when billed to a key; free use reports none
        var cost = 0.0;
        if (result.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("parallel/usage", out var usage) && usage.ValueKind == JsonValueKind.Array)
            cost = usage.EnumerateArray().Sum(u => u.TryGetProperty("cost_usd", out var c) && c.TryGetDouble(out var d) ? d : 0);
        else if (!Free) cost = ParallelModeInUse == "advanced" ? 0.005 : 0.001;
        Count(cost);

        var data = result.TryGetProperty("structuredContent", out var sc) ? sc
            : JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}").RootElement;
        var found = new List<(string, string, string)>();
        if (data.TryGetProperty("results", out var list))
            foreach (var r in list.EnumerateArray())
            {
                var text = r.TryGetProperty("excerpts", out var ex) && ex.ValueKind == JsonValueKind.Array
                    ? string.Join(" … ", ex.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()))
                    : "";
                text = text.Replace('\n', ' ');
                if (text.Length < 80 || !r.TryGetProperty("url", out var url)) continue;
                if (text.Length > 1200) text = text[..1200] + "…";
                found.Add((r.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "", url.GetString() ?? "", text));
                if (found.Count == 3) break;
            }
        _logger.LogInformation("Web lookup (Parallel, {Mode}): {Title} ({Year}) → {Count} page(s){Cost}", ParallelModeInUse, movie.Title, movie.ReleaseYear, found.Count, cost > 0 ? $", ${cost:0.###}" : ", free");
        return found;
    }

    private async Task McpStartAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var (status, _) = await McpPostAsync(client, new
        {
            jsonrpc = "2.0", id = Interlocked.Increment(ref _mcpId), method = "initialize",
            @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "pickyfin", version = "1.0" } },
        }, cancellationToken, initializing: true);
        if (status is 401 or 403) throw new InvalidOperationException("Parallel refused the key (AI page → Parallel key)");
        if (status is < 200 or >= 300 || _mcpSession is null) throw new InvalidOperationException($"Parallel: couldn't start a session (HTTP {status})");
        await McpPostAsync(client, new { jsonrpc = "2.0", method = "notifications/initialized" }, cancellationToken);
    }

    /// <summary>One JSON-RPC message to the MCP server; the answer comes as JSON or as a server-sent event.</summary>
    private async Task<(int Status, JsonElement? Message)> McpPostAsync(HttpClient client, object body, CancellationToken cancellationToken, bool initializing = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ParallelMcp) { Content = JsonContent.Create(body) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        if (!initializing && _mcpSession is not null) request.Headers.Add("Mcp-Session-Id", _mcpSession);
        if (!string.IsNullOrWhiteSpace(ServerSettings.Current.ParallelApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ServerSettings.Current.ParallelApiKey);
            // A key allows choosing the search mode (the server's default is fast)
            request.Headers.Add("x-parallel-mcp-config", JsonSerializer.Serialize(new { web_search = new { @override = new { mode = ParallelModeInUse } } }));
        }
        using var response = await client.SendAsync(request, cancellationToken);
        if (initializing && response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _mcpSession = ids.FirstOrDefault();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) return ((int)response.StatusCode, null);
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? string.Join("", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()).LastOrDefault() ?? "")
            : text;
        try { return ((int)response.StatusCode, JsonDocument.Parse(json).RootElement.Clone()); }
        catch (JsonException) { return ((int)response.StatusCode, null); }
    }

    private async Task<List<(string Title, string Url, string Text)>> SearchAsync(string query, string[]? sites, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.exa.ai/search");
        request.Headers.Add("x-api-key", ServerSettings.Current.ExaApiKey);
        request.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["query"] = query,
            ["type"] = "auto",
            ["numResults"] = 3,
            ["includeDomains"] = sites,
            // Only the passages that say what the film is like, not the whole page
            ["contents"] = new { highlights = new { query = "What is this film about: plot, genre, tone, mood, themes, style and critical reception", maxCharacters = 1200 } },
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Exa: HTTP {(int)response.StatusCode} {(body.Length > 200 ? body[..200] : body)}");

        using var json = JsonDocument.Parse(body);
        var cost = json.RootElement.TryGetProperty("costDollars", out var c) && c.TryGetProperty("total", out var t) ? t.GetDouble() : 0.007;
        Count(cost);
        var results = new List<(string, string, string)>();
        if (json.RootElement.TryGetProperty("results", out var list))
            foreach (var r in list.EnumerateArray())
            {
                var text = r.TryGetProperty("highlights", out var hl) && hl.ValueKind == JsonValueKind.Array
                    ? string.Join(" … ", hl.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.String).Select(h => h.GetString()!.Trim()))
                    : r.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                if (text.Length < 80) continue;
                results.Add((r.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "", r.GetProperty("url").GetString() ?? "", text.Replace('\n', ' ')));
            }
        _logger.LogInformation("Web lookup: {Query} → {Count} page(s), ${Cost:0.###}", query, results.Count, cost);
        return results;
    }
}

/// <summary>The data folder, for services that keep a small file there.</summary>
public sealed record DataDirectory(string Path);
