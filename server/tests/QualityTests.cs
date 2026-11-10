using System.Text.Json;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

public class QualityTests
{
    private static MovieMetadata M(string title, string[] genres, string[] subs, string[]? moods = null) => new()
    {
        ItemId = Guid.NewGuid(), Title = title, IsClassified = subs.Length > 0,
        Genres = JsonSerializer.Serialize(genres), Subcategories = JsonSerializer.Serialize(subs),
        Moods = JsonSerializer.Serialize(moods ?? Array.Empty<string>()),
    };

    [Fact]
    public void GenreCheckFlagsOnlyClearMismatches()
    {
        Assert.Empty(GenreCheck.Problems(M("Heat", new[] { "Action", "Crime", "Drama" }, new[] { "Heist", "Crime Drama" })));
        Assert.Single(GenreCheck.Problems(M("Heat", new[] { "Action", "Crime", "Drama" }, new[] { "Slasher", "Heist" })));
        // German genre names count too
        Assert.Empty(GenreCheck.Problems(M("Ghostbusters", new[] { "Komödie", "Fantasy" }, new[] { "Horror Comedy" })));
        // No genres known: nothing to judge
        Assert.Empty(GenreCheck.Problems(M("Unknown", Array.Empty<string>(), new[] { "Slasher" })));
        Assert.Contains(GenreCheck.Problems(M("Doc", new[] { "Documentary" }, new[] { "Biopic" })), p => p.Contains("documentary"));
    }

    [Fact]
    public void UnknownFilmIsAnIssueUntilLookedUp()
    {
        var movie = M("Obscure", new[] { "Drama" }, new[] { "Character Study" });
        Assert.Equal(Issues.Unknown, Issues.Describe(movie, "no"));
        Assert.Null(Issues.Describe(movie, "yes"));
        movie.WebNotes = "A Greek drama about...";
        Assert.Null(Issues.Describe(movie, "no"));
    }

    [Fact]
    public void AnswersAreMatchedByRefNeverByPosition()
    {
        var batch = new List<MovieMetadata> { M("A", new string[0], new string[0]), M("B", new string[0], new string[0]) };
        JsonElement E(string json) => JsonDocument.Parse(json).RootElement;
        Assert.Same(batch[1], MovieClassifier.Match(E("{\"Ref\":\"m2\"}"), batch));
        Assert.Same(batch[0], MovieClassifier.Match(E("{\"Ref\":1}"), batch));
        Assert.Null(MovieClassifier.Match(E("{\"Subcategories\":[]}"), batch));
        Assert.Null(MovieClassifier.Match(E("{\"Ref\":\"m9\"}"), batch));
        Assert.Same(batch[1], MovieClassifier.Match(E("{\"Title\":\"b\"}"), batch));
    }

    [Fact]
    public void SecondOpinionKeepsOnlyTagsTheMovieHas()
    {
        var batch = new List<MovieMetadata>
        {
            M("Heat", new[] { "Crime" }, new[] { "Heist", "Slasher" }, new[] { "tense" }),
            M("Amélie", new[] { "Comedy" }, new[] { "Romantic Comedy" }, new[] { "whimsical" }),
        };
        var answer = "```json\n{\"problems\":[{\"Ref\":\"m1\",\"Wrong\":[\"Slasher\"],\"Better\":[\"crime drama\"],\"Why\":\"No horror.\"},{\"Ref\":\"m2\",\"Wrong\":[\"Western\"]}]}\n```";
        var result = SecondOpinion.Parse(answer, batch)!;
        Assert.Single(result);
        Assert.Contains("\"Slasher\" doesn't fit; better: Crime Drama (No horror)", result[batch[0]]);
        Assert.Null(SecondOpinion.Parse("not json", batch));
        Assert.Empty(SecondOpinion.Parse("{\"problems\":[]}", batch)!);
    }
}
