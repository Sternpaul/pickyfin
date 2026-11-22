using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace Pickyfin.Server.Library;

/// <summary>
/// Keeps the movie table in step with Jellyfin (movies, and series as a taste signal), then
/// classifies new titles with the AI and enriches them with TMDB keywords.
/// </summary>
public sealed class LibrarySync
{

    private readonly JellyfinClient _jellyfin;
    private readonly MovieStore _store;
    private readonly MovieClassifier _classifier;
    private readonly SecondOpinion _secondOpinion;
    private readonly TmdbKeywordService _tmdb;
    private readonly ILogger<LibrarySync> _logger;

    public LibrarySync(JellyfinClient jellyfin, MovieStore store, MovieClassifier classifier, SecondOpinion secondOpinion, TmdbKeywordService tmdb, ILogger<LibrarySync> logger, MovieHistory history, Database database)
    {
        _database = database;
        _history = history;
        _jellyfin = jellyfin;
        _store = store;
        _classifier = classifier;
        _secondOpinion = secondOpinion;
        _tmdb = tmdb;
        _logger = logger;
    }

    private readonly MovieHistory _history;
    private readonly Database _database;

    public DateTime? LastSync { get; private set; }

    /// <summary>Movie, TV and mixed libraries seen at the last sync.</summary>
    public List<JellyfinClient.Library> Libraries { get; private set; } = new();

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var users = await _jellyfin.GetUsersAsync(cancellationToken);
        var admin = users.FirstOrDefault(u => u.IsAdministrator && !u.IsDisabled)
            ?? throw new JellyfinException("No enabled administrator account found in Jellyfin.");

        var movies = await _jellyfin.GetMoviesAsync(admin.Id, withDetails: true, cancellationToken);
        var series = await _jellyfin.GetSeriesAsync(admin.Id, withDetails: true, cancellationToken);
        var known = (await _store.GetAllIncludingGoneAsync(cancellationToken)).ToDictionary(m => m.ItemId);

        // Which library each title is in, so libraries can be switched off
        Libraries = await _jellyfin.GetLibrariesAsync(cancellationToken);
        var membership = new Dictionary<Guid, List<Guid>>();
        foreach (var library in Libraries)
        {
            var ids = new List<Guid>();
            if (library.HoldsMovies) ids.AddRange(await _jellyfin.GetLibraryItemIdsAsync(admin.Id, library.Id, "Movie", cancellationToken));
            if (library.HoldsSeries) ids.AddRange(await _jellyfin.GetLibraryItemIdsAsync(admin.Id, library.Id, "Series", cancellationToken));
            foreach (var id in ids)
                (membership.TryGetValue(id, out var list) ? list : membership[id] = new()).Add(library.Id);
        }

        var lastWatched = await LastWatchedSeriesAsync(cancellationToken);
        var changed = new List<MovieMetadata>();
        foreach (var item in movies)
            if (Apply(item, MovieMetadata.MovieKind, known, membership, null) is { } movie) changed.Add(movie);
        foreach (var item in series)
            if (Apply(item, MovieMetadata.SeriesKind, known, membership, lastWatched.TryGetValue(item.Id, out var at) ? at : null) is { } show) changed.Add(show);

        if (changed.Count > 0) await _store.SaveMoviesAsync(changed, cancellationToken);
        // Titles Jellyfin no longer lists are only hidden: deleting them (with tags that took days to
        // make) waits for the admin to confirm on the admin page, in case Jellyfin lost them by accident
        var (hidden, back) = await _store.MarkGoneAsync(movies.Select(i => i.Id).ToHashSet(), MovieMetadata.MovieKind, cancellationToken);
        var (hiddenSeries, backSeries) = await _store.MarkGoneAsync(series.Select(i => i.Id).ToHashSet(), MovieMetadata.SeriesKind, cancellationToken);
        hidden += hiddenSeries;
        back += backSeries;
        if (hidden > 0) _logger.LogWarning("Library: {Count} title(s) are no longer in Jellyfin; hidden, and kept until you confirm their removal on the admin page", hidden);
        if (back > 0) _logger.LogInformation("Library: {Count} title(s) are back in Jellyfin, with their tags as before", back);
        var orphans = await _store.DeleteOrphanedEmbeddingsAsync(cancellationToken);
        if (orphans > 0) _logger.LogInformation("Library: cleared {Count} stored meanings of films that no longer exist", orphans);
        var changedSeries = changed.Count(m => m.IsSeries);
        _logger.LogInformation("Library: {Movies} movies and {Series} series, {Changed} new or changed ({ChangedSeries} of them series), {Hidden} no longer in Jellyfin",
            movies.Count, series.Count, changed.Count, changedSeries, hidden);

        // TMDB keywords first: quick, free, and the AI tagging can use them
        if (!string.IsNullOrWhiteSpace(ServerSettings.Current.TmdbApiKey))
        {
            var missing = (await _store.GetAllTitlesAsync(cancellationToken)).OrderBy(m => m.IsSeries).Where(m => m.Keywords is null && !ServerSettings.Current.IsLibraryExcluded(m.Libraries)).ToList();
            if (missing.Count > 0) await _tmdb.EnrichKeywordsAsync(ServerSettings.Current.TmdbApiKey, missing, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(ServerSettings.Current.ApiKey))
        {
            await _classifier.ClassifyPendingMoviesAsync(cancellationToken, onlyNew: true);
            await GenreSweepAsync(cancellationToken);
        }
        else
            _logger.LogWarning("No AI key set: movies are not classified, recommendations fall back to ratings");


        LastSync = DateTime.UtcNow;
    }

    /// <summary>
    /// The free genre check for movies tagged before it existed (or whose genres arrived
    /// later). Movies already reviewed or cleared by the admin are left alone.
    /// </summary>
    private async Task GenreSweepAsync(CancellationToken cancellationToken)
    {
        var movies = await _store.GetAllTitlesAsync(cancellationToken);

        // Looked-up films were described from the web: drop genre-check flags left on them
        var cleared = movies.Where(m => m.Issue is not null && !string.IsNullOrEmpty(m.WebNotes)).ToList()
            .Where(m =>
            {
                var kept = m.Issue!.Split('\n').Where(l => !GenreCheck.IsGenreLine(l)).ToList();
                var issue = kept.Count == 0 ? null : string.Join("\n", kept);
                if (issue == m.Issue) return false;
                m.Issue = issue;
                return true;
            }).ToList();
        if (cleared.Count > 0)
        {
            await _store.SaveMoviesAsync(cleared, cancellationToken);
            _logger.LogInformation("Genre check: cleared {Count} looked-up movie(s)", cleared.Count);
        }

        var flagged = movies
            .Where(m => m.IsClassified && !m.ManuallyEdited && !m.Checked && m.Issue is null && string.IsNullOrEmpty(m.WebNotes))
            .Select(m => (Movie: m, Problems: GenreCheck.Problems(m)))
            .Where(x => x.Problems.Count > 0)
            .ToList();
        if (flagged.Count == 0) return;
        foreach (var (movie, problems) in flagged) movie.Issue = string.Join("\n", problems);
        await _store.SaveMoviesAsync(flagged.Select(x => x.Movie), cancellationToken);
        await _history.AddAsync(flagged.Select(x => MovieHistory.Entry(x.Movie, "genre", null, null, string.Join("\n", x.Problems))), cancellationToken);
        _logger.LogInformation("Genre check: {Count} movie(s) look off, see Issues", flagged.Count);
    }

    /// <summary>
    /// Updates (or creates) the stored title from Jellyfin's item. Null when nothing changed.
    /// </summary>
    internal static MovieMetadata? Apply(JellyfinItem item, string kind, IReadOnlyDictionary<Guid, MovieMetadata> known,
        IReadOnlyDictionary<Guid, List<Guid>> membership, DateTime? lastWatched)
    {
        known.TryGetValue(item.Id, out var movie);
        var isNew = movie is null;
        movie ??= new MovieMetadata { ItemId = item.Id, Kind = kind, DateAdded = item.DateCreated ?? DateTime.UtcNow };

        var libraries = string.Join(",", membership.GetValueOrDefault(item.Id, new()).OrderBy(g => g));
        var plotChanged = !isNew && !string.Equals(movie.Plot, item.Overview, StringComparison.Ordinal);
        var genres = System.Text.Json.JsonSerializer.Serialize(item.Genres ?? new());
        var countries = string.Join(", ", item.ProductionLocations ?? new());
        var originalTitle = string.Equals(item.OriginalTitle, item.Name, StringComparison.OrdinalIgnoreCase) ? null : item.OriginalTitle;
        // For a series: the length of an episode
        int? minutes = item.RunTimeTicks is > 0 ? (int)Math.Round(item.RunTimeTicks.Value / 600_000_000.0) : null;
        var series = kind == MovieMetadata.SeriesKind;
        int? seasons = series ? item.ChildCount : null;
        int? episodes = series ? item.RecursiveItemCount : null;
        int? endYear = series ? item.EndDate?.Year : null;
        var status = series ? item.Status : null;
        var network = series ? item.Studios?.FirstOrDefault()?.Name : null;
        if (!isNew && !plotChanged && movie.Title == item.Name && movie.Libraries == libraries
            && movie.CommunityRating == item.CommunityRating && movie.CriticRating == item.CriticRating
            && movie.Genres == genres && movie.Countries == countries && movie.OriginalTitle == originalTitle && movie.Minutes == minutes
            && movie.Seasons == seasons && movie.Episodes == episodes && movie.EndYear == endYear && movie.Status == status && movie.Network == network
            && movie.LastWatched == lastWatched)
            return null;

        movie.Kind = kind;
        movie.Libraries = libraries;
        movie.Title = item.Name;
        movie.ReleaseYear = item.ProductionYear;
        movie.Plot = item.Overview;
        movie.CommunityRating = item.CommunityRating;
        movie.CriticRating = item.CriticRating;
        movie.ImdbId = Provider(item, "Imdb");
        movie.TmdbId = int.TryParse(Provider(item, "Tmdb"), out var tmdbId) ? tmdbId : null;
        // A series' creators come from TMDB (Jellyfin rarely has a director for one); keep them
        if (!series || movie.Director is null)
            movie.Director = item.People?.FirstOrDefault(p => p.Type == "Director")?.Name;
        movie.Cast = string.Join(", ", (item.People ?? new()).Where(p => p.Type == "Actor").Take(5).Select(p => p.Name));
        movie.Genres = genres;
        movie.Countries = countries;
        movie.OriginalTitle = originalTitle;
        movie.Minutes = minutes;
        (movie.Seasons, movie.Episodes, movie.EndYear, movie.Status, movie.Network, movie.LastWatched) = (seasons, episodes, endYear, status, network, lastWatched);
        // A changed plot means the old tags may be wrong; classify again
        if (plotChanged && !movie.ManuallyEdited) movie.IsClassified = false;
        return movie;
    }

    /// <summary>
    /// Per series, when someone switched on last finished one of its episodes, so watched series
    /// are described first. A failure only loses that order.
    /// </summary>
    private async Task<Dictionary<Guid, DateTime>> LastWatchedSeriesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, DateTime>();
        List<Guid> enabled;
        await using (var db = _database.Open())
            enabled = await db.Users.AsNoTracking().Where(u => u.Enabled).Select(u => u.UserId).ToListAsync(cancellationToken);
        foreach (var userId in enabled)
        {
            try
            {
                foreach (var episode in await _jellyfin.GetWatchedEpisodesAsync(userId, cancellationToken))
                {
                    if (episode.SeriesId is not { } seriesId) continue;
                    // Marked as played by hand: no date, but still watched
                    var at = episode.UserData?.LastPlayedDate ?? DateTime.UnixEpoch;
                    if (!result.TryGetValue(seriesId, out var known) || at > known) result[seriesId] = at;
                }
            }
            catch (Exception error) when (error is JellyfinException or HttpRequestException)
            {
                _logger.LogWarning("Library: couldn't read watched episodes of a user: {Error}", error.Message);
            }
        }
        return result;
    }

    private static string? Provider(JellyfinItem item, string name) =>
        item.ProviderIds?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
