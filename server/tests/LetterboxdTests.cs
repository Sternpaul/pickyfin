using Pickyfin.Server.Letterboxd;
using Xunit;

public class LetterboxdTests
{
    [Fact]
    public void Feed_keeps_films_and_skips_lists()
    {
        const string xml = """
            <?xml version='1.0' encoding='utf-8'?>
            <rss version="2.0" xmlns:letterboxd="https://letterboxd.com" xmlns:tmdb="https://themoviedb.org"><channel>
              <item><title>Coward, 2026 - ★★★★</title>
                <letterboxd:watchedDate>2026-11-10</letterboxd:watchedDate>
                <letterboxd:filmTitle>Coward</letterboxd:filmTitle><letterboxd:filmYear>2026</letterboxd:filmYear>
                <letterboxd:memberRating>4.0</letterboxd:memberRating><tmdb:movieId>1437981</tmdb:movieId></item>
              <item><title>Unrated, 1999</title>
                <letterboxd:filmTitle>Unrated</letterboxd:filmTitle><letterboxd:filmYear>1999</letterboxd:filmYear></item>
              <item><title>My list of favourites</title><description>a list</description></item>
            </channel></rss>
            """;
        var films = LetterboxdSync.ParseFeed(xml, Guid.NewGuid());

        Assert.Equal(2, films.Count);
        Assert.Equal("Coward", films[0].Title);
        Assert.Equal(2026, films[0].Year);
        Assert.Equal(4.0, films[0].Rating);
        Assert.Equal(1437981, films[0].TmdbId);
        Assert.Equal(new DateTime(2026, 11, 10), films[0].Date!.Value.Date);
        Assert.Null(films[1].Rating);
    }

    [Fact]
    public void Csv_handles_quotes_commas_and_bom()
    {
        const string csv = "﻿Date,Name,Year,Letterboxd URI,Rating\r\n"
            + "2024-01-02,\"Crouching Tiger, Hidden Dragon\",2000,https://boxd.it/a,4.5\r\n"
            + "2024-02-03,\"The \"\"Burbs\"\"\",1989,https://boxd.it/b,3\n"
            + "2024-03-04,Amélie,2001,https://boxd.it/c,5\n";
        var rows = LetterboxdSync.Rows(csv);

        Assert.Equal(3, rows.Count);
        Assert.Equal("Crouching Tiger, Hidden Dragon", rows[0]["Name"]);
        Assert.Equal("2024-01-02", rows[0]["Date"]);
        Assert.Equal("The \"Burbs\"", rows[1]["Name"]);
        Assert.Equal("5", rows[2]["Rating"]);
    }

    [Theory]
    [InlineData("The Lord of the Rings: The Return of the King", "lord of the rings the return of the king")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Fast & Furious", "fast and furious")]
    [InlineData("Fast and Furious", "fast and furious")]
    [InlineData("A Quiet Place", "quiet place")]
    [InlineData("Ψωμί για έναν Δραπέτη", "ψωμι για εναν δραπετη")]
    public void Titles_normalize_for_matching(string title, string expected) =>
        Assert.Equal(expected, LetterboxdSync.Normalize(title));
}
