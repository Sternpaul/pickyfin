using System.Text.Json;
using Pickyfin.Server.Curation;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

public class MatrixTests
{
    [Fact]
    public void OscarsAreRead()
    {
        Assert.Equal((3, 3), OmdbScores.Oscars("Won 3 Oscars. 45 wins & 112 nominations total"));
        Assert.Equal((1, 1), OmdbScores.Oscars("Won 1 Oscar. 10 wins"));
        Assert.Equal((0, 2), OmdbScores.Oscars("Nominated for 2 Oscars. 5 wins & 20 nominations total"));
        Assert.Equal((0, 0), OmdbScores.Oscars("3 wins & 4 nominations"));
    }

    [Fact]
    public void TmdbSeriesAndRecommendationsAreRead()
    {
        using var doc = JsonDocument.Parse("""
            { "id": 120, "runtime": 178, "belongs_to_collection": { "id": 119, "name": "The Lord of the Rings Collection" },
              "recommendations": { "results": [ { "id": 121 }, { "id": 122 }, { "id": 121 } ] } }
            """);
        var d = TmdbDetails.Parse(doc.RootElement);
        Assert.Equal(119, d.CollectionId);
        Assert.Equal("The Lord of the Rings Collection", d.CollectionName);
        Assert.Equal(new[] { 121, 122 }, d.Recommendations);

        using var none = JsonDocument.Parse("""{ "id": 5, "belongs_to_collection": null }""");
        Assert.Null(TmdbDetails.Parse(none.RootElement).CollectionId);
    }

    [Fact]
    public void CrowdLinkWorksBothWays()
    {
        var a = new MovieMetadata { Title = "A", TmdbId = 1, TmdbRecs = "[2,3]" };
        var b = new MovieMetadata { Title = "B", TmdbId = 2 };
        var c = new MovieMetadata { Title = "C", TmdbId = 9 };
        Assert.True(SimilarityEngine.CrowdLinked(a, b));
        Assert.True(SimilarityEngine.CrowdLinked(b, a));
        Assert.False(SimilarityEngine.CrowdLinked(a, c));
    }

    [Fact]
    public void NextPartOfTheSeriesComesFirst()
    {
        var seed = new MovieMetadata { Title = "Part 1", ReleaseYear = 2001, CollectionId = 7, CollectionName = "Saga" };
        var part2 = new MovieMetadata { Title = "Part 2", ReleaseYear = 2002, CollectionId = 7 };
        var other = new MovieMetadata { Title = "Close match", ReleaseYear = 2001 };
        var picked = BecauseRow.Pick(seed, new[] { (other, 0.6), (part2, 0.05) }, new(), 5);
        Assert.Equal("Part 2", picked[0].Movie.Title);
        Assert.StartsWith("the series", picked[0].Reason);
    }

    [Fact]
    public void BriefLimitsRunningTimeAndEndings()
    {
        var brief = new BriefProfile { MaxMinutes = 120, AvoidEndings = new() { "bleak" }, Awarded = true, Pacing = new() { "fast" } };
        var winner = new MovieMetadata { Title = "W", OscarWins = 2, Pacing = "fast" };
        var plain = new MovieMetadata { Title = "P" };
        Assert.True(Curator.BriefScore(winner, brief) > Curator.BriefScore(plain, brief));
    }

    [Fact]
    public void MinRatingUsesRealScoresOnly()
    {
        var film = new MovieMetadata { Title = "X", CriticalAcclaimScore = 9 };
        Assert.Null(film.RealRating);
        film.RtScore = 40; film.ImdbRating = 5.0;
        Assert.Equal(4.5, film.RealRating!.Value, 2);
        Assert.Equal(4.5, film.Acclaim, 2);
    }
}
