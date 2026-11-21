using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;

namespace Pickyfin.Server.Data;

/// <summary>The movie table, with the methods the classifier and TMDB enrichment use.</summary>
public sealed class MovieStore
{
    private readonly Database _database;

    public MovieStore(Database database) => _database = database;

    public async Task<List<MovieMetadata>> GetAllMoviesAsync(CancellationToken cancellationToken = default)
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
        // New films first, then the ones queued to be described again
        pending = pending.OrderBy(m => m.IsClassified).ThenBy(m => m.Title).ToList();
        // Movies only in switched-off libraries are never recommended, so don't pay to classify them
        return pending.Where(m => !ServerSettings.Current.IsLibraryExcluded(m.Libraries)).ToList();
    }

    /// <summary>Films queued to be described again.</summary>
    public async Task<int> CountRedoAsync(CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        return await db.Movies.CountAsync(m => m.RedoDescribe && !m.ManuallyEdited, cancellationToken);
    }

    public async Task<(int Total, int Classified, int Issues, int Reviewed, int LookedUp)> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        // Only movies that can be recommended (not in switched-off libraries)
        await using var db = _database.Open();
        var rows = await db.Movies.AsNoTracking().Select(m => new { m.Libraries, m.IsClassified, HasIssue = m.Issue != null, Reviewed = m.Checked || m.ManuallyEdited || (m.WebNotes != null && m.WebNotes != ""), LookedUp = m.WebNotes != null }).ToListAsync(cancellationToken);
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
    /// Films Jellyfin doesn't list (any more) are hidden; ones it lists again come back.
    /// Returns how many were newly hidden and how many came back.
    /// </summary>
    public async Task<(int Hidden, int Back)> MarkGoneAsync(HashSet<Guid> liveIds, CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var all = await db.Movies.IgnoreQueryFilters().Select(m => new { m.ItemId, m.GoneSince }).ToListAsync(cancellationToken);
        var hide = all.Where(m => m.GoneSince is null && !liveIds.Contains(m.ItemId)).Select(m => m.ItemId).ToList();
        var back = all.Where(m => m.GoneSince is not null && liveIds.Contains(m.ItemId)).Select(m => m.ItemId).ToList();
        var now = DateTime.UtcNow;
        foreach (var chunk in hide.Chunk(500))
            await db.Movies.IgnoreQueryFilters().Where(m => chunk.Contains(m.ItemId)).ExecuteUpdateAsync(u => u.SetProperty(m => m.GoneSince, now), cancellationToken);
        foreach (var chunk in back.Chunk(500))
            await db.Movies.IgnoreQueryFilters().Where(m => chunk.Contains(m.ItemId)).ExecuteUpdateAsync(u => u.SetProperty(m => m.GoneSince, (DateTime?)null), cancellationToken);
        return (hide.Count, back.Count);
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
