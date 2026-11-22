using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Letterboxd;
using Pickyfin.Server.Library;
using Xunit;

/// <summary>Series sit in the same table as movies, but nothing that works on films may see them yet.</summary>
public class SeriesGroundworkTests
{
    private static async Task WithDatabase(Func<Database, Task> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-series-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try { await test(new Database(dir)); }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }

    private static MovieMetadata Series(Guid id, string title, int? tmdb = null) =>
        new() { ItemId = id, Title = title, Kind = MovieMetadata.SeriesKind, TmdbId = tmdb };

    [Fact]
    public Task A_movie_sync_never_hides_the_series() => WithDatabase(async database =>
    {
        var store = new MovieStore(database);
        var movie = Guid.NewGuid(); var show = Guid.NewGuid();
        await using (var db = database.Open())
        {
            db.Movies.AddRange(new MovieMetadata { ItemId = movie, Title = "Heat" }, Series(show, "Breaking Bad"));
            await db.SaveChangesAsync();
        }

        // Jellyfin's movie list only has movies: the series must stay
        Assert.Equal((0, 0), await store.MarkGoneAsync(new HashSet<Guid> { movie }, MovieMetadata.MovieKind));
        Assert.Single(await store.GetAllSeriesAsync());

        // Each kind is split cleanly
        Assert.Equal(new[] { movie }, (await store.GetAllMoviesAsync()).Select(m => m.ItemId));
        Assert.Equal(new[] { show }, (await store.GetAllSeriesAsync()).Select(m => m.ItemId));
        Assert.Equal(2, (await store.GetAllTitlesAsync()).Count);

        // A series Jellyfin drops is hidden by the series list, not the movie one
        Assert.Equal((1, 0), await store.MarkGoneAsync(new HashSet<Guid>(), MovieMetadata.SeriesKind));
        Assert.Empty(await store.GetAllSeriesAsync());
        Assert.Single(await store.GetAllMoviesAsync());
    });

    [Fact]
    public Task Series_are_not_described_yet() => WithDatabase(async database =>
    {
        var store = new MovieStore(database);
        await using (var db = database.Open())
        {
            db.Movies.AddRange(new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Heat" }, Series(Guid.NewGuid(), "Breaking Bad"));
            await db.SaveChangesAsync();
        }
        var pending = await store.GetMoviesNeedingClassificationAsync(includeOutdated: true);
        Assert.Equal(new[] { "Heat" }, pending.Select(m => m.Title));
        var (total, _, _, _, _) = await store.GetStatsAsync();
        Assert.Equal(1, total);
    });

    [Fact]
    public Task Letterboxd_films_never_match_a_series_with_the_same_tmdb_id() => WithDatabase(async database =>
    {
        var store = new MovieStore(database);
        var user = Guid.NewGuid(); var show = Guid.NewGuid();
        await using (var db = database.Open())
        {
            // TMDB TV 1396 is Breaking Bad; TMDB movie 1396 is an unrelated film
            db.Movies.Add(Series(show, "Breaking Bad", tmdb: 1396));
            db.Letterboxd.Add(new LetterboxdFilm { UserId = user, Kind = "watched", Source = "feed", Title = "Some Film", Year = 1990, TmdbId = 1396 });
            await db.SaveChangesAsync();
        }
        var sync = new LetterboxdSync(new HttpClient(), database, store, NullLogger<LetterboxdSync>.Instance);
        var (_, _, matched) = await sync.MatchAsync(user, CancellationToken.None);
        Assert.Equal(0, matched);
    });

    [Fact]
    public void Tmdb_recommendations_only_link_titles_of_the_same_kind()
    {
        var film = new MovieMetadata { TmdbId = 100, TmdbRecs = "[1396]" };
        var show = Series(Guid.NewGuid(), "Breaking Bad", tmdb: 1396);
        var otherFilm = new MovieMetadata { TmdbId = 1396 };
        Assert.False(SimilarityEngine.CrowdLinked(film, show));
        Assert.True(SimilarityEngine.CrowdLinked(film, otherFilm));
    }

    [Fact]
    public async Task An_older_database_gets_the_kind_column_with_every_row_a_movie()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-series-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var id = Guid.NewGuid();
            var database = new Database(dir);
            await using (var db = database.Open())
            {
                // Make it look like a database from before series
                foreach (var column in new[] { "Kind", "Seasons", "Episodes", "EndYear", "Status", "Network", "Format" })
                    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Movies\" DROP COLUMN \"" + column + "\"");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Movies\" (\"ItemId\", \"Title\", \"Popularity\", \"Checked\", \"Verified\", \"RedoDescribe\", \"OscarWins\", \"OscarNominations\", \"CriticalAcclaimScore\", \"IsClassified\", \"VocabularyVersion\", \"ManuallyEdited\", \"DateAdded\", \"LastUpdated\") "
                    + "VALUES ({0}, 'Heat', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, '2026-01-01', '2026-01-01')", id.ToString().ToUpperInvariant());
            }
            SqliteConnection.ClearAllPools();

            var upgraded = new MovieStore(new Database(dir));
            var movie = Assert.Single(await upgraded.GetAllMoviesAsync());
            Assert.Equal(MovieMetadata.MovieKind, movie.Kind);
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }
}
