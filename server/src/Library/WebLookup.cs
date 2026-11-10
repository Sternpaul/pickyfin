using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// Looks a film up on the web with Exa (under one cent per film), so the AI can tag
/// films it doesn't know from real descriptions instead of guessing. Spending is
/// counted per month and stops at the budget set on the AI page.
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

    public sealed record Usage(string Month, int Lookups, double Dollars);

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
        lock (Gate) File.WriteAllText(_usagePath, JsonSerializer.Serialize(now with { Lookups = now.Lookups + 1, Dollars = now.Dollars + dollars }));
    }

    public static bool Configured => !string.IsNullOrWhiteSpace(ServerSettings.Current.ExaApiKey);

    /// <summary>Why a lookup can't run now, or null when it can.</summary>
    public string? Blocked()
    {
        if (!Configured) return "No Exa key set (AI page).";
        if (ThisMonth.Dollars >= ServerSettings.Current.WebLookupBudget) return $"This month's web lookup budget (${ServerSettings.Current.WebLookupBudget:0.##}) is used up.";
        return null;
    }

    /// <summary>A few paragraphs about the film from film sites (or anywhere, if those have nothing). Null when nothing useful was found.</summary>
    public async Task<string?> FindAsync(MovieMetadata movie, CancellationToken cancellationToken)
    {
        if (Blocked() is { } why) throw new InvalidOperationException(why);
        var query = $"{movie.Title} ({movie.ReleaseYear}) film" + (string.IsNullOrWhiteSpace(movie.Director) ? "" : $" directed by {movie.Director}")
            + (string.IsNullOrWhiteSpace(movie.OriginalTitle) ? "" : $" original title {movie.OriginalTitle}");

        var found = await SearchAsync(query, FilmSites, cancellationToken);
        if (found.Count == 0) found = await SearchAsync(query, null, cancellationToken);
        if (found.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var (title, url, text) in found)
            sb.Append($"[{title} – {new Uri(url).Host}] {text}\n\n");
        return sb.ToString().Trim();
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
