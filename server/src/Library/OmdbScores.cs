using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// Real scores from OMDb (omdbapi.com): the Rotten Tomatoes Tomatometer, Metacritic and
/// the IMDb rating, looked up by the film's IMDb id, a batch every round up to the daily
/// number of requests. Films are fetched again after a while (sooner for new films, whose
/// scores still move).
/// </summary>
public sealed class OmdbScores
{
    public const string Key = "scores", Title = "Scores (OMDb)";
    private const int PerRound = 60;
    private static readonly TimeSpan Refresh = TimeSpan.FromDays(180);
    private static readonly TimeSpan RefreshNew = TimeSpan.FromDays(30);
    private static readonly object Gate = new();

    private readonly IHttpClientFactory _http;
    private readonly Database _database;
    private readonly ILogger<OmdbScores> _logger;
    private readonly string _usagePath;

    public OmdbScores(IHttpClientFactory http, Database database, DataDirectory data, ILogger<OmdbScores> logger)
    {
        _http = http;
        _database = database;
        _logger = logger;
        _usagePath = Path.Combine(data.Path, "omdb.json");
    }

    private static DateTime? _pausedUntil;
    private static string _pauseReason = "";
    /// <summary>A new key deserves a try right away.</summary>
    public static void ResetPause() => _pausedUntil = null;

    private sealed record Usage(string Day, int Requests);
    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    public int RequestsToday
    {
        get
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(_usagePath)) return 0;
                    var u = JsonSerializer.Deserialize<Usage>(File.ReadAllText(_usagePath));
                    return u?.Day == Today() ? u.Requests : 0;
                }
                catch { return 0; }
            }
        }
    }

    private void Count()
    {
        var used = RequestsToday + 1;
        lock (Gate) File.WriteAllText(_usagePath, JsonSerializer.Serialize(new Usage(Today(), used)));
    }

    public static bool Due(MovieMetadata m) =>
        m.ScoresFetched is not { } at || m.Awards is null || DateTime.UtcNow - at > (m.ReleaseYear >= DateTime.UtcNow.Year - 1 ? RefreshNew : Refresh);

    public sealed record Scores(bool Found, int? RottenTomatoes, int? Metacritic, double? Imdb, int? Year, string? Error,
        string Awards = "", int OscarWins = 0, int OscarNominations = 0);

    /// <summary>"Won 3 Oscars. 45 wins…" → (3, 3); "Nominated for 2 Oscars. …" → (0, 2).</summary>
    public static (int Wins, int Nominations) Oscars(string? awards)
    {
        if (string.IsNullOrWhiteSpace(awards)) return (0, 0);
        var won = System.Text.RegularExpressions.Regex.Match(awards, @"Won (\d+) Oscars?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var nominated = System.Text.RegularExpressions.Regex.Match(awards, @"Nominated for (\d+) Oscars?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var wins = won.Success ? int.Parse(won.Groups[1].Value) : 0;
        var noms = nominated.Success ? int.Parse(nominated.Groups[1].Value) : 0;
        return (wins, Math.Max(wins, noms));
    }

    /// <summary>Reads an OMDb answer. "N/A" and odd values count as missing.</summary>
    public static Scores Parse(JsonElement root)
    {
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (!string.Equals(Str("Response"), "True", StringComparison.OrdinalIgnoreCase))
            return new Scores(false, null, null, null, null, Str("Error"));

        int? rt = null, meta = null;
        double? imdb = null;
        if (root.TryGetProperty("Ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
            foreach (var r in ratings.EnumerateArray())
            {
                var source = r.TryGetProperty("Source", out var s) ? s.GetString() : null;
                var value = r.TryGetProperty("Value", out var v) ? v.GetString() : null;
                if (value is null) continue;
                if (source == "Rotten Tomatoes" && int.TryParse(value.TrimEnd('%'), out var p) && p is >= 0 and <= 100) rt = p;
                else if (source == "Metacritic" && int.TryParse(value.Split('/')[0], out var m) && m is >= 0 and <= 100) meta = m;
                else if (source == "Internet Movie Database" && double.TryParse(value.Split('/')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var i) && i is > 0 and <= 10) imdb = i;
            }
        if (meta is null && int.TryParse(Str("Metascore"), out var ms) && ms is >= 0 and <= 100) meta = ms;
        if (imdb is null && double.TryParse(Str("imdbRating"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ir) && ir is > 0 and <= 10) imdb = ir;
        int? year = Str("Year") is { Length: >= 4 } y && int.TryParse(y[..4], out var yy) ? yy : null;
        var awards = Str("Awards") is { } a && a != "N/A" ? a.Trim() : "";
        if (awards.Length > 200) awards = awards[..200];
        var (wins, noms) = Oscars(awards);
        return new Scores(true, rt, meta, imdb, year, null, awards, wins, noms);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        if (string.IsNullOrWhiteSpace(s.OmdbApiKey)) { Jobs.Idle(Key, Title, "Off"); return; }
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return; }
        var midnight = DateTime.UtcNow.Date.AddDays(1);
        var left = s.OmdbPerDay - RequestsToday;
        if (left <= 0) { Jobs.Idle(Key, Title, $"Today's {s.OmdbPerDay:N0} done", midnight); return; }

        List<MovieMetadata> todo;
        await using (var db = _database.Open())
            todo = (await db.Movies.AsNoTracking().Where(m => m.Kind == MovieMetadata.MovieKind).ToListAsync(cancellationToken))
                .Where(m => !s.IsLibraryExcluded(m.Libraries) && Due(m))
                .OrderBy(m => m.ScoresFetched ?? DateTime.MinValue)
                .ToList();
        if (todo.Count == 0) { Jobs.Idle(Key, Title, "Up to date"); return; }

        var batch = todo.Take(Math.Min(PerRound, left)).ToList();
        Jobs.Running(Key, Title, $"{batch.Count} films");
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        int found = 0, done = 0;
        foreach (var movie in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = !string.IsNullOrWhiteSpace(movie.ImdbId)
                ? $"i={Uri.EscapeDataString(movie.ImdbId!)}"
                : $"t={Uri.EscapeDataString(movie.Title)}&type=movie" + (movie.ReleaseYear is { } yr ? $"&y={yr}" : "");
            Scores scores;
            try
            {
                using var response = await client.GetAsync($"https://www.omdbapi.com/?apikey={Uri.EscapeDataString(s.OmdbApiKey.Trim())}&{query}", cancellationToken);
                Count();
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    scores = Parse(doc.RootElement);
                }
                catch (JsonException)
                {
                    scores = new Scores(false, null, null, null, null, $"HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Pause(TimeSpan.FromMinutes(30), "Paused: OMDb didn't answer", $"OMDb didn't answer: {error.Message}");
                break;
            }

            if (!scores.Found)
            {
                var why = scores.Error ?? "";
                if (why.Contains("limit", StringComparison.OrdinalIgnoreCase))
                {
                    Pause(TimeSpan.FromHours(3), "Paused: OMDb's daily limit reached", "OMDb says the daily limit is reached; trying again in 3 hours");
                    break;
                }
                if (why.Contains("key", StringComparison.OrdinalIgnoreCase) || why.StartsWith("HTTP"))
                {
                    Pause(TimeSpan.FromHours(1), why.Contains("key", StringComparison.OrdinalIgnoreCase) ? "Paused: OMDb doesn't accept the key" : $"Paused: OMDb answered {why}", $"OMDb: {why}");
                    break;
                }
                // Not found: remember the try, so the film waits for the next refresh instead of every round
            }
            // A title search can land on another film; only trust it for the right year
            else if (string.IsNullOrWhiteSpace(movie.ImdbId) && movie.ReleaseYear is { } want && scores.Year is { } got && Math.Abs(want - got) > 1)
                scores = scores with { Found = false };

            var rt = scores.Found ? scores.RottenTomatoes : null;
            var meta = scores.Found ? scores.Metacritic : null;
            var imdb = scores.Found ? scores.Imdb : null;
            var awards = scores.Found ? scores.Awards : "";
            var (oscarWins, oscarNoms) = scores.Found ? (scores.OscarWins, scores.OscarNominations) : (0, 0);
            if (rt is not null || meta is not null || imdb is not null) found++;
            await using (var db = _database.Open())
                await db.Movies.Where(m => m.ItemId == movie.ItemId).ExecuteUpdateAsync(u => u
                    .SetProperty(m => m.RtScore, rt)
                    .SetProperty(m => m.Metascore, meta)
                    .SetProperty(m => m.ImdbRating, imdb)
                    .SetProperty(m => m.Awards, awards)
                    .SetProperty(m => m.OscarWins, oscarWins)
                    .SetProperty(m => m.OscarNominations, oscarNoms)
                    .SetProperty(m => m.ScoresFetched, DateTime.UtcNow), cancellationToken);
            done++;
        }

        if (done > 0)
            _logger.LogInformation("OMDb scores: {Done} films fetched ({Found} with scores), {Today} requests today, {Left} films to go",
                done, found, RequestsToday, todo.Count - done);
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return; }
        var today = RequestsToday;
        Jobs.Idle(Key, Title, $"Today {today:N0} of {s.OmdbPerDay:N0}", today >= s.OmdbPerDay ? midnight : DateTime.UtcNow.AddMinutes(5));
    }

    private void Pause(TimeSpan howLong, string status, string log)
    {
        _pausedUntil = DateTime.UtcNow + howLong;
        _pauseReason = status;
        _logger.LogWarning("{Message}", log);
    }
}
