using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;

namespace Pickyfin.Server.Library;

/// <summary>
/// Keeps the movie table in step with Jellyfin, then classifies new movies with the
/// AI and enriches them with TMDB keywords.
/// </summary>
public sealed class LibrarySync
{
    private readonly JellyfinClient _jellyfin;
    private readonly MovieStore _store;
    private readonly MovieClassifier _classifier;
    private readonly SecondOpinion _secondOpinion;
    private readonly TmdbKeywordService _tmdb;
    private readonly ILogger<LibrarySync> _logger;

    public LibrarySync(JellyfinClient jellyfin, MovieStore store, MovieClassifier classifier, SecondOpinion secondOpinion, TmdbKeywordService tmdb, ILogger<LibrarySync> logger)
    {
        _jellyfin = jellyfin;
        _store = store;
        _classifier = classifier;
        _secondOpinion = secondOpinion;
        _tmdb = tmdb;
        _logger = logger;
    }

    public DateTime? LastSync { get; private set; }

    /// <summary>Movie libraries seen at the last sync.</summary>
    public List<JellyfinClient.Library> Libraries { get; private set; } = new();

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var users = await _jellyfin.GetUsersAsync(cancellationToken);
        var admin = users.FirstOrDefault(u => u.IsAdministrator && !u.IsDisabled)
            ?? throw new JellyfinException("No enabled administrator account found in Jellyfin.");

        var items = await _jellyfin.GetMoviesAsync(admin.Id, withDetails: true, cancellationToken);
        var known = (await _store.GetAllMoviesAsync(cancellationToken)).ToDictionary(m => m.ItemId);

        // Which library each movie is in, so libraries can be switched off
        var membership = new Dictionary<Guid, List<Guid>>();
        Libraries = await _jellyfin.GetMovieLibrariesAsync(cancellationToken);
        foreach (var library in Libraries)
            foreach (var id in await _jellyfin.GetLibraryMovieIdsAsync(admin.Id, library.Id, cancellationToken))
                (membership.TryGetValue(id, out var list) ? list : membership[id] = new()).Add(library.Id);

        var changed = new List<MovieMetadata>();
        foreach (var item in items)
        {
            known.TryGetValue(item.Id, out var movie);
            var isNew = movie is null;
            movie ??= new MovieMetadata { ItemId = item.Id, DateAdded = item.DateCreated ?? DateTime.UtcNow };

            var libraries = string.Join(",", membership.GetValueOrDefault(item.Id, new()).OrderBy(g => g));
            var plotChanged = !isNew && !string.Equals(movie.Plot, item.Overview, StringComparison.Ordinal);
            var genres = System.Text.Json.JsonSerializer.Serialize(item.Genres ?? new());
            var countries = string.Join(", ", item.ProductionLocations ?? new());
            var originalTitle = string.Equals(item.OriginalTitle, item.Name, StringComparison.OrdinalIgnoreCase) ? null : item.OriginalTitle;
            if (!isNew && !plotChanged && movie.Title == item.Name && movie.Libraries == libraries
                && movie.CommunityRating == item.CommunityRating && movie.CriticRating == item.CriticRating
                && movie.Genres == genres && movie.Countries == countries && movie.OriginalTitle == originalTitle)
                continue;

            movie.Libraries = libraries;
            movie.Title = item.Name;
            movie.ReleaseYear = item.ProductionYear;
            movie.Plot = item.Overview;
            movie.CommunityRating = item.CommunityRating;
            movie.CriticRating = item.CriticRating;
            movie.ImdbId = Provider(item, "Imdb");
            movie.TmdbId = int.TryParse(Provider(item, "Tmdb"), out var tmdbId) ? tmdbId : null;
            movie.Director = item.People?.FirstOrDefault(p => p.Type == "Director")?.Name;
            movie.Cast = string.Join(", ", (item.People ?? new()).Where(p => p.Type == "Actor").Take(5).Select(p => p.Name));
            movie.Genres = genres;
            movie.Countries = countries;
            movie.OriginalTitle = originalTitle;
            // A changed plot means the old tags may be wrong; classify again
            if (plotChanged && !movie.ManuallyEdited) movie.IsClassified = false;
            changed.Add(movie);
        }

        if (changed.Count > 0) await _store.SaveMoviesAsync(changed, cancellationToken);
        var removed = await _store.DeleteMoviesNotInAsync(items.Select(i => i.Id).ToHashSet(), cancellationToken);
        _logger.LogInformation("Library: {Total} movies, {Changed} new or changed, {Removed} removed", items.Count, changed.Count, removed);

        // TMDB keywords first: quick, free, and the AI tagging can use them
        if (!string.IsNullOrWhiteSpace(ServerSettings.Current.TmdbApiKey))
        {
            var missing = (await _store.GetAllMoviesAsync(cancellationToken)).Where(m => m.Keywords is null && !ServerSettings.Current.IsLibraryExcluded(m.Libraries)).ToList();
            if (missing.Count > 0) await _tmdb.EnrichKeywordsAsync(ServerSettings.Current.TmdbApiKey, missing, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(ServerSettings.Current.ApiKey))
        {
            await _classifier.ClassifyPendingMoviesAsync(cancellationToken);
            await GenreSweepAsync(cancellationToken);
            await _secondOpinion.CheckPendingAsync(cancellationToken);
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
        var flagged = (await _store.GetAllMoviesAsync(cancellationToken))
            .Where(m => m.IsClassified && !m.ManuallyEdited && !m.Checked && m.Issue is null)
            .Select(m => (Movie: m, Problems: GenreCheck.Problems(m)))
            .Where(x => x.Problems.Count > 0)
            .ToList();
        if (flagged.Count == 0) return;
        foreach (var (movie, problems) in flagged) movie.Issue = string.Join("\n", problems);
        await _store.SaveMoviesAsync(flagged.Select(x => x.Movie), cancellationToken);
        _logger.LogInformation("Genre check: {Count} movie(s) look off, see Issues", flagged.Count);
    }

    private static string? Provider(JellyfinItem item, string name) =>
        item.ProviderIds?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
