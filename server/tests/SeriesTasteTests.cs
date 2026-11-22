using Pickyfin.Server.Curation;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;
using Pickyfin.Server.Library;
using Xunit;

/// <summary>What the series someone watches add to their taste in films, and the separate series matrix.</summary>
public class SeriesTasteTests
{
    private static MovieMetadata Show(string title, int episodes, string? kind = null) =>
        new() { ItemId = Guid.NewGuid(), Title = title, Kind = MovieMetadata.SeriesKind, Episodes = episodes, SeriesKinds = kind is null ? null : $"[\"{kind}\"]", IsClassified = true, ReleaseYear = 2010 };

    private static IEnumerable<JellyfinItem> Watched(MovieMetadata show, int count, int seasons = 1, DateTime? at = null) =>
        Enumerable.Range(0, count).Select(i => new JellyfinItem
        {
            Id = Guid.NewGuid(), SeriesId = show.ItemId, ParentIndexNumber = 1 + i % seasons,
            UserData = new JellyfinUserData { Played = true, LastPlayedDate = (at ?? new DateTime(2026, 11, 1)).AddHours(i) },
        });

    [Fact]
    public void Levels_follow_how_much_was_watched()
    {
        var sampled = Show("Sampled", 10);
        var watched = Show("Watched", 40);
        var finished = Show("Finished", 10);
        var long_ = Show("Three Seasons", 100);
        var reality = Show("Love Island", 50, "Reality Competition");
        var library = new[] { sampled, watched, finished, long_, reality }.ToDictionary(m => m.ItemId);
        var episodes = Watched(sampled, 2).Concat(Watched(watched, 5)).Concat(Watched(finished, 9))
            .Concat(Watched(long_, 12, seasons: 3)).Concat(Watched(reality, 30)).ToList();

        var counted = SeriesTaste.Read(episodes, library, null);
        Assert.Equal(new[] { "Finished", "Three Seasons", "Watched" }, counted.Select(w => w.Series.Title).OrderBy(t => t));
        Assert.Equal(SeriesTaste.Watched, counted.Single(w => w.Series == watched).Level);
        Assert.True(counted.Single(w => w.Series == finished).Finished);   // 9 of 10
        Assert.True(counted.Single(w => w.Series == long_).Finished);      // 3 seasons

        // The admin page sees the rest too, with why they don't count
        var all = SeriesTaste.Read(episodes, library, null, all: true);
        Assert.Equal(SeriesTaste.Sampled, all.Single(w => w.Series == sampled).Level);
        Assert.Equal(SeriesTaste.NotTasteLevel, all.Single(w => w.Series == reality).Level);
        Assert.False(all.Single(w => w.Series == reality).Counts);
    }

    [Fact]
    public void Only_episodes_from_the_count_from_date_count()
    {
        var show = Show("Shared TV Show", 40);
        var library = new Dictionary<Guid, MovieMetadata> { [show.ItemId] = show };
        var episodes = Watched(show, 10, at: new DateTime(2025, 1, 1)).Concat(Watched(show, 2, at: new DateTime(2026, 6, 1)))
            .Append(new JellyfinItem { SeriesId = show.ItemId, UserData = new JellyfinUserData { Played = true } }) // marked by hand, no date
            .ToList();
        Assert.Empty(SeriesTaste.Read(episodes, library, new DateTime(2026, 1, 1)));
        Assert.Equal(13, SeriesTaste.Read(episodes, library, null).Single().Episodes);
    }

    [Theory]
    [InlineData(true, 50, 1.0)]
    [InlineData(false, 50, 0.7)]
    [InlineData(false, 100, 1.0)]
    [InlineData(true, 25, 0.5)]
    [InlineData(true, 0, 0.0)]
    public void Weight_follows_the_tuning_setting(bool finished, int setting, double expected)
    {
        var watch = new SeriesWatch(Show("x", 10), 5, 1, null, finished ? SeriesTaste.Finished : SeriesTaste.Watched);
        Assert.Equal(expected, SeriesTaste.Weight(watch, setting), 3);
    }

    [Fact]
    public void At_most_ten_described_series_join_the_taste_newest_first()
    {
        var watched = Enumerable.Range(0, 14).Select(i =>
        {
            var show = Show($"S{i}", 10);
            show.IsClassified = i != 0; // the newest isn't described yet
            return new SeriesWatch(show, 5, 1, new DateTime(2026, 11, 20).AddDays(-i), SeriesTaste.Watched);
        }).ToList();
        var taste = SeriesTaste.ForTaste(watched, 50);
        Assert.Equal(10, taste.Count);
        Assert.Equal("S1", taste[0].Title.Title);
        Assert.All(taste, t => Assert.Equal(0.7, t.Weight, 3));
        Assert.Empty(SeriesTaste.ForTaste(watched, 0));
    }

    [Fact]
    public void A_series_counts_less_than_a_film_unless_finished()
    {
        var candidate = new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Candidate", IsClassified = true };
        var film = new MovieMetadata { ItemId = Guid.NewGuid(), IsClassified = true };
        var show = Show("Show", 10);
        double Similar(MovieMetadata a, MovieMetadata b) => 0.8;
        Assert.Equal(0.8, Curator.HistoryScore(candidate, new() { (film, 1.0) }, Similar), 3);
        Assert.Equal(0.56, Curator.HistoryScore(candidate, new() { (show, 0.7) }, Similar), 3);
    }

    [Fact]
    public void A_new_or_finished_series_changes_the_signature()
    {
        var show = Show("Show", 10);
        var before = SeriesTaste.Signature(new[] { new SeriesWatch(show, 4, 1, null, SeriesTaste.Watched) });
        var same = SeriesTaste.Signature(new[] { new SeriesWatch(show, 6, 1, null, SeriesTaste.Watched) });
        var after = SeriesTaste.Signature(new[] { new SeriesWatch(show, 9, 1, null, SeriesTaste.Finished) });
        Assert.Equal(before, same); // another episode alone: no new picks
        Assert.NotEqual(before, after);
    }

    // ---- meaning: films and series are two matrices

    private static Dictionary<Guid, float[]> Random(int count, int seed)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, count).ToDictionary(_ => Guid.NewGuid(), _ => Meaning.Normalize(new[] { (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() }));
    }

    [Fact]
    public void Series_never_shift_a_films_meaning_scores()
    {
        var films = Random(40, 3);
        var (a, b) = (films.Keys.First(), films.Keys.Last());
        var filmsOnly = new Meaning.Part("m", films, new());
        var series = Random(60, 4);
        var together = new Meaning.Part("m", films.Concat(series).ToDictionary(x => x.Key, x => x.Value), new(), series.Keys.ToHashSet());

        Assert.Equal(filmsOnly.Rank(a, b, false), together.Rank(a, b, false));
        // The film map is the same; the series have a map of their own
        Assert.Equal(filmsOnly.Map().Keys.OrderBy(k => k), together.Map().Keys.OrderBy(k => k));
        Assert.Equal(series.Keys.OrderBy(k => k), together.Map(MovieMetadata.SeriesKind).Keys.OrderBy(k => k));
        // "Both": every title on one map, while the scores stay per kind
        Assert.Equal(100, together.Map("all").Count);
        Assert.Equal(filmsOnly.Rank(a, b, false), together.Rank(a, b, false));
    }

    [Fact]
    public void A_film_close_to_a_series_ranks_high_among_films()
    {
        var films = Random(40, 5);
        var twin = Guid.NewGuid();
        films[twin] = Meaning.Normalize(new[] { 0f, 0f, -1f });
        var show = Guid.NewGuid();
        var series = Random(25, 6);
        series[show] = Meaning.Normalize(new[] { 0.01f, 0f, -1f });
        var snap = new Meaning.Snapshot(1, new List<Meaning.Part>
        {
            new("m", films.Concat(series).ToDictionary(x => x.Key, x => x.Value), new(), series.Keys.ToHashSet()),
        });
        Assert.Equal(1.0, snap.Score(show, twin)!.Value, 2);
        Assert.True(snap.Has(show));
    }

    [Fact]
    public void Too_few_series_make_no_series_matrix_but_films_work()
    {
        var films = Random(30, 7);
        var series = Random(5, 8);
        var part = new Meaning.Part("m", films.Concat(series).ToDictionary(x => x.Key, x => x.Value), new(), series.Keys.ToHashSet());
        Assert.True(part.Any);
        Assert.False(part.AnySeries);
        Assert.Null(part.Rank(films.Keys.First(), series.Keys.First(), false));
        Assert.NotNull(part.Rank(series.Keys.First(), films.Keys.First(), false));
    }
}
