using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// Letterboxd ratings and reviews from a letterboxd-collector service
/// (https://github.com/Sternpaul/letterboxd-collector), which people run next to Pickyfin.
/// Pickyfin only reads the collector's JSON API; it never contacts Letterboxd itself.
/// Films are matched by Jellyfin item id, else by TMDB or IMDb id.
/// </summary>
public sealed class LetterboxdRatings
{
    public const string Key = "lbratings", Title = "Ratings (Letterboxd)";
    public const string ProjectUrl = "https://github.com/Sternpaul/letterboxd-collector";
    /// <summary>Where the collector is when it runs in the same Docker stack under its usual name.</summary>
    public const string DefaultUrl = "http://letterboxd-collector:8098";
    private const int Page = 500, ReviewsPerFilm = 5, ReviewChars = 700;
    /// <summary>How often the collector is read (and the TV app downloads the list), from the settings.</summary>
    private static TimeSpan Every => TimeSpan.FromHours(Math.Clamp(ServerSettings.Current.LetterboxdHours, 1, 168));

    private readonly IHttpClientFactory _http;
    private readonly Database _database;
    private readonly ILogger<LetterboxdRatings> _logger;

    public LetterboxdRatings(IHttpClientFactory http, Database database, ILogger<LetterboxdRatings> logger)
    {
        _http = http;
        _database = database;
        _logger = logger;
    }

    private static DateTime? _next;
    /// <summary>A new address or token deserves a try right away.</summary>
    public static void ResetPause() => _next = null;

    /// <summary>What the last contact with the collector showed, for the AI page and Status.</summary>
    public sealed record State(bool Reached, string Url, string Message, int? Schema, int? WithRating, int? InCollector, int Matched, DateTime At);
    public static State? Last { get; private set; }

    public static string Address(ServerSettings s) =>
        (string.IsNullOrWhiteSpace(s.LetterboxdUrl) ? DefaultUrl : s.LetterboxdUrl.Trim()).TrimEnd('/');

    public sealed record Review(string Author, double? Rating, int Likes, string Text, string? Url, bool Liked);
    public sealed record Film(Guid? JellyfinId, int? TmdbId, string? ImdbId, double? Rating, int? Count, string? Url, int[]? Histogram, List<Review> Reviews);

    private static readonly string[] HistogramKeys = { "0.5", "1.0", "1.5", "2.0", "2.5", "3.0", "3.5", "4.0", "4.5", "5.0" };

    /// <summary>Reads one movie object from the collector's API.</summary>
    public static Film? ParseFilm(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        Guid? jellyfin = Guid.TryParse(Str(e, "jellyfin_id"), out var g) ? g : null;
        int? tmdb = int.TryParse(Str(e, "tmdb_id") ?? (Num(e, "tmdb_id") is { } t ? t.ToString("0") : null), out var ti) ? ti : null;
        var imdb = Str(e, "imdb_id") is { Length: > 2 } im && im.StartsWith("tt") ? im : null;
        double? rating = null; int? count = null; string? url = null; int[]? histogram = null;
        if (e.TryGetProperty("letterboxd", out var lb) && lb.ValueKind == JsonValueKind.Object)
        {
            rating = Num(lb, "rating_avg") is { } r and > 0 and <= 5 ? Math.Round(r, 2) : null;
            count = Num(lb, "rating_count") is { } c and >= 0 ? (int)Math.Min(c, int.MaxValue) : null;
            url = Str(lb, "url") is { } u && u.StartsWith("https://letterboxd.com/") ? u : null;
            if (lb.TryGetProperty("histogram", out var h) && h.ValueKind == JsonValueKind.Object)
                histogram = HistogramKeys.Select(k => h.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Min(v.GetDouble(), int.MaxValue) : 0).ToArray();
        }
        var reviews = new List<Review>();
        if (e.TryGetProperty("reviews", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var r in rs.EnumerateArray())
            {
                var text = (Str(r, "text") ?? "").Trim();
                if (text.Length < 2 || (r.TryGetProperty("spoiler", out var sp) && sp.ValueKind == JsonValueKind.True)) continue;
                if (text.Length > ReviewChars) text = text[..ReviewChars].TrimEnd() + "…";
                var reviewUrl = Str(r, "url") is { } ru && ru.StartsWith("https://letterboxd.com/") ? ru : null;
                reviews.Add(new Review(Str(r, "author") ?? "", Num(r, "rating") is { } rr and > 0 and <= 5 ? rr : null,
                    (int)(Num(r, "likes") ?? 0), text, reviewUrl, r.TryGetProperty("liked", out var lk) && lk.ValueKind == JsonValueKind.True));
                if (reviews.Count >= ReviewsPerFilm) break;
            }
        return new Film(jellyfin, tmdb, imdb, rating, count, url, histogram, reviews);
    }

    private HttpClient Client(ServerSettings s)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        if (!string.IsNullOrWhiteSpace(s.LetterboxdToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.LetterboxdToken.Trim());
        return client;
    }

    /// <summary>Asks the collector how it's doing (health and progress); doesn't read films.</summary>
    public async Task<State> CheckAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        var url = Address(s);
        var client = Client(s);
        client.Timeout = TimeSpan.FromSeconds(10);
        var matched = Last?.Matched ?? 0;
        try
        {
            int? schema = null;
            using (var health = await client.GetAsync(url + "/health", cancellationToken))
            {
                if (!health.IsSuccessStatusCode)
                    return Last = new State(false, url, $"The collector answered {(int)health.StatusCode}", null, null, null, matched, DateTime.UtcNow);
                using var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync(cancellationToken));
                if (doc.RootElement.TryGetProperty("schema_version", out var v) && v.ValueKind == JsonValueKind.Number) schema = v.GetInt32();
            }
            using var status = await client.GetAsync(url + "/status", cancellationToken);
            if (status.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return Last = new State(false, url, "The collector wants a token (API_TOKEN), or the token is wrong", schema, null, null, matched, DateTime.UtcNow);
            if (!status.IsSuccessStatusCode)
                return Last = new State(false, url, $"The collector answered {(int)status.StatusCode}", schema, null, null, matched, DateTime.UtcNow);
            using var st = JsonDocument.Parse(await status.Content.ReadAsStringAsync(cancellationToken));
            int? withRating = st.RootElement.TryGetProperty("movies_with_rating", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : null;
            int? inCollector = st.RootElement.TryGetProperty("library", out var lib) && lib.ValueKind == JsonValueKind.Object
                ? lib.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number).Sum(p => p.Value.GetInt32()) : null;
            return Last = new State(true, url, "Connected", schema, withRating, inCollector, matched, DateTime.UtcNow);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var why = string.IsNullOrWhiteSpace(s.LetterboxdUrl) ? $"No collector found at {url}" : $"Can't reach {url}: {Jobs.Short(error.Message)}";
            return Last = new State(false, url, why, null, null, null, matched, DateTime.UtcNow);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        if (!s.LetterboxdOn) { Jobs.Idle(Key, Title, "Off"); return; }
        if (_next > DateTime.UtcNow)
        {
            if (Last is { Reached: true } ok) Jobs.Idle(Key, Title, $"{ok.Matched:N0} films with a rating", _next);
            else Jobs.Idle(Key, Title, Last?.Message ?? "Waiting", _next, error: !string.IsNullOrWhiteSpace(s.LetterboxdUrl));
            return;
        }

        var state = await CheckAsync(cancellationToken);
        if (!state.Reached)
        {
            // Without an address set, a missing collector just means it isn't used: look again later, quietly
            var quiet = string.IsNullOrWhiteSpace(s.LetterboxdUrl);
            _next = DateTime.UtcNow + (quiet ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(30));
            if (!quiet) _logger.LogWarning("Letterboxd collector: {Message}", state.Message);
            Jobs.Idle(Key, Title, quiet ? "Not set up" : state.Message, _next, error: !quiet);
            return;
        }

        Jobs.Running(Key, Title, "Reading the collector");
        List<MovieMetadata> movies;
        await using (var db = _database.Open())
            movies = await db.Movies.AsNoTracking().ToListAsync(cancellationToken);
        var byId = movies.ToDictionary(m => m.ItemId);
        var byTmdb = movies.Where(m => m.TmdbId is not null).GroupBy(m => m.TmdbId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var byImdb = movies.Where(m => !string.IsNullOrWhiteSpace(m.ImdbId)).GroupBy(m => m.ImdbId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var client = Client(s);
        var found = new Dictionary<Guid, Film>();
        for (var offset = 0; ; offset += Page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await client.GetAsync($"{state.Url}/movies?status=ok,partial&limit={Page}&offset={offset}&reviews={ReviewsPerFilm}&spoilers=0", cancellationToken);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The collector answered {(int)response.StatusCode} for its film list");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                if (ParseFilm(item) is not { Rating: not null } film) continue;
                // Same Jellyfin: the item id; another Jellyfin (or several versions): TMDB, then IMDb
                var targets = film.JellyfinId is { } id && byId.ContainsKey(id) ? new List<MovieMetadata> { byId[id] }
                    : film.TmdbId is { } t && byTmdb.TryGetValue(t, out var tl) ? tl
                    : film.ImdbId is { } i && byImdb.TryGetValue(i, out var il) ? il : null;
                if (targets is null) continue;
                foreach (var m in targets)
                    if (!found.TryGetValue(m.ItemId, out var had) || (film.Count ?? 0) > (had.Count ?? 0)) found[m.ItemId] = film;
            }
            var total = doc.RootElement.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : 0;
            if (count < Page || offset + Page >= total) break;
        }

        // Write what changed; films the collector no longer rates keep their last rating
        var changed = 0;
        await using (var db = _database.Open())
        {
            foreach (var (id, film) in found)
            {
                var m = byId[id];
                var histogram = film.Histogram is null ? null : JsonSerializer.Serialize(film.Histogram);
                var reviews = JsonSerializer.Serialize(film.Reviews);
                if (m.LbRating == film.Rating && m.LbCount == film.Count && m.LbUrl == film.Url && m.LbHistogram == histogram && m.LbReviews == reviews) continue;
                await db.Movies.Where(x => x.ItemId == id).ExecuteUpdateAsync(u => u
                    .SetProperty(x => x.LbRating, film.Rating)
                    .SetProperty(x => x.LbCount, film.Count)
                    .SetProperty(x => x.LbUrl, film.Url)
                    .SetProperty(x => x.LbHistogram, histogram)
                    .SetProperty(x => x.LbReviews, reviews)
                    .SetProperty(x => x.LbFetched, DateTime.UtcNow), cancellationToken);
                changed++;
            }
        }
        var matched = movies.Count(m => found.ContainsKey(m.ItemId) || m.LbRating is not null);
        Last = state with { Matched = matched };
        _next = DateTime.UtcNow + Every;
        if (changed > 0) _logger.LogInformation("Letterboxd: {Changed} films updated, {Matched} of {Total} films have a rating", changed, matched, movies.Count);
        Jobs.Idle(Key, Title, $"{matched:N0} films with a rating", _next);
    }
}
