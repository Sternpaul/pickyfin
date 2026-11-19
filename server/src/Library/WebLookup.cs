using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// Looks a film up on the web, so the AI can tag films from real descriptions instead of guessing.
/// Several search engines in an order set on the AI page (Parallel, Exa, Linkup, Tavily, Wikipedia):
/// each lookup goes to the first ones that are on and still have allowance left this day or month;
/// when one fails or finds nothing, the next takes over. Wikipedia can be added to every lookup.
/// Searches and spending are counted per engine.
/// </summary>
public sealed class WebLookup
{
    private static readonly string[] FilmSites = { "en.wikipedia.org", "letterboxd.com", "themoviedb.org", "rottentomatoes.com", "mubi.com", "imdb.com" };
    private const string UserAgent = "Pickyfin/1.0 (https://github.com/Sternpaul/pickyfin)";

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

    /// <summary>Films looked up this month (all engines together), and each engine's own searches and spend.</summary>
    public sealed record Ledger(Usage Films, Dictionary<string, Usage> Engines);
    private sealed record OldLedger(Usage? Films, Usage? Exa, Usage? Parallel);

    private Ledger Read()
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        Usage Fresh() => new(month, 0, 0);
        Usage ThisMonthOnly(Usage? u) => u is not null && u.Month == month ? u : Fresh();
        lock (Gate)
        {
            try
            {
                if (!File.Exists(_usagePath)) return new Ledger(Fresh(), new());
                var text = File.ReadAllText(_usagePath);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("Engines", out _))
                {
                    var ledger = JsonSerializer.Deserialize<Ledger>(text)!;
                    return new Ledger(ThisMonthOnly(ledger.Films), ledger.Engines.ToDictionary(e => e.Key, e => ThisMonthOnly(e.Value)));
                }
                if (doc.RootElement.TryGetProperty("Films", out _))
                {
                    var old = JsonSerializer.Deserialize<OldLedger>(text)!;
                    return new Ledger(ThisMonthOnly(old.Films), new() { ["exa"] = ThisMonthOnly(old.Exa), ["parallel"] = ThisMonthOnly(old.Parallel) });
                }
                // Oldest file: one total, which was Exa's
                var single = ThisMonthOnly(JsonSerializer.Deserialize<Usage>(text));
                return new Ledger(single, new() { ["exa"] = single });
            }
            catch { return new Ledger(Fresh(), new()); }
        }
    }

    /// <summary>Films looked up this month (Dollars: all engines together).</summary>
    public Usage ThisMonth => Read().Films;
    public Usage EngineMonth(string id) => Read().Engines.GetValueOrDefault(id) ?? new Usage(DateTime.UtcNow.ToString("yyyy-MM"), 0, 0);
    public Usage ExaMonth => EngineMonth("exa");
    public Usage ParallelMonth => EngineMonth("parallel");

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
    private void Count(string engine, double dollars) => Write(l =>
    {
        var engines = new Dictionary<string, Usage>(l.Engines) { [engine] = Add(EngineOf(l, engine), dollars) };
        return new Ledger(l.Films with { Dollars = l.Films.Dollars + dollars }, engines);
    });

    private static Usage EngineOf(Ledger l, string id) => l.Engines.GetValueOrDefault(id) ?? new Usage(l.Films.Month, 0, 0);

    /// <summary>One film looked up (however many engines answered).</summary>
    private void CountFilm() => Write(l => l with { Films = Add(l.Films, 0) });

    // ---- which engines can answer now

    /// <summary>The Parallel search mode for this lookup: advanced only with a key.</summary>
    public static string ParallelModeInUse => !string.IsNullOrWhiteSpace(ServerSettings.Current.ParallelApiKey) && ServerSettings.Current.ParallelMode == "advanced" ? "advanced" : "fast";
    public static bool ParallelFree => string.IsNullOrWhiteSpace(ServerSettings.Current.ParallelApiKey);

    /// <summary>At least one engine is switched on and has the key it needs.</summary>
    public static bool Configured => SearchEngines.For(ServerSettings.Current).Any(e => SearchEngines.Ready(ServerSettings.Current, e));
    /// <summary>Every engine that's on costs nothing.</summary>
    public static bool Free
    {
        get
        {
            var s = ServerSettings.Current;
            var ready = SearchEngines.For(s).Where(e => SearchEngines.Ready(s, e)).ToList();
            return ready.Count > 0 && ready.All(e => SearchEngines.Free(s, e.Id));
        }
    }

    /// <summary>How many engines answer one lookup.</summary>
    public static int PerLookup(ServerSettings s) =>
        s.SearchEngines.Count == 0 && s.WebSearchOrder == "both" && s.ExaOn && s.ParallelOn ? 2 : Math.Clamp(s.SearchPerLookup, 1, 3);

    /// <summary>Searches used in the engine's period (today, or this month).</summary>
    public int Used(SearchEngineSetting e)
    {
        var u = EngineMonth(e.Id);
        return e.Period == "day" ? u.Today : u.Lookups;
    }

    /// <summary>Why this engine can't take a search now, or null when it can.</summary>
    public string? Unavailable(SearchEngineSetting e)
    {
        var s = ServerSettings.Current;
        if (!e.On) return "off";
        if (SearchEngines.Get(e.Id).NeedsKey && !SearchEngines.HasKey(s, e.Id)) return "no key";
        if (e.Allowance > 0 && Used(e) >= e.Allowance) return e.Period == "day" ? "today's allowance used" : "this month's allowance used";
        if (e.Budget > 0 && !SearchEngines.Free(s, e.Id) && EngineMonth(e.Id).Dollars >= e.Budget) return "monthly limit reached";
        return null;
    }

    /// <summary>The engines in order that can take a search now (Wikipedia aside when it's added to every lookup).</summary>
    private List<SearchEngineSetting> Queue(ServerSettings s) =>
        SearchEngines.For(s).Where(e => e.Id != "wikipedia" && Unavailable(e) is null).ToList();

    /// <summary>Wikipedia's article comes with every lookup when it's on, so the other engines leave Wikipedia out.</summary>
    public bool WikipediaOn => SearchEngines.For(ServerSettings.Current).FirstOrDefault(e => e.Id == "wikipedia") is { } w && Unavailable(w) is null;
    private static bool IsWikipedia(string url) => url.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a lookup can't run now, or null when at least one engine can.</summary>
    public string? Blocked()
    {
        var s = ServerSettings.Current;
        if (!Configured) return "No web search set up (AI page).";
        if (Queue(s).Count > 0) return null;
        if (WikipediaOn) return null;
        return "Every search engine has used its allowance or monthly limit.";
    }

    // Which engines answered the last lookup of each film, for its history
    private readonly ConcurrentDictionary<Guid, string> _answeredBy = new();
    /// <summary>Who answered the latest lookup of this film ("Wikipedia + Parallel (advanced)").</summary>
    public string EnginesFor(Guid itemId) => _answeredBy.TryGetValue(itemId, out var names) ? names : "web search";
    /// <summary>The engines next in line, for messages.</summary>
    public string ProviderName => string.Join(" + ", Queue(ServerSettings.Current).Take(PerLookup(ServerSettings.Current)).Select(e => Name(e.Id)));
    private static string Name(string id) => id == "parallel" ? $"Parallel ({ParallelModeInUse})" : SearchEngines.Get(id).Name;

    /// <summary>
    /// Passages about the film. Goes down the engine order until the wanted number of engines
    /// answered (one that fails or finds nothing hands over to the next), plus Wikipedia when it's
    /// added to every lookup. Null when nothing useful was found.
    /// </summary>
    public async Task<string?> FindAsync(MovieMetadata movie, CancellationToken cancellationToken)
    {
        if (Blocked() is { } why) throw new InvalidOperationException(why);
        var s = ServerSettings.Current;
        var query = $"{movie.Title} ({movie.ReleaseYear}) film" + (string.IsNullOrWhiteSpace(movie.Director) ? "" : $" directed by {movie.Director}")
            + (string.IsNullOrWhiteSpace(movie.OriginalTitle) ? "" : $" original title {movie.OriginalTitle}");

        Task<List<(string Title, string Url, string Text)>> Start(string id) => id switch
        {
            "exa" => ExaAsync(query, cancellationToken),
            "parallel" => ParallelAsync(movie, cancellationToken),
            "linkup" => LinkupAsync(movie, cancellationToken),
            "tavily" => TavilyAsync(movie, cancellationToken),
            _ => WikipediaAsync(movie, cancellationToken),
        };

        // Wikipedia alongside, when it's added to every lookup
        var skipWiki = WikipediaOn;
        var wiki = skipWiki ? Start("wikipedia") : null;

        var want = PerLookup(s);
        var queue = Queue(s);
        var answers = new List<(string Engine, List<(string Title, string Url, string Text)> Pages)>();
        var errors = new List<string>();
        var next = 0;
        while (answers.Count < want && next < queue.Count)
        {
            // Ask as many as are still wanted at once; whoever fails or finds nothing hands over to the next
            var batch = queue.Skip(next).Take(want - answers.Count).ToList();
            next += batch.Count;
            var tasks = batch.Select(e => (e.Id, Task: Start(e.Id))).ToList();
            try { await Task.WhenAll(tasks.Select(t => t.Task)); } catch { /* looked at one by one */ }
            foreach (var (id, task) in tasks)
            {
                if (!task.IsCompletedSuccessfully)
                {
                    var error = task.Exception?.GetBaseException().Message ?? "failed";
                    errors.Add($"{Name(id)}: {error}");
                    _logger.LogWarning("Web lookup: {Engine} failed for {Title}: {Error}", Name(id), movie.Title, error);
                }
                else
                {
                    var pages = skipWiki ? task.Result.Where(r => !IsWikipedia(r.Url)).ToList() : task.Result;
                    if (pages.Count == 0) _logger.LogInformation("Web lookup: {Engine} found nothing for {Title}", Name(id), movie.Title);
                    else answers.Add((id, pages));
                }
            }
        }
        if (wiki is not null)
        {
            try { var pages = await wiki; if (pages.Count > 0) answers.Insert(0, ("wikipedia", pages)); }
            catch (Exception error) when (error is not OperationCanceledException) { _logger.LogWarning("Web lookup: Wikipedia failed for {Title}: {Error}", movie.Title, error.Message); }
        }
        if (answers.Count == 0)
        {
            if (errors.Count > 0 && errors.Count == next) throw new InvalidOperationException(string.Join("; ", errors));
            CountFilm();
            _answeredBy[movie.ItemId] = "nothing found";
            return null;
        }
        CountFilm();
        _answeredBy[movie.ItemId] = string.Join(" + ", answers.Select(a => Name(a.Engine)));

        // Pages taking turns between engines, no page twice: 3 per engine (Wikipedia's article counts as one)
        static string Key(string url) { try { var u = new Uri(url); return (u.Host.Replace("www.", "") + u.AbsolutePath).TrimEnd('/').ToLowerInvariant(); } catch { return url; } }
        var cap = answers.Sum(a => a.Engine == "wikipedia" ? 1 : 3);
        var found = new List<(string Title, string Url, string Text)>();
        var seen = new HashSet<string>();
        for (var k = 0; found.Count < cap && answers.Any(a => k < a.Pages.Count); k++)
            foreach (var (_, pages) in answers)
                if (k < pages.Count && found.Count < cap && seen.Add(Key(pages[k].Url))) found.Add(pages[k]);

        var sb = new StringBuilder();
        foreach (var (title, url, text) in found)
            sb.Append($"[{title} – {new Uri(url).Host}] {text}\n\n");
        return sb.ToString().Trim();
    }

    // ---- Linkup: passages that fit the question, per page
    private async Task<List<(string Title, string Url, string Text)>> LinkupAsync(MovieMetadata movie, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.linkup.so/v1/search");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ServerSettings.Current.LinkupApiKey);
        request.Content = JsonContent.Create(new
        {
            q = $"What is the {movie.ReleaseYear} film {movie.Title}{(string.IsNullOrWhiteSpace(movie.Director) ? "" : $" by {movie.Director}")} about: plot, genre, tone, mood, themes and critical reception?",
            depth = "standard", outputType = "searchResults", maxResults = 5,
            excludeDomains = WikipediaOn ? new[] { "wikipedia.org" } : Array.Empty<string>(),
        });
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Linkup: HTTP {(int)response.StatusCode} {(body.Length > 200 ? body[..200] : body)}");
        Count("linkup", 0.005);
        using var json = JsonDocument.Parse(body);
        var found = Pages(json.RootElement, "name", "content");
        _logger.LogInformation("Web lookup (Linkup): {Title} ({Year}) → {Count} page(s), $0.005", movie.Title, movie.ReleaseYear, found.Count);
        return found;
    }

    // ---- Tavily: up to three short passages per page
    private async Task<List<(string Title, string Url, string Text)>> TavilyAsync(MovieMetadata movie, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ServerSettings.Current.TavilyApiKey);
        request.Content = JsonContent.Create(new
        {
            query = $"{movie.Title} {movie.ReleaseYear} film{(string.IsNullOrWhiteSpace(movie.Director) ? "" : $" {movie.Director}")} plot genre tone themes review",
            search_depth = "basic", chunks_per_source = 3, max_results = 5, include_answer = false,
            exclude_domains = WikipediaOn ? new[] { "wikipedia.org" } : Array.Empty<string>(),
        });
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Tavily: HTTP {(int)response.StatusCode} {(body.Length > 200 ? body[..200] : body)}");
        Count("tavily", 0.008);
        using var json = JsonDocument.Parse(body);
        var found = Pages(json.RootElement, "title", "content");
        _logger.LogInformation("Web lookup (Tavily): {Title} ({Year}) → {Count} page(s), $0.008", movie.Title, movie.ReleaseYear, found.Count);
        return found;
    }

    /// <summary>Pages from a "results" array: title, url and text fields, at most 3, each cut to 1,200 characters.</summary>
    private static List<(string Title, string Url, string Text)> Pages(JsonElement root, string titleField, string textField)
    {
        var found = new List<(string, string, string)>();
        if (!root.TryGetProperty("results", out var list) || list.ValueKind != JsonValueKind.Array) return found;
        foreach (var r in list.EnumerateArray())
        {
            if (!r.TryGetProperty("url", out var url) || !r.TryGetProperty(textField, out var t) || t.ValueKind != JsonValueKind.String) continue;
            var text = (t.GetString() ?? "").Replace('\n', ' ').Trim();
            if (text.Length < 80) continue;
            if (text.Length > 1200) text = text[..1200] + "…";
            found.Add((r.TryGetProperty(titleField, out var ti) ? ti.GetString() ?? "" : "", url.GetString() ?? "", text));
            if (found.Count == 3) break;
        }
        return found;
    }

    // ---- Wikipedia: the film's English article, its summary, plot and reception
    private async Task<List<(string Title, string Url, string Text)>> WikipediaAsync(MovieMetadata movie, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        const string api = "https://en.wikipedia.org/w/api.php?format=json&formatversion=2";

        // Find the article: a title match, preferably one saying "film" and the year
        var search = $"{movie.Title} {movie.ReleaseYear} film";
        using var found = JsonDocument.Parse(await client.GetStringAsync($"{api}&action=query&list=search&srlimit=5&srsearch={Uri.EscapeDataString(search)}", cancellationToken));
        static string Fold(string t) => new string(t.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var wanted = Fold(movie.Title);
        string? page = null;
        foreach (var hit in found.RootElement.GetProperty("query").GetProperty("search").EnumerateArray())
        {
            var hitTitle = hit.GetProperty("title").GetString() ?? "";
            var snippet = hit.TryGetProperty("snippet", out var sn) ? sn.GetString() ?? "" : "";
            if (!Fold(hitTitle).StartsWith(wanted)) continue;
            if (movie.ReleaseYear is { } year && !hitTitle.Contains(year.ToString()) && !snippet.Contains(year.ToString()) && hitTitle.Contains('(')) continue;
            page = hitTitle;
            break;
        }
        Count("wikipedia", 0);
        if (page is null) { _logger.LogInformation("Web lookup (Wikipedia): no article for {Title} ({Year})", movie.Title, movie.ReleaseYear); return new(); }

        using var article = JsonDocument.Parse(await client.GetStringAsync($"{api}&action=query&prop=extracts&explaintext=1&exsectionformat=wiki&redirects=1&titles={Uri.EscapeDataString(page)}", cancellationToken));
        var p = article.RootElement.GetProperty("query").GetProperty("pages")[0];
        var text = p.TryGetProperty("extract", out var ex) ? ex.GetString() ?? "" : "";
        if (!text.Contains("film", StringComparison.OrdinalIgnoreCase)) return new();

        // The summary before the first heading, then the plot and the reception
        static string Section(string all, params string[] names)
        {
            foreach (var name in names)
            {
                var m = System.Text.RegularExpressions.Regex.Match(all, @"\n==\s*" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*==\s*\n(.*?)(?=\n==[^=]|$)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) return System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"\n=+[^=\n]+=+\n", "\n").Trim();
            }
            return "";
        }
        static string Cut(string t, int max) => t.Length <= max ? t : t[..max] + "…";
        var lead = text.Split("\n==")[0].Trim();
        var plot = Section(text, "Plot", "Synopsis", "Premise");
        var reception = Section(text, "Reception", "Critical response", "Critical reception");
        var summary = string.Join(" ", new[] { Cut(lead, 1200), plot.Length > 0 ? "Plot: " + Cut(plot, 1500) : "", reception.Length > 0 ? "Reception: " + Cut(reception, 1000) : "" }
            .Where(x => x.Length > 0)).Replace('\n', ' ');
        var title = p.GetProperty("title").GetString() ?? page;
        _logger.LogInformation("Web lookup (Wikipedia): {Title} ({Year}) → \"{Page}\"", movie.Title, movie.ReleaseYear, title);
        return new() { (title, "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')), summary) };
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
            objective = $"What the {movie.ReleaseYear} film {movie.Title}{original}{who} is about: plot, genre, tone, mood, themes, style and critical reception. "
                + (WikipediaOn ? "Prefer Letterboxd, TMDB, IMDb, Rotten Tomatoes and film reviews; not Wikipedia." : "Prefer Wikipedia, Letterboxd, TMDB, IMDb, Rotten Tomatoes and film reviews."),
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
        Count("parallel", cost);

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
            object over = WikipediaOn
                ? new { mode = ParallelModeInUse, advanced_settings = new { source_policy = new { exclude_domains = new[] { "wikipedia.org" } } } }
                : new { mode = ParallelModeInUse };
            request.Headers.Add("x-parallel-mcp-config", JsonSerializer.Serialize(new { web_search = new { @override = over } }));
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
        var skipWiki = WikipediaOn;
        var sites = skipWiki ? FilmSites.Where(f => !IsWikipedia(f)).ToArray() : FilmSites;
        var found = await SearchAsync(query, sites, cancellationToken);
        if (found.Count == 0) found = await SearchAsync(query, null, cancellationToken, skipWiki ? new[] { "wikipedia.org" } : null);
        return found;
    }

    private async Task<List<(string Title, string Url, string Text)>> SearchAsync(string query, string[]? sites, CancellationToken cancellationToken, string[]? exclude = null)
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
            ["excludeDomains"] = exclude,
            // Only the passages that say what the film is like, not the whole page
            ["contents"] = new { highlights = new { query = "What is this film about: plot, genre, tone, mood, themes, style and critical reception", maxCharacters = 1200 } },
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Exa: HTTP {(int)response.StatusCode} {(body.Length > 200 ? body[..200] : body)}");

        using var json = JsonDocument.Parse(body);
        var cost = json.RootElement.TryGetProperty("costDollars", out var c) && c.TryGetProperty("total", out var t) ? t.GetDouble() : 0.007;
        Count("exa", cost);
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
