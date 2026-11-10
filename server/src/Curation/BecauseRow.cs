using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Curation;

/// <summary>
/// Ranks a "Because You Watched …" row. The tag similarity does most of the work; on top:
/// - the same director or lead actors: a nudge, and up to two places kept for such films
/// - the same country, when it's uncommon in the library (a French comedy after a French
///   comedy): a nudge, and up to three places kept
/// - a release year close to the seed: a small nudge, far away: a small penalty
/// </summary>
public static class BecauseRow
{
    public const int PeopleSlots = 2, CountrySlots = 3;
    private const double Uncommon = 0.25; // a country with less than a quarter of the library

    private static IEnumerable<string> Split(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Director(s) and the first three actors: the names people choose films by.</summary>
    public static HashSet<string> Leads(MovieMetadata m) =>
        Split(m.Director).Concat(Split(m.Cast).Take(3)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Everyone(MovieMetadata m) =>
        Split(m.Director).Concat(Split(m.Cast)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static List<string> Countries(MovieMetadata m) => Split(m.Countries).ToList();

    /// <summary>Share of the library from each country (0..1).</summary>
    public static Dictionary<string, double> CountryShares(IReadOnlyCollection<MovieMetadata> library)
    {
        var withCountry = library.Where(m => !string.IsNullOrWhiteSpace(m.Countries)).ToList();
        if (withCountry.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        return withCountry.SelectMany(m => Countries(m).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count() / (double)withCountry.Count, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The seed's uncommon country, if its main one is uncommon in this library.</summary>
    public static string? UncommonCountry(MovieMetadata seed, Dictionary<string, double> shares) =>
        Countries(seed).FirstOrDefault() is { } main && shares.TryGetValue(main, out var share) && share < Uncommon ? main : null;

    public static bool SharesPeople(MovieMetadata seed, MovieMetadata m)
    {
        var leads = Leads(seed);
        return leads.Count > 0 && Everyone(m).Overlaps(leads);
    }

    public static double Nudge(MovieMetadata seed, MovieMetadata m, string? country)
    {
        var nudge = 0.0;
        var leads = Leads(seed);
        if (Split(seed.Director).Any(d => Split(m.Director).Contains(d, StringComparer.OrdinalIgnoreCase))) nudge += 0.10;
        if (Split(seed.Cast).Take(3).Any(a => Everyone(m).Contains(a))) nudge += 0.08;
        nudge = Math.Min(nudge, 0.15);
        if (country is not null && Countries(m).Contains(country, StringComparer.OrdinalIgnoreCase)) nudge += 0.08;
        if (seed.ReleaseYear is { } a && m.ReleaseYear is { } b)
        {
            var gap = Math.Abs(a - b);
            nudge += gap <= 5 ? 0.05 : gap <= 12 ? 0.03 : gap > 30 ? -0.03 : 0;
        }
        return nudge;
    }

    /// <param name="scored">Candidates with their similarity score (before nudges).</param>
    public static List<MovieMetadata> Pick(MovieMetadata seed, IEnumerable<(MovieMetadata Movie, double Score)> scored, Dictionary<string, double> shares, int count)
    {
        if (count <= 0) return new();
        var country = UncommonCountry(seed, shares);
        var ranked = scored
            .Select(x => (x.Movie, x.Score, Final: x.Score + Nudge(seed, x.Movie, country)))
            .OrderByDescending(x => x.Final)
            .ToList();

        // Places kept for the same people and the same uncommon country, if they're related at all
        var kept = ranked.Where(x => x.Score > 0.10 && SharesPeople(seed, x.Movie)).Take(PeopleSlots).ToList();
        if (country is not null)
            kept.AddRange(ranked.Where(x => x.Score > 0.10 && !kept.Contains(x) && Countries(x.Movie).Contains(country, StringComparer.OrdinalIgnoreCase)).Take(CountrySlots));
        kept = kept.Take(count).ToList();

        var rest = ranked.Where(x => x.Final > 0.15 && !kept.Contains(x)).Take(count - kept.Count);
        return kept.Concat(rest).OrderByDescending(x => x.Final).Select(x => x.Movie).ToList();
    }
}
