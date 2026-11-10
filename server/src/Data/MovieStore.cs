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

    public async Task<List<MovieMetadata>> GetMoviesNeedingClassificationAsync(bool includeOutdated, CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var pending = await db.Movies.AsNoTracking()
            .Where(m => !m.ManuallyEdited && (!m.IsClassified || (includeOutdated && m.VocabularyVersion < ClassificationVocabulary.Version)))
            .ToListAsync(cancellationToken);
        // Movies only in switched-off libraries are never recommended, so don't pay to classify them
        return pending.Where(m => !ServerSettings.Current.IsLibraryExcluded(m.Libraries)).ToList();
    }

    public async Task<(int Total, int Classified, int Issues)> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        // Only movies that can be recommended (not in switched-off libraries)
        await using var db = _database.Open();
        var rows = await db.Movies.AsNoTracking().Select(m => new { m.Libraries, m.IsClassified, HasIssue = m.Issue != null }).ToListAsync(cancellationToken);
        var counted = rows.Where(r => !ServerSettings.Current.IsLibraryExcluded(r.Libraries)).ToList();
        return (counted.Count, counted.Count(r => r.IsClassified), counted.Count(r => r.HasIssue));
    }

    /// <summary>Inserts or replaces whole rows.</summary>
    public async Task SaveMoviesAsync(IEnumerable<MovieMetadata> movies, CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var list = movies.ToList();
        var ids = list.Select(m => m.ItemId).ToList();
        var rows = await db.Movies.AsNoTracking().Where(m => ids.Contains(m.ItemId)).Select(m => new { m.ItemId, m.ManuallyEdited }).ToListAsync(cancellationToken);
        var existing = rows.Select(r => r.ItemId).ToHashSet();
        // A background job holding an older copy must not overwrite a hand edit made meanwhile
        var handEdited = rows.Where(r => r.ManuallyEdited).Select(r => r.ItemId).ToHashSet();
        foreach (var movie in list)
        {
            if (handEdited.Contains(movie.ItemId) && !movie.ManuallyEdited) continue;
            movie.LastUpdated = DateTime.UtcNow;
            if (existing.Contains(movie.ItemId)) db.Movies.Update(movie);
            else db.Movies.Add(movie);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeleteMoviesNotInAsync(HashSet<Guid> liveIds, CancellationToken cancellationToken = default)
    {
        await using var db = _database.Open();
        var gone = await db.Movies.Where(m => !liveIds.Contains(m.ItemId)).ToListAsync(cancellationToken);
        db.Movies.RemoveRange(gone);
        await db.SaveChangesAsync(cancellationToken);
        return gone.Count;
    }
}
