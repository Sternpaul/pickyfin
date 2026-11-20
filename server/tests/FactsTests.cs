using System.Text.Json;
using Pickyfin.Server.Curation;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

public class FactsTests
{
    [Fact]
    public void ScoresNotesAndSettingAreReadAndCleaned()
    {
        var film = new MovieMetadata { Title = "The Trial" };
        using var doc = JsonDocument.Parse("""
            { "CriticsScore": 84, "AudienceScore": 87, "ScoreSource": "Rotten Tomatoes",
              "ContentNotes": ["torture", "Gore", "made-up note"], "BasedOn": "Novel",
              "SettingEra": "1960s", "SettingPlace": "unknown" }
            """);
        MovieClassifier.ApplyFacts(film, doc.RootElement);
        Assert.Equal(84, film.CriticsScore);
        Assert.Equal(87, film.AudienceScore);
        Assert.Equal(new[] { "torture", "gore" }, film.ContentNoteList);
        Assert.Equal("novel", film.BasedOn);
        Assert.Equal("1960s", film.SettingEra);
        Assert.Null(film.SettingPlace);
        // Real scores beat the AI's guess
        film.CriticalAcclaimScore = 5;
        Assert.Equal(8.55, film.Acclaim, 2);
    }

    [Fact]
    public void NoScoreInTheTextMeansNoScore()
    {
        var film = new MovieMetadata { Title = "Obscure" };
        using var doc = JsonDocument.Parse("""{ "CriticsScore": null, "AudienceScore": 0, "ScoreSource": "Rotten Tomatoes" }""");
        MovieClassifier.ApplyFacts(film, doc.RootElement);
        Assert.Null(film.CriticsScore);
        Assert.Null(film.AudienceScore);
        Assert.Null(film.ScoreSource);
    }

    [Fact]
    public void ThePromptAsksForTheNewFacts()
    {
        var prompt = Pickyfin.Server.Ai.ClassificationPrompt.Build(new List<MovieMetadata> { new() { Title = "X" } });
        foreach (var field in new[] { "CriticsScore", "AudienceScore", "ContentNotes", "BasedOn", "SettingEra", "SettingPlace", "\"paranoid\"", "\"Slow Cinema\"" })
            Assert.Contains(field, prompt);
    }
}

public class OmdbTests
{
    [Fact]
    public void OmdbAnswerIsRead()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            { "Title": "Heat", "Year": "1995", "Response": "True", "Metascore": "76", "imdbRating": "8.3",
              "Ratings": [ { "Source": "Internet Movie Database", "Value": "8.3/10" },
                           { "Source": "Rotten Tomatoes", "Value": "83%" },
                           { "Source": "Metacritic", "Value": "76/100" } ] }
            """);
        var s = Pickyfin.Server.Library.OmdbScores.Parse(doc.RootElement);
        Assert.True(s.Found);
        Assert.Equal(83, s.RottenTomatoes);
        Assert.Equal(76, s.Metacritic);
        Assert.Equal(8.3, s.Imdb);
        Assert.Equal(1995, s.Year);

        using var missing = System.Text.Json.JsonDocument.Parse("""{ "Response": "True", "Year": "2023–", "Metascore": "N/A", "imdbRating": "N/A", "Ratings": [] }""");
        var m = Pickyfin.Server.Library.OmdbScores.Parse(missing.RootElement);
        Assert.Null(m.RottenTomatoes); Assert.Null(m.Metacritic); Assert.Null(m.Imdb);

        using var limit = System.Text.Json.JsonDocument.Parse("""{ "Response": "False", "Error": "Request limit reached!" }""");
        Assert.Equal("Request limit reached!", Pickyfin.Server.Library.OmdbScores.Parse(limit.RootElement).Error);
    }

    [Fact]
    public void OmdbScoresComeFirst()
    {
        var film = new Pickyfin.Server.Data.Models.MovieMetadata { Title = "Heat", CriticsScore = 50, AudienceScore = 50, CriticalAcclaimScore = 5 };
        Assert.Equal(5.0, film.Acclaim, 2);
        film.RtScore = 83; film.ImdbRating = 8.3;
        Assert.Equal(8.3, film.Acclaim, 2);
    }
}
