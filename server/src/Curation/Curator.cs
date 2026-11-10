using System.Text;
using System.Text.Json;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;
using Pickyfin.Server.Library;

namespace Pickyfin.Server.Curation;

/// <summary>One row of picks: becomes one playlist, which the TV app shows as one row.</summary>
public sealed record PickRow(string Slot, string Name, List<Guid> Items);

public sealed record CurationResult(List<PickRow> Rows, string Summary);

/// <summary>
/// Picks recommendations for one user:
/// 1. history: movies finished on or after the user's "count from" date
/// 2. shortlist (no AI): unwatched movies scored against the brief, the counted
///    history and acclaim, with a cap per subcategory so it isn't one-note
/// 3. choice (one AI call): the AI picks the "Recommended for you" row from the
///    shortlist, reading the brief and the history
/// 4. "Because You Watched …" rows for the two most recent counted watches, ranked
///    by similarity to that movie, nudged by the brief
/// </summary>
public sealed class Curator
{
    public const string RecommendedName = "Recommended for you";
    public const string BecausePrefix = "Because You Watched ";

    private readonly JellyfinClient _jellyfin;
    private readonly MovieStore _store;
    private readonly AIProviderFactory _ai;
    private readonly ILogger<Curator> _logger;
    private readonly SimilarityEngine _similarity = new() { KeywordWeight = 0.10 };

    public Curator(JellyfinClient jellyfin, MovieStore store, AIProviderFactory ai, ILogger<Curator> logger)
    {
        _jellyfin = jellyfin;
        _store = store;
        _ai = ai;
        _logger = logger;
    }

    private static ServerSettings Settings => ServerSettings.Current;

    public async Task<CurationResult> CurateAsync(UserProfile user, BriefProfile? brief, CancellationToken cancellationToken)
    {
        var library = (await _store.GetAllMoviesAsync(cancellationToken)).ToDictionary(m => m.ItemId);
        var visible = await _jellyfin.GetMoviesAsync(user.UserId, withDetails: false, cancellationToken);

        // 1. Counted history, newest first
        var history = visible
            .Where(i => i.UserData is { Played: true })
            .Where(i => user.CountHistoryFrom is null || (i.UserData!.LastPlayedDate ?? DateTime.MinValue) >= user.CountHistoryFrom)
            .OrderByDescending(i => i.UserData!.LastPlayedDate ?? DateTime.MinValue)
            .Select(i => library.GetValueOrDefault(i.Id))
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        // Candidates: what this user may see and hasn't finished or started
        var candidates = visible
            .Where(i => i.UserData is not { Played: true } && (i.UserData?.PlaybackPositionTicks ?? 0) == 0)
            .Select(i => library.GetValueOrDefault(i.Id))
            .Where(m => m is not null && !Settings.IsLibraryExcluded(m.Libraries))
            .Select(m => m!)
            .ToList();
        if (brief is not null)
            candidates = candidates.Where(m => !IsAvoided(m, brief)).ToList();

        // The same film can sit in several libraries (e.g. "Movies" and "Greek Movies");
        // recommend it once, and not at all if any copy was watched
        var seen = history.Select(Identity).ToHashSet();
        candidates = candidates
            .Where(m => !seen.Contains(Identity(m)))
            .GroupBy(Identity)
            .Select(g => g.OrderByDescending(m => m.IsClassified).First())
            .ToList();

        if (candidates.Count == 0)
            return new CurationResult(new(), "Nothing left to recommend: this user has seen everything they can access.");

        // 2. Shortlist
        var recentHistory = history.Take(30).ToList();
        var scored = candidates
            .Select(m => (Movie: m, Score: Score(m, brief, recentHistory)))
            .OrderByDescending(x => x.Score)
            .ToList();
        var shortlist = Diversify(scored.Select(x => x.Movie), Settings.ShortlistSize);

        // 3. AI picks the main row
        var perRow = Math.Clamp(Settings.PicksPerRow, 5, 50);
        List<Guid> recommended;
        string how;
        try
        {
            recommended = await AskAiAsync(user, brief, recentHistory, shortlist, perRow, cancellationToken);
            how = "AI picked from a shortlist of " + shortlist.Count;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning("AI pick failed for {User}, using the shortlist order instead: {Error}", user.Name, error.Message);
            recommended = new();
            how = "shortlist order (AI unavailable: " + error.Message + ")";
        }
        // Top up from the shortlist if the AI returned fewer
        foreach (var movie in shortlist)
        {
            if (recommended.Count >= perRow) break;
            if (!recommended.Contains(movie.ItemId)) recommended.Add(movie.ItemId);
        }

        var rows = new List<PickRow> { new("recommended", RecommendedName, recommended) };

        // 4. Because You Watched: two most recent counted watches with tags
        var used = new HashSet<Guid>(recommended);
        var slot = 1;
        foreach (var seed in history.Where(m => m.IsClassified).DistinctBy(Identity).Take(Math.Clamp(Settings.BecauseRows, 0, 2)))
        {
            var similar = candidates
                .Where(m => !used.Contains(m.ItemId))
                .Select(m => (Movie: m, Score: _similarity.CalculateSimilarity(seed, m) * 0.75 + (brief is null ? 0 : BriefScore(m, brief) * 0.25)))
                .Where(x => x.Score > 0.15)
                .OrderByDescending(x => x.Score)
                .Take(perRow)
                .Select(x => x.Movie.ItemId)
                .ToList();
            if (similar.Count < 5) continue;
            used.UnionWith(similar);
            rows.Add(new($"because-{slot++}", BecausePrefix + seed.Title, similar));
        }

        var summary = $"{rows.Count} row(s); {history.Count} counted watch(es); brief {(brief is null || brief.IsEmpty ? "not set" : "used")}; {how}.";
        return new CurationResult(rows, summary);
    }

    /// <summary>What makes two library entries the same film: IMDb id, or else title and year.</summary>
    private static string Identity(MovieMetadata m) =>
        !string.IsNullOrWhiteSpace(m.ImdbId) ? m.ImdbId! : $"{m.Title.Trim().ToLowerInvariant()}|{m.ReleaseYear}";

    // ---------- scoring ----------

    private static bool IsAvoided(MovieMetadata movie, BriefProfile brief) =>
        movie.SubcategorySet.Items.Any(s => brief.AvoidSubcategories.Contains(s, StringComparer.OrdinalIgnoreCase))
        || movie.MoodSet.Items.Any(s => brief.AvoidMoods.Contains(s, StringComparer.OrdinalIgnoreCase));

    /// <summary>How well a movie fits the brief, 0..1.</summary>
    internal static double BriefScore(MovieMetadata movie, BriefProfile brief)
    {
        double subcategory = 0;
        var subs = movie.SubcategorySet.Items;
        for (var i = 0; i < subs.Count; i++)
        {
            if (!brief.Subcategories.Contains(subs[i], StringComparer.OrdinalIgnoreCase)) continue;
            subcategory = Math.Max(subcategory, i == 0 ? 1.0 : i == 1 ? 0.75 : 0.6);
        }

        var moodHits = movie.MoodSet.Items.Count(m => brief.Moods.Contains(m, StringComparer.OrdinalIgnoreCase));
        var mood = brief.Moods.Count == 0 ? 0 : Math.Min(1.0, moodHits / (double)Math.Min(2, brief.Moods.Count));

        var themeHits = movie.ThemeSet.Items.Count(t => brief.Themes.Any(b => t.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(t, StringComparison.OrdinalIgnoreCase)));
        var theme = Math.Min(1.0, themeHits / 2.0);

        var keywordHits = movie.KeywordSet.Items.Count(k => brief.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase));
        var keyword = Math.Min(1.0, keywordHits / 2.0);

        return subcategory * 0.5 + mood * 0.25 + theme * 0.15 + keyword * 0.10;
    }

    private double HistoryScore(MovieMetadata movie, List<MovieMetadata> history)
    {
        if (history.Count == 0 || !movie.IsClassified) return 0;
        // Average of the three closest watches: one strong match counts, but not alone
        var best = history.Select(h => _similarity.CalculateSimilarity(h, movie)).OrderByDescending(s => s).Take(3).ToList();
        return best.Average();
    }

    private double Score(MovieMetadata movie, BriefProfile? brief, List<MovieMetadata> history)
    {
        var hasBrief = brief is { IsEmpty: false };
        var hasHistory = history.Count > 0;
        var acclaim = Math.Clamp(movie.Acclaim / 10.0, 0, 1);

        if (hasBrief && hasHistory) return BriefScore(movie, brief!) * 0.55 + HistoryScore(movie, history) * 0.30 + acclaim * 0.15;
        if (hasBrief) return BriefScore(movie, brief!) * 0.80 + acclaim * 0.20;
        if (hasHistory) return HistoryScore(movie, history) * 0.80 + acclaim * 0.20;
        // Nothing known: well-regarded films, a little preference for newer additions
        var freshness = Math.Max(0, 1 - (DateTime.UtcNow - movie.DateAdded).TotalDays / 365.0);
        return acclaim * 0.85 + freshness * 0.15;
    }

    /// <summary>Takes the best-scored movies, but no first-subcategory may fill more than a quarter.</summary>
    private static List<MovieMetadata> Diversify(IEnumerable<MovieMetadata> ranked, int size)
    {
        var cap = Math.Max(3, size / 4);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MovieMetadata>();
        var overflow = new List<MovieMetadata>();
        foreach (var movie in ranked)
        {
            if (result.Count >= size) break;
            var key = movie.SubcategorySet.Items.FirstOrDefault() ?? "";
            var count = counts.GetValueOrDefault(key);
            if (key.Length > 0 && count >= cap) { overflow.Add(movie); continue; }
            counts[key] = count + 1;
            result.Add(movie);
        }
        foreach (var movie in overflow)
        {
            if (result.Count >= size) break;
            result.Add(movie);
        }
        return result;
    }

    // ---------- AI choice ----------

    private async Task<List<Guid>> AskAiAsync(
        UserProfile user, BriefProfile? brief, List<MovieMetadata> history, List<MovieMetadata> shortlist, int count, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Settings.ApiKey)) throw new InvalidOperationException("no AI key set");

        var prompt = new StringBuilder();
        prompt.AppendLine($"Pick {count} movies for one viewer from the numbered list below, best first.");
        prompt.AppendLine();
        prompt.AppendLine("About the viewer (written by the person who runs the media server; the most important signal):");
        prompt.AppendLine(string.IsNullOrWhiteSpace(user.Brief) ? "(nothing written)" : user.Brief.Trim());
        prompt.AppendLine();
        prompt.AppendLine(history.Count == 0
            ? "Recently finished: nothing yet."
            : "Recently finished, newest first (a shared TV may also show others' viewing; trust the description above more):");
        foreach (var movie in history.Take(25)) prompt.AppendLine($"- {movie.Title} ({movie.ReleaseYear})");
        prompt.AppendLine();
        prompt.AppendLine("Candidates:");
        for (var i = 0; i < shortlist.Count; i++) prompt.AppendLine($"{i + 1}. {Line(shortlist[i])}");
        prompt.AppendLine();
        prompt.AppendLine("Rules: follow the description first; mix well-known and lesser-known films; avoid near-duplicates (several films of one franchise or the same niche); never invent numbers.");
        prompt.AppendLine($@"Answer with ONLY a JSON object: {{""picks"": [numbers from the list, {count} of them, best first]}}");

        var system = "You are a thoughtful film curator choosing for one specific person. Output only JSON.";
        var response = await _ai.GetProvider().ChatAsync(prompt.ToString(), system, cancellationToken);
        var json = BriefInterpreter.ExtractJson(response) ?? throw new InvalidOperationException("no JSON in the AI answer");

        using var document = JsonDocument.Parse(json);
        var picks = new List<Guid>();
        if (document.RootElement.TryGetProperty("picks", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in array.EnumerateArray())
            {
                if (!value.TryGetInt32(out var number) || number < 1 || number > shortlist.Count) continue;
                var id = shortlist[number - 1].ItemId;
                if (!picks.Contains(id)) picks.Add(id);
                if (picks.Count >= count) break;
            }
        }
        if (picks.Count == 0) throw new InvalidOperationException("the AI answer had no usable picks");
        return picks;
    }

    private static string Line(MovieMetadata m)
    {
        var tags = string.Join(", ", m.SubcategorySet.Items.Concat(m.MoodSet.Items).Take(5));
        var plot = (m.Plot ?? "").Replace('\n', ' ');
        if (plot.Length > 140) plot = plot[..140] + "…";
        return $"{m.Title} ({m.ReleaseYear}) [{tags}] {plot}";
    }
}
