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
    /// <summary>Strengths 0..100 from the Tuning page; 50 is the default.</summary>
    public record Strengths(int People = 50, int Country = 50, int Era = 50)
    {
        public int PeopleSlots => (int)Math.Round(People / 25.0);   // 50 → 2, 100 → 4
        public int CountrySlots => (int)Math.Round(Country * 0.06); // 50 → 3, 100 → 6
        public static Strengths FromSettings(ServerSettings s) => new(s.BecausePeople, s.BecauseCountry, s.BecauseEra);
    }

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

    // At strength 50: same director +10 points, a lead actor +8 (together at most +15),
    // uncommon country +8, within 5 years +5, within 12 years +3, over 30 years apart −3.
    // Strength scales these linearly: 0 = off, 100 = double.
    public static double Nudge(MovieMetadata seed, MovieMetadata m, string? country, Strengths? strengths = null)
    {
        var st = strengths ?? new Strengths();
        double people = 0, era = 0;
        if (Split(seed.Director).Any(d => Split(m.Director).Contains(d, StringComparer.OrdinalIgnoreCase))) people += 0.10;
        if (Split(seed.Cast).Take(3).Any(a => Everyone(m).Contains(a))) people += 0.08;
        people = Math.Min(people, 0.15);
        var sameCountry = country is not null && Countries(m).Contains(country, StringComparer.OrdinalIgnoreCase) ? 0.08 : 0;
        if (seed.ReleaseYear is { } a && m.ReleaseYear is { } b)
        {
            var gap = Math.Abs(a - b);
            era = gap <= 5 ? 0.05 : gap <= 12 ? 0.03 : gap > 30 ? -0.03 : 0;
        }
        return people * st.People / 50.0 + sameCountry * st.Country / 50.0 + era * st.Era / 50.0;
    }

    /// <summary>Why a film is in the row, for the log: "same people: …", "French" or null (tags).</summary>
    public record Picked(MovieMetadata Movie, string? Reason);

    /// <param name="scored">Candidates with their similarity score (before nudges).</param>
    public static List<Picked> Pick(MovieMetadata seed, IEnumerable<(MovieMetadata Movie, double Score)> scored, Dictionary<string, double> shares, int count, Strengths? strengths = null)
    {
        var st = strengths ?? new Strengths();
        if (count <= 0) return new();
        var country = st.Country > 0 ? UncommonCountry(seed, shares) : null;
        var ranked = scored
            .Select(x => (x.Movie, x.Score, Final: x.Score + Nudge(seed, x.Movie, country, st)))
            .OrderByDescending(x => x.Final)
            .ToList();

        // Places kept for the same people and the same uncommon country, if they're related at all
        var people = st.People > 0 ? ranked.Where(x => x.Score > 0.10 && SharesPeople(seed, x.Movie)).Take(st.PeopleSlots).ToList() : new();
        var local = country is null ? new() : ranked.Where(x => x.Score > 0.10 && !people.Contains(x) && Countries(x.Movie).Contains(country, StringComparer.OrdinalIgnoreCase)).Take(st.CountrySlots).ToList();
        var kept = people.Concat(local).Take(count).ToList();

        var rest = ranked.Where(x => x.Final > 0.15 && !kept.Contains(x)).Take(count - kept.Count);
        string? Why((MovieMetadata Movie, double Score, double Final) x) =>
            SharesPeople(seed, x.Movie) && st.People > 0 ? "same people: " + string.Join(", ", Leads(seed).Where(Everyone(x.Movie).Contains))
            : country is not null && Countries(x.Movie).Contains(country, StringComparer.OrdinalIgnoreCase) ? country
            : null;
        return kept.Concat(rest).OrderByDescending(x => x.Final).Select(x => new Picked(x.Movie, Why(x))).ToList();
    }
}
