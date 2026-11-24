using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;

namespace Pickyfin.Server.Data;

/// <summary>The movie table, with the methods the classifier and TMDB enrichment use.</summary>
public sealed class MovieStore
{
    private readonly Database _database;

    public MovieStore(Database database) => _database = database;

    /// <summary>Movies only: everything that recommends, matches Letterboxd films or counts films uses this.</summary>
    public async Task<List<MovieMetadata>> GetAllMoviesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        return await db.Movies.AsNoTracking().Where(m => m.Kind == MovieMetadata.MovieKind).ToListAsync(cancellationToken);
    }

    /// <summary>Series only.</summary>
    public async Task<List<MovieMetadata>> GetAllSeriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        return await db.Movies.AsNoTracking().Where(m => m.Kind == MovieMetadata.SeriesKind).ToListAsync(cancellationToken);
    }

    /// <summary>Movies and series: for the pipeline steps that describe and enrich every title the same way.</summary>
    public async Task<List<MovieMetadata>> GetAllTitlesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        return await db.Movies.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<List<MovieMetadata>> GetMoviesNeedingClassificationAsync(bool includeOutdated, CancellationToken cancellationToken = default, bool includeQueued = true)
    {
        await using var db = _database.Open();
        var pending = await db.Movies.AsNoTracking()
            .Where(m => !m.ManuallyEdited && (!m.IsClassified || (includeQueued && m.RedoDescribe) || (includeOutdated && m.VocabularyVersion < ClassificationVocabulary.Version)))
            .ToListAsync(cancellationToken);
        // Movies before series: a new film never waits behind a backlog of series. Series someone
        // watched go first (they're what tells us about taste), most recently watched first.
        // Within each: new ones first, then the ones queued to be described again.
        pending = pending
            .OrderBy(m => m.IsSeries)
            .ThenByDescending(m => m.IsSeries ? m.LastWatched ?? DateTime.MinValue : DateTime.MinValue)
            .ThenBy(m => m.IsClassified).ThenBy(m => m.Title).ToList();
        // Movies only in switched-off libraries are never recommended, so don't pay to classify them
        return pending.Where(m => !ServerSettings.Current.IsLibraryExcluded(m.Libraries)).ToList();
    }

    /// <summary>Series not described yet (in libraries that are switched on): tagged a few calls per round.</summary>
    public async Task<int> CountUntaggedSeriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var libraries = await db.Movies.AsNoTracking().Where(m => m.Kind == MovieMetadata.SeriesKind && !m.IsClassified && !m.ManuallyEdited)
            .Select(m => m.Libraries).ToListAsync(cancellationToken);
        return libraries.Count(l => !ServerSettings.Current.IsLibraryExcluded(l));
    }

    /// <summary>Films queued to be described again.</summary>
    public async Task<int> CountRedoAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        return await db.Movies.CountAsync(m => m.RedoDescribe && !m.ManuallyEdited, cancellationToken);
    }

    /// <param name="kind">"movie", "series", or null for both.</param>
    public async Task<(int Total, int Classified, int Issues, int Reviewed, int LookedUp)> GetStatsAsync(CancellationToken cancellationToken = default, string? kind = MovieMetadata.MovieKind)
    {
        // Only titles in libraries that are switched on
        await using var db = _database.Open();
        var rows = await db.Movies.AsNoTracking().Where(m => kind == null || m.Kind == kind).Select(m => new { m.Libraries, m.IsClassified, HasIssue = m.Issue != null, Reviewed = m.Checked || m.ManuallyEdited || (m.WebNotes != null && m.WebNotes != ""), LookedUp = m.WebNotes != null }).ToListAsync(cancellationToken);
        var counted = rows.Where(r => !ServerSettings.Current.IsLibraryExcluded(r.Libraries)).ToList();
        return (counted.Count, counted.Count(r => r.IsClassified), counted.Count(r => r.HasIssue), counted.Count(r => r.IsClassified && r.Reviewed), counted.Count(r => r.IsClassified && r.LookedUp));
    }

    /// <summary>Inserts or replaces whole rows.</summary>
    public async Task SaveMoviesAsync(IEnumerable<MovieMetadata> movies, CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var list = movies.ToList();
        var ids = list.Select(m => m.ItemId).ToList();
        var rows = await db.Movies.IgnoreQueryFilters().AsNoTracking().Where(m => ids.Contains(m.ItemId)).Select(m => new { m.ItemId, m.ManuallyEdited, m.RtScore, m.Metascore, m.ImdbRating, m.ScoresFetched, m.Awards, m.OscarWins, m.OscarNominations,
                m.CollectionId, m.CollectionName, m.TmdbRecs, m.DetailsFetched, m.LbRating, m.LbCount, m.LbUrl, m.LbHistogram, m.LbReviews, m.LbFetched }).ToListAsync(cancellationToken);
        var scores = rows.ToDictionary(r => r.ItemId);
        var existing = rows.Select(r => r.ItemId).ToHashSet();
        // A background job holding an older copy must not overwrite a hand edit made meanwhile
        var handEdited = rows.Where(r => r.ManuallyEdited).Select(r => r.ItemId).ToHashSet();
        foreach (var movie in list)
        {
            if (handEdited.Contains(movie.ItemId) && !movie.ManuallyEdited) continue;
            movie.LastUpdated = DateTime.UtcNow;
            if (scores.TryGetValue(movie.ItemId, out var sc))
            {
                (movie.RtScore, movie.Metascore, movie.ImdbRating, movie.ScoresFetched) = (sc.RtScore, sc.Metascore, sc.ImdbRating, sc.ScoresFetched);
                (movie.Awards, movie.OscarWins, movie.OscarNominations) = (sc.Awards, sc.OscarWins, sc.OscarNominations);
                (movie.CollectionId, movie.CollectionName, movie.TmdbRecs, movie.DetailsFetched) = (sc.CollectionId, sc.CollectionName, sc.TmdbRecs, sc.DetailsFetched);
                (movie.LbRating, movie.LbCount, movie.LbUrl, movie.LbHistogram, movie.LbReviews, movie.LbFetched) = (sc.LbRating, sc.LbCount, sc.LbUrl, sc.LbHistogram, sc.LbReviews, sc.LbFetched);
            }
            if (existing.Contains(movie.ItemId)) db.Movies.Update(movie);
            else db.Movies.Add(movie);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Every film, also those Jellyfin no longer lists (for the library sync).</summary>
    public async Task<List<MovieMetadata>> GetAllIncludingGoneAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        return await db.Movies.IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Titles of one kind ("movie" or "series") that Jellyfin doesn't list (any more) are hidden;
    /// ones it lists again come back. Only rows of that kind are touched, so a list of movies
    /// never hides the series. Returns how many were newly hidden and how many came back.
    /// </summary>
    public async Task<(int Hidden, int Back)> MarkGoneAsync(HashSet<Guid> liveIds, string kind, CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var all = await db.Movies.IgnoreQueryFilters().Where(m => m.Kind == kind).Select(m => new { m.ItemId, m.GoneSince }).ToListAsync(cancellationToken);
        var hide = all.Where(m => m.GoneSince is null && !liveIds.Contains(m.ItemId)).Select(m => m.ItemId).ToList();
        var back = all.Where(m => m.GoneSince is not null && liveIds.Contains(m.ItemId)).Select(m => m.ItemId).ToList();
        var now = DateTime.UtcNow;
        foreach (var chunk in hide.Chunk(500))
            await db.Movies.IgnoreQueryFilters().Where(m => chunk.Contains(m.ItemId)).ExecuteUpdateAsync(u => u.SetProperty(m => m.GoneSince, now), cancellationToken);
        foreach (var chunk in back.Chunk(500))
            await db.Movies.IgnoreQueryFilters().Where(m => chunk.Contains(m.ItemId)).ExecuteUpdateAsync(u => u.SetProperty(m => m.GoneSince, (DateTime?)null), cancellationToken);
        return (hide.Count, back.Count);
    }

    /// <summary>
    /// A title Jellyfin now lists under a new id (the file was swapped or moved) is the same title:
    /// a hidden title with the same TMDB or IMDb id as a listed one has been replaced.
    /// - The new one has nothing of its own yet (not tagged, not edited): the old record moves to
    ///   the new id, with its tags, edits, meanings and history, so nothing has to be done again.
    /// - The new one already has its own tags: the old record is a leftover and goes.
    /// Returns (moved over, leftovers removed), with the titles for the log.
    /// </summary>
    public async Task<(List<string> Moved, List<string> Removed)> ResolveReplacedAsync(string kind, CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var rows = await db.Movies.IgnoreQueryFilters().AsNoTracking().Where(m => m.Kind == kind && (m.TmdbId != null || m.ImdbId != null))
            .Select(m => new { m.ItemId, m.Title, m.ReleaseYear, m.TmdbId, m.ImdbId, m.GoneSince, m.IsClassified, m.ManuallyEdited, m.Libraries }).ToListAsync(cancellationToken);
        var live = rows.Where(r => r.GoneSince is null).ToList();
        var moved = new List<string>();
        var removed = new List<string>();
        var taken = new HashSet<Guid>();
        // the old records with the most work in them first
        foreach (var old in rows.Where(r => r.GoneSince is not null).OrderByDescending(r => r.IsClassified).ThenByDescending(r => r.ManuallyEdited))
        {
            var match = live.FirstOrDefault(l => !taken.Contains(l.ItemId)
                && ((old.TmdbId is { } t && l.TmdbId == t) || (!string.IsNullOrWhiteSpace(old.ImdbId) && string.Equals(l.ImdbId, old.ImdbId, StringComparison.OrdinalIgnoreCase))));
            if (match is null) continue;
            taken.Add(match.ItemId);
            var title = $"{old.Title}{(old.ReleaseYear is { } y ? $" ({y})" : "")}";
            // EF keeps ids as upper-case text
            string Id(Guid g) => g.ToString().ToUpperInvariant();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!match.IsClassified && !match.ManuallyEdited && old.IsClassified)
            {
                await db.Embeddings.Where(e => e.ItemId == match.ItemId).ExecuteDeleteAsync(cancellationToken);
                await db.History.Where(h => h.ItemId == match.ItemId).ExecuteDeleteAsync(cancellationToken);
                await db.Movies.IgnoreQueryFilters().Where(m => m.ItemId == match.ItemId).ExecuteDeleteAsync(cancellationToken);
                // the key changes, which EF won't do: plain SQL
                await db.Database.ExecuteSqlRawAsync("UPDATE Movies SET ItemId = {0}, GoneSince = NULL, Libraries = {1} WHERE ItemId = {2}",
                    new object?[] { Id(match.ItemId), match.Libraries, Id(old.ItemId) }, cancellationToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE Embeddings SET ItemId = {0} WHERE ItemId = {1}", new object[] { Id(match.ItemId), Id(old.ItemId) }, cancellationToken);
                await db.Database.ExecuteSqlRawAsync("UPDATE History SET ItemId = {0} WHERE ItemId = {1}", new object[] { Id(match.ItemId), Id(old.ItemId) }, cancellationToken);
                moved.Add(title);
            }
            else
            {
                await db.Embeddings.Where(e => e.ItemId == old.ItemId).ExecuteDeleteAsync(cancellationToken);
                await db.History.Where(h => h.ItemId == old.ItemId).ExecuteDeleteAsync(cancellationToken);
                await db.Movies.IgnoreQueryFilters().Where(m => m.ItemId == old.ItemId).ExecuteDeleteAsync(cancellationToken);
                removed.Add(title);
            }
            await tx.CommitAsync(cancellationToken);
        }
        return (moved, removed);
    }

    /// <summary>The films waiting for the admin to confirm their removal.</summary>
    public async Task<List<MovieMetadata>> GetGoneAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        return await db.Movies.IgnoreQueryFilters().AsNoTracking().Where(m => m.GoneSince != null).OrderBy(m => m.Title).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes films the admin confirmed, with what was kept for them (their meanings for every
    /// model and their history). Only films that are still gone. Returns the titles removed.
    /// </summary>
    public async Task<List<string>> DeleteGoneAsync(IReadOnlyCollection<Guid>? only, CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var gone = await db.Movies.IgnoreQueryFilters().Where(m => m.GoneSince != null).ToListAsync(cancellationToken);
        if (only is not null) gone = gone.Where(m => only.Contains(m.ItemId)).ToList();
        if (gone.Count == 0) return new();
        var ids = gone.Select(m => m.ItemId).ToList();
        db.Movies.RemoveRange(gone);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var chunk in ids.Chunk(500))
        {
            await db.Embeddings.Where(e => e.Kind == "film" && chunk.Contains(e.ItemId)).ExecuteDeleteAsync(cancellationToken);
            await db.History.Where(h => chunk.Contains(h.ItemId)).ExecuteDeleteAsync(cancellationToken);
        }
        return gone.Select(m => $"{m.Title}{(m.ReleaseYear is { } y ? $" ({y})" : "")}").ToList();
    }

    /// <summary>Meanings of films that are gone (from before removals cleaned them up).</summary>
    public async Task<int> DeleteOrphanedEmbeddingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        return await db.Embeddings.Where(e => e.Kind == "film" && !db.Movies.IgnoreQueryFilters().Any(m => m.ItemId == e.ItemId)).ExecuteDeleteAsync(cancellationToken);
    }
}
