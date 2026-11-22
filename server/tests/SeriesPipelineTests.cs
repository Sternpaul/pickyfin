using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;
using Pickyfin.Server.Library;
using Xunit;

/// <summary>Series are synced and go through the same steps as films, told apart where it matters.</summary>
public class SeriesPipelineTests
{
    private static JellyfinItem BreakingBad() => new()
    {
        Id = Guid.NewGuid(), Name = "Breaking Bad", ProductionYear = 2008, Overview = "A chemistry teacher…",
        Status = "Ended", EndDate = new DateTime(2013, 9, 29), ChildCount = 5, RecursiveItemCount = 62, RunTimeTicks = 47L * 600_000_000,
        Studios = new() { new JellyfinNamed { Name = "AMC" } }, Genres = new() { "Drama", "Crime" },
        ProviderIds = new() { ["Tmdb"] = "1396", ["Imdb"] = "tt0903747" },
        People = new() { new JellyfinPerson { Name = "Bryan Cranston", Type = "Actor" } },
    };

    private static MovieMetadata AsSeries(JellyfinItem item, DateTime? watched = null) =>
        LibrarySync.Apply(item, MovieMetadata.SeriesKind, new Dictionary<Guid, MovieMetadata>(), new Dictionary<Guid, List<Guid>>(), watched)!;

    [Fact]
    public void A_series_is_stored_with_its_run()
    {
        var show = AsSeries(BreakingBad(), new DateTime(2026, 11, 1));
        Assert.True(show.IsSeries);
        Assert.Equal((5, 62, 2013, "Ended", "AMC", 47), (show.Seasons, show.Episodes, show.EndYear, show.Status, show.Network, show.Minutes));
        Assert.Equal(1396, show.TmdbId);
        Assert.Equal(new DateTime(2026, 11, 1), show.LastWatched);
    }

    [Fact]
    public void A_series_keeps_its_creators_from_tmdb_and_unchanged_series_are_skipped()
    {
        var item = BreakingBad();
        var show = AsSeries(item);
        show.Director = "Vince Gilligan"; // from TMDB's created_by
        var known = new Dictionary<Guid, MovieMetadata> { [item.Id] = show };
        Assert.Null(LibrarySync.Apply(item, MovieMetadata.SeriesKind, known, new Dictionary<Guid, List<Guid>>(), null));

        item.RecursiveItemCount = 63; // a new episode
        var again = LibrarySync.Apply(item, MovieMetadata.SeriesKind, known, new Dictionary<Guid, List<Guid>>(), null)!;
        Assert.Equal(63, again.Episodes);
        Assert.Equal("Vince Gilligan", again.Director);
    }

    [Fact]
    public void Films_never_get_series_facts()
    {
        var item = BreakingBad();
        var film = LibrarySync.Apply(item, MovieMetadata.MovieKind, new Dictionary<Guid, MovieMetadata>(), new Dictionary<Guid, List<Guid>>(), null)!;
        Assert.False(film.IsSeries);
        Assert.Null(film.Seasons);
        Assert.Null(film.Network);
    }

    [Theory]
    [InlineData("Ended", 2013, "2008–2013")]
    [InlineData("Continuing", null, "2008–")]
    [InlineData("Ended", 2008, "2008")]
    public void The_run_is_written_as_years(string status, int? end, string expected) =>
        Assert.Equal(expected, ClassificationPrompt.Aired(new MovieMetadata { Kind = MovieMetadata.SeriesKind, ReleaseYear = 2008, EndYear = end, Status = status }));

    [Fact]
    public void The_prompt_says_which_entries_are_series()
    {
        var show = AsSeries(BreakingBad());
        show.Director = "Vince Gilligan";
        var film = new MovieMetadata { Title = "Heat", ReleaseYear = 1995, Director = "Michael Mann" };
        var json = JsonSerializer.Serialize(new[] { ClassificationPrompt.Facts(show, 0), ClassificationPrompt.Facts(film, 1) });
        using var doc = JsonDocument.Parse(json);
        var (s, f) = (doc.RootElement[0], doc.RootElement[1]);
        Assert.Equal("TV series", s.GetProperty("Type").GetString());
        Assert.Equal("2008–2013", s.GetProperty("Aired").GetString());
        Assert.Equal("Vince Gilligan", s.GetProperty("CreatedBy").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("Director").ValueKind);
        Assert.Equal(JsonValueKind.Null, f.GetProperty("Type").ValueKind);
        Assert.Equal("Michael Mann", f.GetProperty("Director").GetString());
        Assert.Equal(1995, f.GetProperty("Year").GetInt32());
        Assert.Contains("TV series", ClassificationPrompt.Build(new[] { show, film }));
    }

    [Fact]
    public void Format_is_read_for_series_only()
    {
        using var doc = JsonDocument.Parse("""{ "Format": "Miniseries" }""");
        var show = new MovieMetadata { Kind = MovieMetadata.SeriesKind };
        var film = new MovieMetadata();
        MovieClassifier.ApplyFacts(show, doc.RootElement);
        MovieClassifier.ApplyFacts(film, doc.RootElement);
        Assert.Equal("miniseries", show.Format);
        Assert.Null(film.Format);
    }

    [Fact]
    public void A_films_meaning_text_is_unchanged_and_a_series_says_what_it_is()
    {
        // Any change to a film's text would embed the whole library again
        var film = new MovieMetadata { Title = "Heat", ReleaseYear = 1995, Director = "Michael Mann" };
        Assert.StartsWith("Heat (1995). Directed by Michael Mann.", Meaning.Document(film));

        var show = AsSeries(BreakingBad());
        show.Director = "Vince Gilligan";
        show.Format = "ongoing";
        Assert.StartsWith("Breaking Bad. A TV series (2008–2013, 5 seasons, ongoing). On AMC. Created by Vince Gilligan.", Meaning.Document(show));
    }

    [Fact]
    public void Tv_genres_count_as_the_film_genres_they_cover()
    {
        var show = new MovieMetadata
        {
            Kind = MovieMetadata.SeriesKind, IsClassified = true,
            Genres = """["Sci-Fi & Fantasy", "Drama"]""", Subcategories = """["Space Opera"]""",
        };
        Assert.Empty(GenreCheck.Problems(show));
        Assert.Contains("Science Fiction", GenreCheck.KnownGenres(show));
    }

    [Fact]
    public void Tmdb_tv_details_give_the_creators()
    {
        using var doc = JsonDocument.Parse("""
            { "created_by": [{ "name": "Vince Gilligan" }], "recommendations": { "results": [{ "id": 60059 }] } }
            """);
        var details = TmdbDetails.Parse(doc.RootElement);
        Assert.Equal(new[] { "Vince Gilligan" }, details.CreatedBy);
        Assert.Equal(new[] { 60059 }, details.Recommendations);
        Assert.Null(details.CollectionId);
    }

    [Fact]
    public async Task Films_are_described_first_then_watched_series_newest_first()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-series-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var database = new Database(dir);
            await using (var db = database.Open())
            {
                db.Movies.AddRange(
                    new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Unwatched Show", Kind = MovieMetadata.SeriesKind },
                    new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Watched Long Ago", Kind = MovieMetadata.SeriesKind, LastWatched = new DateTime(2024, 1, 1) },
                    new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Zodiac" },
                    new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Watched Yesterday", Kind = MovieMetadata.SeriesKind, LastWatched = new DateTime(2026, 11, 21) });
                await db.SaveChangesAsync();
            }
            var pending = await new MovieStore(database).GetMoviesNeedingClassificationAsync(includeOutdated: true);
            Assert.Equal(new[] { "Zodiac", "Watched Yesterday", "Watched Long Ago", "Unwatched Show" }, pending.Select(m => m.Title));
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }
}
