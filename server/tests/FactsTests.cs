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
