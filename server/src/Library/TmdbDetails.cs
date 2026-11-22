using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// From TMDB, one request per film: the series it belongs to (so parts come in order)
/// and the films TMDB's viewers liked alongside it (a crowd link between films).
/// For a TV series: the shows liked alongside it (TV ids, linked only to series) and who created it.
/// A batch every round; fetched again after three months, since recommendations grow.
/// </summary>
public sealed class TmdbDetails
{
    public const string Key = "tmdb", Title = "Series and related (TMDB)";
    private const int PerRound = 150;
    private static readonly TimeSpan Refresh = TimeSpan.FromDays(90);

    private readonly IHttpClientFactory _http;
    private readonly Database _database;
    private readonly ILogger<TmdbDetails> _logger;
    private static DateTime? _pausedUntil;
    private static string _pauseReason = "";

    public TmdbDetails(IHttpClientFactory http, Database database, ILogger<TmdbDetails> logger)
    {
        _http = http;
        _database = database;
        _logger = logger;
    }

    public static void ResetPause() => _pausedUntil = null;

    public static bool Wanted(MovieMetadata m) =>
        (m.TmdbId is not null || !string.IsNullOrWhiteSpace(m.ImdbId)) && (m.DetailsFetched is not { } at || DateTime.UtcNow - at > Refresh);

    public sealed record Details(int? CollectionId, string? CollectionName, List<int> Recommendations, List<string>? CreatedBy = null);

    /// <summary>Reads TMDB's /movie/{id} or /tv/{id} ?append_to_response=recommendations answer.</summary>
    public static Details Parse(JsonElement root)
    {
        int? collectionId = null;
        string? collectionName = null;
        if (root.TryGetProperty("belongs_to_collection", out var c) && c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty("id", out var cid) && cid.TryGetInt32(out var id))
        {
            collectionId = id;
            collectionName = c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        var recs = new List<int>();
        if (root.TryGetProperty("recommendations", out var r) && r.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var item in results.EnumerateArray())
                if (item.TryGetProperty("id", out var rid) && rid.TryGetInt32(out var tmdb) && !recs.Contains(tmdb)) recs.Add(tmdb);
        // TV only: the people who created the show
        var createdBy = new List<string>();
        if (root.TryGetProperty("created_by", out var cb) && cb.ValueKind == JsonValueKind.Array)
            foreach (var person in cb.EnumerateArray())
                if (person.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String && pn.GetString() is { Length: > 0 } name && !createdBy.Contains(name))
                    createdBy.Add(name);
        return new Details(collectionId, collectionName, recs.Take(20).ToList(), createdBy.Count > 0 ? createdBy.Take(3).ToList() : null);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        var key = s.TmdbApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(key)) { Jobs.Idle(Key, Title, "Off"); return; }
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return; }

        List<MovieMetadata> todo;
        await using (var db = _database.Open())
            todo = (await db.Movies.AsNoTracking().ToListAsync(cancellationToken))
                .Where(m => !s.IsLibraryExcluded(m.Libraries) && Wanted(m))
                .OrderBy(m => m.IsSeries) // films first
                .ThenBy(m => m.DetailsFetched ?? DateTime.MinValue)
                .ToList();
        if (todo.Count == 0) { Jobs.Idle(Key, Title, "Up to date"); return; }

        var batch = todo.Take(PerRound).ToList();
        Jobs.Running(Key, Title, $"{batch.Count} titles");
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        int done = 0, inSeries = 0;
        foreach (var movie in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var tmdbId = movie.TmdbId;
                if (tmdbId is null)
                {
                    using var find = await client.GetAsync($"https://api.themoviedb.org/3/find/{Uri.EscapeDataString(movie.ImdbId!)}?api_key={Uri.EscapeDataString(key)}&external_source=imdb_id", cancellationToken);
                    if (!await CheckAsync(find)) break;
                    using var found = JsonDocument.Parse(await find.Content.ReadAsStringAsync(cancellationToken));
                    if (found.RootElement.TryGetProperty(movie.IsSeries ? "tv_results" : "movie_results", out var hits) && hits.ValueKind == JsonValueKind.Array
                        && hits.GetArrayLength() > 0 && hits[0].TryGetProperty("id", out var hid) && hid.TryGetInt32(out var h))
                        tmdbId = h;
                }

                var details = new Details(null, null, new());
                if (tmdbId is { } tid)
                {
                    // Movie and TV ids are separate ranges: a series must be asked for as /tv/
                    using var response = await client.GetAsync($"https://api.themoviedb.org/3/{(movie.IsSeries ? "tv" : "movie")}/{tid}?api_key={Uri.EscapeDataString(key)}&append_to_response=recommendations", cancellationToken);
                    if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        if (!await CheckAsync(response)) break;
                        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                        details = Parse(doc.RootElement);
                    }
                }
                if (details.CollectionId is not null) inSeries++;
                var recs = JsonSerializer.Serialize(details.Recommendations);
                await using (var db = _database.Open())
                    await db.Movies.Where(m => m.ItemId == movie.ItemId).ExecuteUpdateAsync(u => u
                        .SetProperty(m => m.TmdbId, tmdbId)
                        .SetProperty(m => m.CollectionId, details.CollectionId)
                        .SetProperty(m => m.CollectionName, details.CollectionName)
                        .SetProperty(m => m.TmdbRecs, recs)
                        // A series' creators stand where a film's director does
                        .SetProperty(m => m.Director, m => details.CreatedBy != null ? string.Join(", ", details.CreatedBy) : m.Director)
                        .SetProperty(m => m.DetailsFetched, DateTime.UtcNow), cancellationToken);
                done++;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Pause(TimeSpan.FromMinutes(30), "Paused: TMDB didn't answer", $"TMDB didn't answer: {error.Message}");
                break;
            }
            await Task.Delay(40, cancellationToken); // well under TMDB's rate limit
        }

        if (done > 0)
            _logger.LogInformation("TMDB: {Done} titles fetched ({Series} part of a film series), {Left} to go", done, inSeries, todo.Count - done);
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return; }
        Jobs.Idle(Key, Title, todo.Count - done > 0 ? $"{todo.Count - done:N0} titles to go" : "Up to date", todo.Count - done > 0 ? DateTime.UtcNow.AddMinutes(5) : null);
    }

    /// <summary>False (and paused) when TMDB refuses the key or asks us to slow down.</summary>
    private Task<bool> CheckAsync(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        if (code == 401) Pause(TimeSpan.FromHours(1), "Paused: TMDB doesn't accept the key", "TMDB doesn't accept the key");
        else if (code == 429) Pause(TimeSpan.FromMinutes(10), "Paused: TMDB asked to slow down", "TMDB asked to slow down");
        else if (code >= 400) Pause(TimeSpan.FromMinutes(30), $"Paused: TMDB answered HTTP {code}", $"TMDB answered HTTP {code}");
        else return Task.FromResult(true);
        return Task.FromResult(false);
    }

    private void Pause(TimeSpan howLong, string status, string log)
    {
        _pausedUntil = DateTime.UtcNow + howLong;
        _pauseReason = status;
        _logger.LogWarning("{Message}", log);
    }
}
