using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// A second model reviews the tags, 40 movies per call. Reviewing is much quicker than
/// tagging (the answer only names the few that look wrong), and a different model
/// catches different mistakes. What it flags goes to the Issues page; with an Exa key,
/// flagged films are looked up on the web and tagged again.
/// </summary>
public sealed class SecondOpinion
{
    public const int BatchSize = 40;
    /// <summary>Calls per round, so picks never wait long behind a big review; the rest follows a few minutes later.</summary>
    public const int BatchesPerRound = 8;
    private const string Key = "second", Title = "Second opinion";

    // After an error: wait 10, 20, 40, then 60 minutes between tries.
    // Films whose batch failed 3 times are set aside for a day, so one bad batch can't block the queue.
    private static DateTime _retryAt = DateTime.MinValue;
    private static int _errorsInARow;
    private static readonly Dictionary<Guid, int> FailedTimes = new();
    private static readonly Dictionary<Guid, DateTime> SetAsideUntil = new();

    public static void ResetPause() { _retryAt = DateTime.MinValue; _errorsInARow = 0; }

    /// <summary>When the next try is due after an error (null when not waiting).</summary>
    public static DateTime? RetryAt => _retryAt > DateTime.UtcNow ? _retryAt : null;

    private readonly Database _database;
    private readonly MovieStore _store;
    private readonly AIProviderFactory _ai;
    private readonly MovieClassifier _classifier;
    private readonly ILogger<SecondOpinion> _logger;

    public SecondOpinion(Database database, MovieStore store, AIProviderFactory ai, MovieClassifier classifier, ILogger<SecondOpinion> logger)
    {
        _database = database;
        _store = store;
        _ai = ai;
        _classifier = classifier;
        _logger = logger;
    }

    public async Task CheckPendingAsync(CancellationToken cancellationToken)
    {
        var settings = ServerSettings.Current;
        if (!settings.CheckEnabled || string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.CheckModel))
        {
            Jobs.Idle(Key, Title, "Off (AI page)");
            return;
        }

        List<MovieMetadata> all;
        await using (var db = _database.Open())
            all = await db.Movies.AsNoTracking().Where(m => m.IsClassified && !m.Checked && !m.ManuallyEdited && (m.WebNotes == null || m.WebNotes == "")).ToListAsync(cancellationToken);
        all = all.Where(m => !settings.IsLibraryExcluded(m.Libraries)).ToList();
        var now = DateTime.UtcNow;
        foreach (var expired in SetAsideUntil.Where(x => x.Value <= now).Select(x => x.Key).ToList()) { SetAsideUntil.Remove(expired); FailedTimes.Remove(expired); }
        var setAside = all.Count(m => SetAsideUntil.ContainsKey(m.ItemId));
        var pending = all.Where(m => !SetAsideUntil.ContainsKey(m.ItemId))
            .OrderBy(m => FailedTimes.GetValueOrDefault(m.ItemId)).ThenBy(m => m.Title).ToList();
        var asideText = setAside > 0 ? $"; {setAside} set aside until tomorrow (the model kept rejecting them)" : "";
        if (pending.Count == 0)
        {
            Jobs.Idle(Key, Title, $"Up to date: every described film is checked{asideText}");
            return;
        }
        if (_retryAt > now)
        {
            Jobs.Idle(Key, Title, $"Paused after an error, {pending.Count} films to go{asideText}", _retryAt, error: true);
            return;
        }

        var thisRound = pending.Take(BatchSize * BatchesPerRound).ToList();
        _logger.LogInformation("Second opinion: {Count} movies to check with {Model}; {Round} this round, {Batch} per call",
            pending.Count, settings.CheckModel, thisRound.Count, BatchSize);
        var provider = _ai.GetProvider();
        var flaggedTotal = 0;
        var failed = false;
        TaggingEta.Start(pending.Count, BatchSize);
        try
        {
            for (var i = 0; i < thisRound.Count; i += BatchSize)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var batch = thisRound.Skip(i).Take(BatchSize).ToList();
                TaggingEta.BatchStarted(pending.Count - i);
                Jobs.Running(Key, Title, $"Checking films {i + 1}–{i + batch.Count} of {pending.Count} with {settings.CheckModel}");
                string answer;
                try
                {
                    using (Activity.Begin($"Second opinion on movies {i + 1}–{i + batch.Count} of {pending.Count}: waiting for {provider.Name}"))
                        answer = await provider.CompleteJsonAsync(ClassificationPrompt.BuildCheck(batch), settings.CheckModel, cancellationToken);
                    _errorsInARow = 0;
                    foreach (var m in batch) FailedTimes.Remove(m.ItemId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed = true;
                    _errorsInARow++;
                    _retryAt = DateTime.UtcNow.AddMinutes(Math.Min(60, 10 * Math.Pow(2, _errorsInARow - 1)));
                    var aside = new List<string>();
                    foreach (var m in batch)
                    {
                        var times = FailedTimes[m.ItemId] = FailedTimes.GetValueOrDefault(m.ItemId) + 1;
                        if (times >= 3) { SetAsideUntil[m.ItemId] = DateTime.UtcNow.AddDays(1); aside.Add(m.Title); }
                    }
                    _logger.LogError("Second opinion paused: {Error}. Trying again at {When}.", ex.Message, Jobs.When(_retryAt));
                    if (aside.Count > 0)
                        _logger.LogWarning("Second opinion: this batch failed 3 times; its {Count} films are set aside until tomorrow and the rest carries on", aside.Count);
                    Jobs.Idle(Key, Title, $"Paused after an error: {Short(ex.Message)}", _retryAt, error: true);
                    break;
                }

                var problems = Parse(answer, batch);
                if (problems is null)
                {
                    _logger.LogWarning("Second opinion: couldn't read the answer for movies {Start}–{End}; they're checked again next time", i + 1, i + batch.Count);
                    TaggingEta.BatchDone();
                    continue;
                }
                foreach (var movie in batch)
                {
                    movie.Checked = true;
                    if (problems.TryGetValue(movie, out var line)) movie.Issue = Issues.Add(movie.Issue, line);
                }
                flaggedTotal += problems.Count;
                await _store.SaveMoviesAsync(batch, cancellationToken);
                _logger.LogInformation("Second opinion on movies {Start}–{End}: {Count} look wrong", i + 1, i + batch.Count, problems.Count);

                await _classifier.LookUpFlaggedAsync(provider, problems.Keys.ToList(), cancellationToken);
                TaggingEta.BatchDone();
            }
        }
        finally { TaggingEta.Stop(); }
        if (failed) return;
        var left = pending.Count - thisRound.Count;
        _logger.LogInformation("Second opinion: round done, {Count} movie(s) flagged; {Left} to go", flaggedTotal, left);
        Jobs.Idle(Key, Title, left > 0 ? $"{left} films to go; next round in a few minutes{asideText}" : $"Up to date: every described film is checked{asideText}");
    }

    private static string Short(string message) => message.Length <= 160 ? message : message[..160] + "…";

    /// <summary>Movie → one line for the Issues page; null when the answer couldn't be read.</summary>
    internal static Dictionary<MovieMetadata, string>? Parse(string? answer, List<MovieMetadata> batch)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(MovieClassifier.ExtractJson(answer)); }
        catch (JsonException) { return null; }
        using var _ = document;
        var root = document.RootElement;
        JsonElement list;
        if (root.ValueKind == JsonValueKind.Array) list = root;
        else if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().FirstOrDefault(p => p.Name.Equals("problems", StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.Array } p) list = p;
        else return null;

        static List<string> Strings(JsonElement e, string name) =>
            e.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.Array } a
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList()
                : new();

        var result = new Dictionary<MovieMetadata, string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var movie = MovieClassifier.Match(item, batch);
            if (movie is null) continue;
            var tags = movie.SubcategorySet.Items.Concat(movie.MoodSet.Items).ToList();
            // Only what it actually has; a "wrong" tag the movie doesn't carry is noise
            var wrong = Strings(item, "Wrong").Where(w => tags.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
            if (wrong.Count == 0) continue;
            var better = Strings(item, "Better").Select(b => Library.ClassificationVocabulary.CanonicalSubcategory(b) ?? Library.ClassificationVocabulary.CanonicalMood(b)).Where(b => b is not null).ToList();
            var why = item.EnumerateObject().FirstOrDefault(p => p.Name.Equals("Why", StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } w ? w.GetString() : null;
            var line = $"Second opinion: {string.Join(", ", wrong.Select(x => $"\"{x}\""))} doesn't fit"
                + (better.Count > 0 ? $"; better: {string.Join(", ", better)}" : "")
                + (string.IsNullOrWhiteSpace(why) ? "" : $" ({why!.Trim().TrimEnd('.')})");
            result[movie] = line;
        }
        return result;
    }
}
