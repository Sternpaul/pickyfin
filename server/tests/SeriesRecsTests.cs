using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Curation;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;
using Pickyfin.Server.Library;
using Xunit;

/// <summary>Series-only tags, and series recommended like films (rows kept here, not in Jellyfin).</summary>
public class SeriesRecsTests
{
    private static MovieMetadata Series(string title, string? kinds = null, string subs = """["Crime Drama"]""") => new()
    {
        ItemId = Guid.NewGuid(), Title = title, Kind = MovieMetadata.SeriesKind, IsClassified = true, ReleaseYear = 2015,
        Subcategories = subs, Moods = """["tense","dark"]""", SeriesKinds = kinds, Episodes = 20,
    };

    // ---- series-only tags

    [Fact]
    public void Kinds_of_tv_are_read_for_series_only_and_format_is_how_it_is_built()
    {
        using var doc = JsonDocument.Parse("""{ "SeriesKinds": ["police procedural", "Made Up", "Sitcom", "Teen Drama", "Dramedy"], "Format": "ongoing" }""");
        var show = new MovieMetadata { Kind = MovieMetadata.SeriesKind };
        var film = new MovieMetadata();
        MovieClassifier.ApplyFacts(show, doc.RootElement);
        MovieClassifier.ApplyFacts(film, doc.RootElement);
        Assert.Equal(new[] { "Police Procedural", "Sitcom", "Teen Drama" }, show.SeriesKindSet.Items);
        Assert.Equal("ongoing", show.Format);
        Assert.Null(film.SeriesKinds);
        Assert.Null(ClassificationVocabulary.CanonicalFormat("sitcom")); // a kind of TV now, not a format
    }

    [Fact]
    public void Kinds_of_tv_only_count_between_two_series()
    {
        var engine = new SimilarityEngine();
        var a = Series("A", """["Sitcom"]""");
        var b = Series("B", """["Sitcom"]""");
        var c = Series("C", """["Police Procedural"]""");
        Assert.Equal(0.15, engine.CalculateSimilarity(a, b) - engine.CalculateSimilarity(a, c), 3);

        // A film compares the same with both: kinds of TV never count against a film
        var film = new MovieMetadata { ItemId = Guid.NewGuid(), IsClassified = true, ReleaseYear = 2015, Subcategories = """["Crime Drama"]""", Moods = """["tense","dark"]""" };
        Assert.Equal(engine.CalculateSimilarity(film, b), engine.CalculateSimilarity(film, c), 6);
    }

    [Fact]
    public void The_description_can_ask_for_or_rule_out_kinds_of_tv()
    {
        var brief = new BriefProfile { Subcategories = { "Crime Drama" }, SeriesKinds = new() { "Police Procedural" }, AvoidSeriesKinds = new() { "Sitcom" }, MaxMinutes = 30 };
        var procedural = Series("P", """["Police Procedural"]""");
        var sitcom = Series("S", """["Sitcom"]""");
        var other = Series("O", """["Prestige Drama"]""");
        procedural.Minutes = other.Minutes = 55; // one episode: the film running-time limit doesn't apply
        Assert.False(Curator.IsAvoided(procedural, brief));
        Assert.True(Curator.IsAvoided(sitcom, brief));
        Assert.True(Curator.BriefScore(procedural, brief) > Curator.BriefScore(other, brief));
    }

    [Fact]
    public void Meaning_text_of_a_series_names_its_kinds()
    {
        var show = Series("Line of Duty", """["Police Procedural"]""");
        Assert.Contains("TV: Police Procedural.", Meaning.Document(show));
    }

    [Theory]
    [InlineData(3, 0, "3 films")]
    [InlineData(1, 0, "1 film")]
    [InlineData(0, 4, "4 series")]
    [InlineData(1200, 1, "1,200 films and 1 series")]
    public void Logs_count_films_and_series(int films, int series, string text) =>
        Assert.Equal(text, Titles.Count(films, series));

    // ---- rows

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static async Task WithDatabase(Func<Database, Task> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-recs-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = ServerSettings.Current;
        var before = (s.JellyfinUrl, s.SeriesRecommendations, s.ApiKey);
        s.JellyfinUrl = "http://jellyfin.test";
        try { await test(new Database(dir)); }
        finally
        {
            (s.JellyfinUrl, s.SeriesRecommendations, s.ApiKey) = before;
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public Task A_series_row_is_kept_without_making_a_playlist() => WithDatabase(async database =>
    {
        var jellyfin = new Fake(_ => throw new InvalidOperationException("Jellyfin must not be asked"));
        var writer = new PlaylistWriter(new JellyfinClient(new HttpClient(jellyfin)), database, NullLogger<PlaylistWriter>.Instance);
        var user = Guid.NewGuid();
        var ids = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        await writer.WriteAsync(user, new[] { new PickRow("series-recommended", Curator.SeriesRecommendedName, ids) }, CancellationToken.None);
        await using (var db = database.Open())
        {
            var row = Assert.Single(db.Playlists.Where(p => p.UserId == user));
            Assert.Equal(Guid.Empty, row.PlaylistId);
            Assert.Equal(Ids.Join(ids), row.Items);
        }
        // Taking it away again doesn't ask Jellyfin either
        await writer.RemoveAllAsync(user, CancellationToken.None);
        Assert.Empty(jellyfin.Asked);
    });

    [Fact]
    public Task Series_rows_hold_series_nobody_started_and_films_stay_films() => WithDatabase(async database =>
    {
        var s = ServerSettings.Current;
        s.SeriesRecommendations = true;
        s.ApiKey = ""; // no AI: the shortlist order fills the rows

        var films = Enumerable.Range(0, 8).Select(i => new MovieMetadata { ItemId = Guid.NewGuid(), Title = $"Film {i}", IsClassified = true, ReleaseYear = 2010 + i, Subcategories = """["Crime Drama"]""", Moods = """["tense"]""" }).ToList();
        var watchedShow = Series("Watched Show", """["Police Procedural"]""");
        var triedShow = Series("Tried Show");     // one episode: started, so not recommended
        var halfShow = Series("Half Show");       // Jellyfin says partly played
        var fresh = Enumerable.Range(0, 30).Select(i => Series($"Fresh {i}", """["Police Procedural"]""")).ToList();
        var allSeries = new[] { watchedShow, triedShow, halfShow }.Concat(fresh).ToList();
        await using (var db = database.Open())
        {
            db.Movies.AddRange(films);
            db.Movies.AddRange(allSeries);
            await db.SaveChangesAsync();
        }

        object Item(MovieMetadata m, object? userData = null) => new { Id = m.ItemId, Name = m.Title, UserData = userData };
        var episodes = Enumerable.Range(0, 5).Select(i => new { Id = Guid.NewGuid(), Name = $"E{i}", SeriesId = watchedShow.ItemId, ParentIndexNumber = 1, UserData = new { Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-i) } })
            .Append(new { Id = Guid.NewGuid(), Name = "Pilot", SeriesId = triedShow.ItemId, ParentIndexNumber = 1, UserData = new { Played = true, LastPlayedDate = DateTime.UtcNow } })
            .ToList();
        var jellyfin = new Fake(request =>
        {
            var q = request.RequestUri!.Query;
            if (q.Contains("IncludeItemTypes=Movie")) return Json(new { Items = films.Select((f, i) => Item(f, new { Played = i == 0, LastPlayedDate = i == 0 ? DateTime.UtcNow : (DateTime?)null })) });
            if (q.Contains("IncludeItemTypes=Series")) return Json(new { Items = allSeries.Select(m => Item(m, new { Played = false, PlayedPercentage = m == halfShow ? 10.0 : (double?)null })) });
            if (q.Contains("IncludeItemTypes=Episode")) return Json(new { Items = episodes });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new JellyfinClient(new HttpClient(jellyfin));
        var curator = new Curator(client, new MovieStore(database), new AIProviderFactory(new NoHttp(), NullLoggerFactory.Instance), database,
            new Meaning(new NoHttp(), database, NullLogger<Meaning>.Instance), NullLogger<Curator>.Instance);
        var user = new UserProfile { UserId = Guid.NewGuid(), Name = "Test", Enabled = true };

        var result = await curator.CurateAsync(user, null, CancellationToken.None);

        var filmRow = result.Rows.Single(r => r.Slot == "recommended");
        Assert.All(filmRow.Items, id => Assert.Contains(films, f => f.ItemId == id));
        Assert.DoesNotContain(films[0].ItemId, filmRow.Items); // watched

        var seriesRow = result.Rows.Single(r => r.Slot == "series-recommended");
        Assert.Equal(Curator.SeriesRecommendedName, seriesRow.Name);
        Assert.All(seriesRow.Items, id => Assert.Contains(fresh, f => f.ItemId == id));

        var because = result.Rows.Single(r => r.Slot == "series-because-1");
        Assert.Equal(Curator.BecausePrefix + "Watched Show", because.Name);
        Assert.All(because.Items, id => Assert.Contains(fresh, f => f.ItemId == id));
        Assert.Contains("Series:", result.Summary);

        // Switched off: only the film rows
        s.SeriesRecommendations = false;
        var filmsOnly = await curator.CurateAsync(user, null, CancellationToken.None);
        Assert.DoesNotContain(filmsOnly.Rows, r => Curator.IsSeriesSlot(r.Slot));
        Assert.Contains("series rows off", filmsOnly.Summary);
    });

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no web in this test");
    }
}
