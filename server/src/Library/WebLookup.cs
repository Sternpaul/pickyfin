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

    /// <summary>This month's films looked up (all engines together), and each engine's own searches and spend.</summary>
    public sealed record Ledger(Usage Films, Usage Exa, Usage Parallel);

    private Ledger Read()
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        Usage Fresh() => new(month, 0, 0);
        Usage ThisMonthOnly(Usage? u) => u is not null && u.Month == month ? u : Fresh();
        lock (Gate)
        {
            try
            {
                if (!File.Exists(_usagePath)) return new Ledger(Fresh(), Fresh(), Fresh());
                var text = File.ReadAllText(_usagePath);
                var ledger = JsonSerializer.Deserialize<Ledger>(text);
                if (ledger?.Films is not null)
                    return new Ledger(ThisMonthOnly(ledger.Films), ThisMonthOnly(ledger.Exa), ThisMonthOnly(ledger.Parallel));
                // Older file: one total, which was Exa's
                var old = ThisMonthOnly(JsonSerializer.Deserialize<Usage>(text));
                return new Ledger(old, old, Fresh());
            }
            catch { return new Ledger(Fresh(), Fresh(), Fresh()); }
        }
    }

    /// <summary>Films looked up this month (Dollars: all engines together).</summary>
    public Usage ThisMonth => Read().Films;
    public Usage ExaMonth => Read().Exa;
    public Usage ParallelMonth => Read().Parallel;

    private void Write(Func<Ledger, Ledger> change)
    {
        var next = change(Read());
        lock (Gate) File.WriteAllText(_usagePath, JsonSerializer.Serialize(next));
    }

    private static Usage Add(Usage u, double dollars)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        return u with { Lookups = u.Lookups + 1, Dollars = u.Dollars + dollars, Day = today, DayLookups = u.Today + 1 };
    }

    /// <summary>One search by one engine, with what it cost.</summary>
    private void Count(string engine, double dollars) => Write(l => engine == "Exa"
        ? l with { Exa = Add(l.Exa, dollars), Films = l.Films with { Dollars = l.Films.Dollars + dollars } }
        : l with { Parallel = Add(l.Parallel, dollars), Films = l.Films with { Dollars = l.Films.Dollars + dollars } });

    /// <summary>One film looked up (however many engines answered).</summary>
    private void CountFilm() => Write(l => l with { Films = Add(l.Films, 0) });

    /// <summary>The Parallel search mode for this lookup: advanced only with a key.</summary>
    public static string ParallelModeInUse => !string.IsNullOrWhiteSpace(ServerSettings.Current.ParallelApiKey) && ServerSettings.Current.ParallelMode == "advanced" ? "advanced" : "fast";
    public static bool Configured => ServerSettings.Current.ExaOn || ServerSettings.Current.ParallelOn;
    /// <summary>Parallel without a key costs nothing, so its monthly limit doesn't apply.</summary>
    public static bool ParallelFree => string.IsNullOrWhiteSpace(ServerSettings.Current.ParallelApiKey);
    /// <summary>Only engines that cost nothing are on.</summary>
    public static bool Free => !ServerSettings.Current.ExaOn && ServerSettings.Current.ParallelOn && ParallelFree;

    public bool ExaAvailable => ServerSettings.Current.ExaOn && ExaMonth.Dollars < ServerSettings.Current.WebLookupBudget;
    public bool ParallelAvailable => ServerSettings.Current.ParallelOn && (ParallelFree || ParallelMonth.Dollars < ServerSettings.Current.ParallelBudget);

    /// <summary>The engines a lookup uses right now, for the history ("Exa + Parallel (advanced)").</summary>
    public string ProviderName => string.Join(" + ", new[] { ExaAvailable ? "Exa" : null, ParallelAvailable ? $"Parallel ({ParallelModeInUse})" : null }.Where(n => n is not null));

    /// <summary>Why a lookup can't run now, or null when at least one engine can.</summary>
    public string? Blocked()
    {
        var s = ServerSettings.Current;
        if (!Configured) return "No web search set up (AI page).";
        if (ExaAvailable || ParallelAvailable) return null;
        return "This month's web lookup limit is reached" + (s.ExaOn && s.ParallelOn ? " for Exa and Parallel." : ".");
    }

    /// <summary>A few paragraphs about the film from film sites (or anywhere, if those have nothing). Null when nothing useful was found.</summary>
    public async Task<string?> FindAsync(MovieMetadata movie, CancellationToken cancellationToken)
    {
        if (Blocked() is { } why) throw new InvalidOperationException(why);
        var query = $"{movie.Title} ({movie.ReleaseYear}) film" + (string.IsNullOrWhiteSpace(movie.Director) ? "" : $" directed by {movie.Director}")
            + (string.IsNullOrWhiteSpace(movie.OriginalTitle) ? "" : $" original title {movie.OriginalTitle}");

        var searches = new List<(string Engine, Task<List<(string Title, string Url, string Text)>> Task)>();
        Task<List<(string Title, string Url, string Text)>> Start(string engine) =>
            engine == "Exa" ? ExaAsync(query, cancellationToken) : ParallelAsync(movie, cancellationToken);
        var engines = new[] { ExaAvailable ? "Exa" : null, ParallelAvailable ? "Parallel" : null }.Where(e => e is not null).Select(e => e!).ToList();
        var order = ServerSettings.Current.WebSearchOrder;
        if (engines.Count == 2 && order is "exa-first" or "parallel-first")
        {
            // One after the other: the second only when the first fails or finds nothing
            if (order == "parallel-first") engines.Reverse();
            foreach (var engine in engines)
            {
                var task = Start(engine);
                searches.Add((engine, task));
                try { if ((await task).Count > 0) break; } catch { /* the next engine takes over */ }
                if (engine == engines[0]) _logger.LogInformation("Web lookup: {Engine} had nothing for {Title}; asking {Next}", engine, movie.Title, engines[1]);
            }
        }
        else
        {
            // Every engine that's on and within its limit, at the same time
            foreach (var engine in engines) searches.Add((engine, Start(engine)));
            try { await Task.WhenAll(searches.Select(x => x.Task)); } catch { /* looked at one by one below */ }
        }
        var answered = searches.Where(x => x.Task.IsCompletedSuccessfully).ToList();
        foreach (var failed in searches.Where(x => !x.Task.IsCompletedSuccessfully))
            _logger.LogWarning("Web lookup: {Engine} failed for {Title}: {Error}", failed.Engine, movie.Title, failed.Task.Exception?.GetBaseException().Message);
        if (answered.Count == 0)
            throw searches.First().Task.Exception?.GetBaseException() ?? new InvalidOperationException("Web lookup failed");
        CountFilm();

        // Pages from both, taking turns, without the same page twice: 3 from one engine, up to 5 from two
        static string Key(string url) { try { var u = new Uri(url); return (u.Host.Replace("www.", "") + u.AbsolutePath).TrimEnd('/').ToLowerInvariant(); } catch { return url; } }
        var lists = answered.Select(x => x.Task.Result).ToList();
        var found = new List<(string Title, string Url, string Text)>();
        var seen = new HashSet<string>();
        for (var k = 0; found.Count < (lists.Count > 1 ? 5 : 3) && lists.Any(l => k < l.Count); k++)
            foreach (var list in lists)
                if (k < list.Count && seen.Add(Key(list[k].Url)) && found.Count < (lists.Count > 1 ? 5 : 3)) found.Add(list[k]);
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

        // What Parallel reports for the call (with a key), e.g. "web_search ×1 $0.005"; free use reports nothing
        double? reported = null;
        var billed = "";
        if (result.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("parallel/usage", out var usage) && usage.ValueKind == JsonValueKind.Array)
        {
            reported = usage.EnumerateArray().Sum(u => u.TryGetProperty("cost_usd", out var c) && c.TryGetDouble(out var d) ? d : 0);
            billed = string.Join(", ", usage.EnumerateArray().Select(u =>
                $"{(u.TryGetProperty("name", out var n) ? n.GetString() : "?")} ×{(u.TryGetProperty("count", out var k) ? k.ToString() : "?")} ${(u.TryGetProperty("cost_usd", out var c) ? c.ToString() : "?")}"));
        }
        // With a key, the limit counts at least the list price, even when Parallel reports $0 (e.g. covered by free credit)
        var listPrice = ParallelModeInUse == "advanced" ? 0.005 : 0.001;
        var cost = ParallelFree ? 0 : Math.Max(reported ?? 0, listPrice);
        Count("Parallel", cost);

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
        _logger.LogInformation("Web lookup (Parallel, {Mode}): {Title} ({Year}) → {Count} page(s), {Cost}", ParallelModeInUse, movie.Title, movie.ReleaseYear, found.Count,
            ParallelFree ? "free (no key)" : $"counted ${cost:0.###}; Parallel reports {(reported is null ? "no cost" : billed)}");
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

    private async Task<List<(string Title, string Url, string Text)>> ExaAsync(string query, CancellationToken cancellationToken)
    {
        var found = await SearchAsync(query, FilmSites, cancellationToken);
        if (found.Count == 0) found = await SearchAsync(query, null, cancellationToken);
        return found;
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
        Count("Exa", cost);
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
