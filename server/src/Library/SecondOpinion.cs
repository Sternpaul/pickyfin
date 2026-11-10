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
        if (!settings.CheckEnabled || string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.CheckModel)) return;

        List<MovieMetadata> pending;
        await using (var db = _database.Open())
            pending = await db.Movies.AsNoTracking().Where(m => m.IsClassified && !m.Checked && !m.ManuallyEdited && (m.WebNotes == null || m.WebNotes == "")).ToListAsync(cancellationToken);
        pending = pending.Where(m => !settings.IsLibraryExcluded(m.Libraries)).OrderBy(m => m.Title).ToList();
        if (pending.Count == 0) return;

        _logger.LogInformation("Second opinion: checking {Count} movies with {Model}, {Batch} per call", pending.Count, settings.CheckModel, BatchSize);
        var provider = _ai.GetProvider();
        var flaggedTotal = 0;
        TaggingEta.Start(pending.Count, BatchSize);
        try
        {
            for (var i = 0; i < pending.Count; i += BatchSize)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var batch = pending.Skip(i).Take(BatchSize).ToList();
                TaggingEta.BatchStarted(pending.Count - i);
                string answer;
                try
                {
                    using (Activity.Begin($"Second opinion on movies {i + 1}–{i + batch.Count} of {pending.Count}: waiting for {provider.Name}"))
                        answer = await provider.CompleteJsonAsync(ClassificationPrompt.BuildCheck(batch), settings.CheckModel, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("Second opinion stopped: {Error}. It continues at the next library sync.", ex.Message);
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
        _logger.LogInformation("Second opinion finished: {Count} movie(s) flagged", flaggedTotal);
    }

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
