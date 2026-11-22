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

public class MeaningTests
{
    [Fact]
    public void EmbeddingAnswerIsRead()
    {
        var v = Meaning.Parse("""{ "data": [ { "index": 1, "embedding": [0, 1] }, { "index": 0, "embedding": [1, 0] } ] }""", 2);
        Assert.NotNull(v);
        Assert.Equal(1f, v![0][0]);
        Assert.Equal(1f, v[1][1]);
        Assert.Null(Meaning.Parse("""{ "data": [ { "index": 0, "embedding": [1, 0] } ] }""", 2));
        Assert.Null(Meaning.Parse("""{ "error": { "message": "nope" } }""", 1));
    }

    [Fact]
    public void ClosestByMeaningRanksHighest()
    {
        var films = new Dictionary<Guid, float[]>();
        var seed = Guid.NewGuid(); var twin = Guid.NewGuid(); var far = Guid.NewGuid();
        films[seed] = Meaning.Normalize(new[] { 1f, 0f });
        films[twin] = Meaning.Normalize(new[] { 0.95f, 0.05f });
        films[far] = Meaning.Normalize(new[] { -1f, 0.1f });
        var rnd = new Random(1);
        for (var i = 0; i < 30; i++) films[Guid.NewGuid()] = Meaning.Normalize(new[] { (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() });
        var user = Guid.NewGuid();
        var snap = new Meaning.Snapshot("m", 1, films, new() { [user] = Meaning.Normalize(new[] { 1f, 0f }) });
        Assert.Equal(1.0, snap.Score(seed, twin)!.Value, 2);  // the closest
        Assert.Equal(0.0, snap.Score(seed, far)!.Value, 2);   // the farthest
        Assert.True(snap.BriefScore(user, twin) > 0.75);       // second closest to what they want
        Assert.Equal(0.5 * 0.3 + 0.5 * 0.7, Meaning.Blend(0.3, 0.7, 0.5), 3);
        Assert.Equal(0.3, Meaning.Blend(0.3, null, 0.5), 3);
    }

    [Fact]
    public void ModelsAreAveragedOnlyWhereTheyKnowBothFilms()
    {
        var rnd = new Random(2);
        var seed = Guid.NewGuid(); var twin = Guid.NewGuid(); var onlyA = Guid.NewGuid();
        Dictionary<Guid, float[]> Films(float[] twinVector)
        {
            var films = new Dictionary<Guid, float[]> { [seed] = Meaning.Normalize(new[] { 1f, 0f }), [twin] = Meaning.Normalize(twinVector) };
            for (var i = 0; i < 30; i++) films[Guid.NewGuid()] = Meaning.Normalize(new[] { (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() });
            return films;
        }
        var a = Films(new[] { 0.95f, 0.05f });
        a[onlyA] = Meaning.Normalize(new[] { 0.9f, 0.1f });
        var b = Films(new[] { -1f, 0.1f }); // model B thinks they're far apart
        var snap = new Meaning.Snapshot(1, new List<Meaning.Part> { new("a", a, new()), new("b", b, new()) });
        var bothSay = snap.ScoreBy(seed, twin);
        Assert.Equal(2, bothSay.Count);
        Assert.Equal((bothSay["a"] + bothSay["b"]) / 2, snap.Score(seed, twin)!.Value, 6);
        // only model A has this film: its score alone counts
        Assert.Equal(snap.Parts[0].Model == "a" ? snap.Parts[0].Rank(seed, onlyA, false) : snap.Parts[1].Rank(seed, onlyA, false), snap.Score(seed, onlyA));
        Assert.True(snap.Has(onlyA));
    }

    [Fact]
    public void DocumentCarriesTheWholeDescription()
    {
        var m = new Pickyfin.Server.Data.Models.MovieMetadata { Title = "Heat", ReleaseYear = 1995, Plot = "A heist.", Director = "Michael Mann", WebNotes = "Critics loved it." };
        var doc = Meaning.Document(m);
        Assert.Contains("Heat (1995)", doc);
        Assert.Contains("Michael Mann", doc);
        Assert.Contains("Critics loved it.", doc);
    }

    [Fact]
    public void MeaningIsSpreadOutAtTheTop()
    {
        // In a library of 2,000: the closest 1, the 10th about 0.7, the 100th about 0.4, the farthest 0
        Assert.Equal(1.0, Meaning.Spread(0, 2000), 3);
        Assert.Equal(0.70, Meaning.Spread(9, 2000), 2);
        Assert.Equal(0.39, Meaning.Spread(99, 2000), 2);
        Assert.Equal(0.0, Meaning.Spread(1999, 2000), 3);
        // the 2nd and the 30th closest are told apart (the old score gave both nearly the top)
        Assert.True(Meaning.Spread(1, 2000) - Meaning.Spread(29, 2000) > 0.3);
    }

    [Fact]
    public void AnUntouchedMeaningWeightMovesToTheNewDefaultOnce()
    {
        var old = new Pickyfin.Server.ServerSettings { MeaningWeight = 50, ScoringVersion = 0 };
        Assert.True(Pickyfin.Server.ServerSettings.Upgrade(old));
        Assert.Equal(70, old.MeaningWeight);
        Assert.False(Pickyfin.Server.ServerSettings.Upgrade(old)); // once
        old.MeaningWeight = 50;                                    // chosen again later: stays
        Assert.False(Pickyfin.Server.ServerSettings.Upgrade(old));
        Assert.Equal(50, old.MeaningWeight);

        var chosen = new Pickyfin.Server.ServerSettings { MeaningWeight = 30, ScoringVersion = 0 };
        Pickyfin.Server.ServerSettings.Upgrade(chosen);
        Assert.Equal(30, chosen.MeaningWeight);
        Assert.Equal(70, new Pickyfin.Server.ServerSettings().MeaningWeight);
    }
}
