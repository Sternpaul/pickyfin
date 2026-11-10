using Pickyfin.Server;
using Pickyfin.Server.Data.Models;
using Xunit;

public class SearchTests
{
    private static MovieMetadata M(string title, int year = 2000, string? director = null, string? cast = null) =>
        new() { Title = title, ReleaseYear = year, Director = director, Cast = cast };

    [Fact]
    public void AccentsAndPunctuationDontMatter()
    {
        Assert.True(BrowserApi.SearchRank(M("Amélie"), "amelie") > 0);
        Assert.True(BrowserApi.SearchRank(M("Spider-Man: No Way Home"), "spider man") > 0);
        Assert.True(BrowserApi.SearchRank(M("Spider-Man: No Way Home"), "spiderman") > 0);
    }

    [Fact]
    public void TitleMatchesRankAboveCast()
    {
        var byTitle = BrowserApi.SearchRank(M("Heat", 1995), "heat");
        var byCast = BrowserApi.SearchRank(M("Collateral", 2004, cast: "Tom Cruise, Jamie Foxx, Heather Heat"), "heat");
        Assert.True(byTitle > byCast && byCast > 0);
    }

    [Fact]
    public void WordsCanComeFromDifferentFields()
    {
        Assert.True(BrowserApi.SearchRank(M("Heat", 1995, director: "Michael Mann"), "heat mann") > 0);
        Assert.True(BrowserApi.SearchRank(M("Heat", 1995), "heat 1995") > 0);
        Assert.Equal(0, BrowserApi.SearchRank(M("Heat", 1995), "heat 1996"));
    }

    [Fact]
    public void ArticleCanBeLeftOut() =>
        Assert.True(BrowserApi.SearchRank(M("The Godfather"), "godfather") >= 60);
}
