using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

[Collection("ServerSettings")]
public class LetterboxdCollectorTests
{
    private const string Fellowship = """
        { "jellyfin_id": "0f1e2d3c4b5a69788796a5b4c3d2e1f0", "title": "The Fellowship", "year": 2001, "tmdb_id": "120", "imdb_id": "tt0120737",
          "status": "ok",
          "letterboxd": { "slug": "the-fellowship", "url": "https://letterboxd.com/film/the-fellowship/", "rating_avg": 4.4, "rating_count": 1500000, "review_count": 9,
            "histogram": { "0.5": 1, "1.0": 2, "1.5": 3, "2.0": 4, "2.5": 5, "3.0": 6, "3.5": 7, "4.0": 8, "4.5": 9, "5.0": 10 } },
          "reviews": [
            { "author": "spoiler", "rating": 1.0, "likes": 999, "spoiler": true, "text": "Gandalf comes back.", "url": null },
            { "author": "anna", "rating": 4.5, "liked": true, "likes": 12, "spoiler": false, "text": "A long walk that never feels long.", "url": "https://letterboxd.com/anna/film/the-fellowship/" } ] }
        """;

    [Fact]
    public void CollectorFilmIsRead()
    {
        using var doc = JsonDocument.Parse(Fellowship);
        var f = LetterboxdRatings.ParseFilm(doc.RootElement)!;
        Assert.Equal(Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"), f.JellyfinId);
        Assert.Equal(120, f.TmdbId);
        Assert.Equal(4.4, f.Rating);
        Assert.Equal(1500000, f.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, f.Histogram);
        // spoilers are left out
        Assert.Equal("anna", Assert.Single(f.Reviews).Author);

        using var empty = JsonDocument.Parse("""{ "jellyfin_id": "x", "status": "new", "letterboxd": { "rating_avg": null, "histogram": null } }""");
        Assert.Null(LetterboxdRatings.ParseFilm(empty.RootElement)!.Rating);
    }

    [Fact]
    public void LetterboxdCountsMostInTheRealRating()
    {
        var film = new MovieMetadata { Title = "X", ImdbRating = 6.0, RtScore = 60 };
        Assert.Equal(6.0, film.RealRating);
        film.LbRating = 4.5; film.LbCount = 50000; // 9 on the 10 scale, weight 2
        Assert.Equal((6 + 6 + 9 * 2) / 4.0, film.RealRating!.Value, 3);
        film.LbCount = 20; // few ratings: weight 0.5
        Assert.Equal((6 + 6 + 9 * 0.5) / 2.5, film.RealRating!.Value, 3);
        Assert.Equal(9.0, new MovieMetadata { Title = "Y", LbRating = 4.5, LbCount = 5 }.RealRating);
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.PathAndQuery + " " + request.Headers.Authorization);
            return Task.FromResult(answer(request));
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task FilmsAreMatchedByJellyfinIdElseTmdb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-lb-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = ServerSettings.Current;
        var (url, token, on) = (s.LetterboxdUrl, s.LetterboxdToken, s.LetterboxdOn);
        try
        {
            var database = new Database(dir);
            var sameServer = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
            var other = Guid.NewGuid();
            var unknown = Guid.NewGuid();
            await using (var db = database.Open())
            {
                db.Movies.Add(new MovieMetadata { ItemId = sameServer, Title = "The Fellowship", TmdbId = 999 });
                db.Movies.Add(new MovieMetadata { ItemId = other, Title = "The Two Towers", TmdbId = 121 });
                db.Movies.Add(new MovieMetadata { ItemId = unknown, Title = "Willow", TmdbId = 900 });
                await db.SaveChangesAsync();
            }
            var towers = Fellowship.Replace("0f1e2d3c4b5a69788796a5b4c3d2e1f0", "ffffffffffffffffffffffffffffffff").Replace("\"120\"", "\"121\"").Replace("4.4", "4.1");
            var handler = new Fake(r => r.RequestUri!.AbsolutePath switch
            {
                "/health" => Json("""{ "ok": true, "schema_version": 2 }"""),
                "/status" => r.Headers.Authorization?.Parameter == "secret" ? Json("""{ "library": { "ok": 2 }, "movies_with_rating": 2 }""") : Json("{}", HttpStatusCode.Unauthorized),
                "/movies" => Json($$"""{ "total": 2, "limit": 500, "offset": 0, "items": [ {{Fellowship}}, {{towers}} ] }"""),
                _ => Json("{}", HttpStatusCode.NotFound),
            });
            s.LetterboxdOn = true; s.LetterboxdUrl = "http://collector:8098"; s.LetterboxdToken = "secret";
            LetterboxdRatings.ResetPause();
            await new LetterboxdRatings(new Factory(handler), database, NullLogger<LetterboxdRatings>.Instance).RunAsync(default);

            await using var check = database.Open();
            var films = check.Movies.ToDictionary(m => m.ItemId);
            Assert.Equal(4.4, films[sameServer].LbRating);
            Assert.Equal(4.1, films[other].LbRating);
            Assert.Null(films[unknown].LbRating);
            Assert.Equal("anna", Assert.Single(films[sameServer].LbReviewList).Author);
            Assert.Contains(handler.Asked, a => a.StartsWith("/movies?status=ok,partial") && a.EndsWith("Bearer secret"));
            Assert.Equal(2, LetterboxdRatings.Last!.Matched);
        }
        finally
        {
            (s.LetterboxdUrl, s.LetterboxdToken, s.LetterboxdOn) = (url, token, on);
            LetterboxdRatings.ResetPause();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
