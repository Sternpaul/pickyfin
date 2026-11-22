using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;
using Pickyfin.Server.Library;

namespace Pickyfin.Server.Curation;

/// <summary>One series someone watched, as far as it says something about their taste.</summary>
/// <param name="Episodes">Episodes they finished (only those that count, see "count history from").</param>
/// <param name="Seasons">Different seasons those episodes are from.</param>
/// <param name="Level">"watched" or "finished" (these count); "sampled" or "not taste" (these don't).</param>
public sealed record SeriesWatch(MovieMetadata Series, int Episodes, int Seasons, DateTime? LastWatched, string Level)
{
    public bool Finished => Level == SeriesTaste.Finished;
    public bool Counts => Level is SeriesTaste.Watched or SeriesTaste.Finished;
}

/// <summary>
/// What the series someone watches say about their taste in films. Series aren't recommended
/// (yet); they join the films someone watched when the shortlist is scored, and the AI sees them.
/// - 1–2 episodes: sampled, says nothing (ignored)
/// - 3 or more: watched, counts as taste
/// - 80 % of the episodes in the library, or episodes from 3 or more seasons: finished, counts
///   like a loved film
/// Reality, talk and game shows say nothing about films and never count.
/// </summary>
public static class SeriesTaste
{
    public const string Watched = "watched", Finished = "finished", Sampled = "sampled", NotTasteLevel = "not taste";
    public const int MinEpisodes = 3;
    /// <summary>At most this many series join someone's taste, the most recently watched, so a binge doesn't drown the films.</summary>
    public const int MaxInTaste = 10;

    /// <param name="all">Also the series that don't count (sampled, reality…), for the admin page.</param>
    public static List<SeriesWatch> Read(IEnumerable<JellyfinItem> episodes, IReadOnlyDictionary<Guid, MovieMetadata> series, DateTime? countFrom, bool all = false)
    {
        var result = new List<SeriesWatch>();
        // An episode marked as played by hand has no date: it only counts when all history counts
        var counted = episodes.Where(e => e.SeriesId is not null
            && (countFrom is null || (e.UserData?.LastPlayedDate is { } at && at >= countFrom)));
        foreach (var group in counted.GroupBy(e => e.SeriesId!.Value))
        {
            if (!series.TryGetValue(group.Key, out var show)) continue;
            var count = group.Count();
            var seasons = group.Select(e => e.ParentIndexNumber).Where(n => n is > 0).Distinct().Count();
            var share = show.Episodes is > 0 and var total ? count / (double)total : 0;
            var level = show.SeriesKindSet.Items.Any(k => ClassificationVocabulary.NotTasteKinds.Contains(k, StringComparer.OrdinalIgnoreCase)) ? NotTasteLevel
                : count < MinEpisodes ? Sampled
                : share >= 0.8 || seasons >= 3 ? Finished : Watched;
            if (!all && level is Sampled or NotTasteLevel) continue;
            var last = group.Max(e => e.UserData?.LastPlayedDate);
            result.Add(new SeriesWatch(show, count, seasons, last, level));
        }
        return result.OrderByDescending(w => w.LastWatched ?? DateTime.MinValue).ThenByDescending(w => w.Episodes).ToList();
    }

    /// <summary>
    /// How much a series counts in someone's taste, 0..1 (a film they watched counts 1).
    /// At the default setting (50) a finished series counts like a film and a watched one 0.7;
    /// at 100 every series counts like a film; at 0 none counts.
    /// </summary>
    public static double Weight(SeriesWatch watch, int setting) =>
        Math.Min(1.0, (watch.Finished ? 1.0 : 0.7) * Math.Clamp(setting, 0, 100) / 50.0);

    /// <summary>The series that join the taste set with their weight: described ones only, the most recent first.</summary>
    public static List<(MovieMetadata Title, double Weight)> ForTaste(IEnumerable<SeriesWatch> watched, int setting) =>
        watched.Where(w => w.Series.IsClassified)
            .Take(MaxInTaste)
            .Select(w => (w.Series, Weight(w, setting)))
            .Where(x => x.Item2 > 0)
            .ToList();

    /// <summary>
    /// What the levels are now (from Read with all: true), to notice a series started, or moving up
    /// (now counts, or finished). Another episode alone doesn't change it.
    /// </summary>
    public static string Signature(IEnumerable<SeriesWatch> watched) =>
        string.Join(",", watched.OrderBy(w => w.Series.ItemId).Select(w => $"{w.Series.ItemId:N}:{w.Level[0]}"));

    /// <summary>One line for the AI: "Breaking Bad (2008–2013, 5 seasons): finished".</summary>
    public static string Line(SeriesWatch w)
    {
        var run = new[] { Ai.ClassificationPrompt.Aired(w.Series), w.Series.Seasons is { } n ? $"{n} season{(n == 1 ? "" : "s")}" : null }.Where(x => x is not null);
        var how = w.Finished ? "finished" : $"{w.Episodes} episode{(w.Episodes == 1 ? "" : "s")}";
        var tags = string.Join(", ", w.Series.SubcategorySet.Items.Concat(w.Series.MoodSet.Items).Take(4));
        return $"{w.Series.Title} ({string.Join(", ", run)}): {how}{(tags.Length > 0 ? $" [{tags}]" : "")}";
    }
}
