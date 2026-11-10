using Pickyfin.Server.Curation;
using Pickyfin.Server.Data.Models;
using Xunit;

public class BecauseRowTests
{
    private static MovieMetadata Film(string title, int year, string country, string director = "", string cast = "") =>
        new() { ItemId = Guid.NewGuid(), Title = title, ReleaseYear = year, Countries = country, Director = director, Cast = cast, IsClassified = true };

    [Fact]
    public void KeepsPlacesForTheSameLeadAndAnUncommonCountry()
    {
        var seed = Film("Cocorico 2", 2025, "France", "Julien Hervé", "Christian Clavier, Didier Bourdon");
        var library = Enumerable.Range(0, 40).Select(i => Film($"US comedy {i}", 2000 + i % 25, "United States of America")).ToList();
        var french = Film("Ibiza", 2019, "France");
        var clavier = Film("Les Visiteurs", 1993, "France", "Jean-Marie Poiré", "Christian Clavier, Jean Reno");
        library.Add(french); library.Add(clavier); library.Add(seed);
        var shares = BecauseRow.CountryShares(library);
        Assert.Equal("France", BecauseRow.UncommonCountry(seed, shares));

        // The US comedies score a bit higher on tags alone
        var scored = library.Where(m => m != seed).Select(m => (m, m.Countries == "France" ? 0.2 : 0.4));
        var row = BecauseRow.Pick(seed, scored, shares, 10).Select(p => p.Movie).ToList();
        Assert.Contains(french, row);
        Assert.Contains(clavier, row);
        Assert.Equal(10, row.Count);
    }

    [Fact]
    public void CommonCountryGetsNoPlaces()
    {
        var seed = Film("The Revenant", 2015, "United States of America", "Alejandro González Iñárritu", "Leonardo DiCaprio, Tom Hardy");
        var library = Enumerable.Range(0, 10).Select(i => Film($"US {i}", 1980, "United States of America")).Append(seed).ToList();
        Assert.Null(BecauseRow.UncommonCountry(seed, BecauseRow.CountryShares(library)));
        var dicaprio = Film("The Departed", 2006, "United States of America", "Martin Scorsese", "Leonardo DiCaprio, Matt Damon");
        Assert.True(BecauseRow.SharesPeople(seed, dicaprio));
        Assert.True(BecauseRow.Nudge(seed, dicaprio, null) > BecauseRow.Nudge(seed, library[0], null));
    }

    [Fact]
    public void AtZeroTheSlidersChangeNothing()
    {
        var seed = Film("Cocorico 2", 2025, "France", "Julien Hervé", "Christian Clavier");
        var other = Film("Les Visiteurs", 1960, "France", "Jean-Marie Poiré", "Christian Clavier");
        Assert.Equal(0, BecauseRow.Nudge(seed, other, "France", new BecauseRow.Strengths(0, 0, 0)));
        Assert.Equal(0, new BecauseRow.Strengths(0, 0, 0).PeopleSlots);
        Assert.Equal(2, new BecauseRow.Strengths().PeopleSlots);
        Assert.Equal(3, new BecauseRow.Strengths().CountrySlots);
        var library = Enumerable.Range(0, 20).Select(i => Film($"US {i}", 2020, "United States of America")).Append(other).Append(seed).ToList();
        var scored = library.Where(m => m != seed).Select(m => (m, m == other ? 0.2 : 0.4));
        var row = BecauseRow.Pick(seed, scored, BecauseRow.CountryShares(library), 10, new BecauseRow.Strengths(0, 0, 0));
        Assert.DoesNotContain(row, p => p.Movie == other);
    }
}
