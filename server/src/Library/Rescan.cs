using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// Step 4: now and then every film goes through the gold standard again. A fresh web lookup
/// (new reviews, new consensus on Wikipedia, Letterboxd, IMDb…), then the step-3 model describes it
/// again from everything, keeping what fits and changing only what's clearly wrong. Spread evenly
/// over the chosen interval, so the Exa spend is steady and each pass ends when the next one starts.
/// Films you edited or marked as checked are left alone.
/// </summary>
public sealed class Rescan
{
    private const string Key = "rescan", Title = "Re-checking everything";
    private const int PerRound = 20;

    private readonly Database _database;
    private readonly MovieStore _store;
    private readonly MovieClassifier _classifier;
    private readonly AIProviderFactory _ai;
    private readonly WebLookup _web;
    private readonly ILogger<Rescan> _logger;

    public Rescan(Database database, MovieStore store, MovieClassifier classifier, AIProviderFactory ai, WebLookup web, ILogger<Rescan> logger)
    {
        _database = database;
        _store = store;
        _classifier = classifier;
        _ai = ai;
        _web = web;
        _logger = logger;
    }

    // Exa lookups made by the re-check today, so the whole-library lookup keeps its own daily number
    private static string _day = "";
    private static int _lookupsToday;
    public static int LookupsToday => _day == DateTime.UtcNow.ToString("yyyy-MM-dd") ? _lookupsToday : 0;
    private static void CountLookup()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (_day != today) { _day = today; _lookupsToday = 0; }
        _lookupsToday++;
    }

    public static bool Eligible(MovieMetadata m) =>
        m.IsClassified && !m.ManuallyEdited && !m.Verified && !ServerSettings.Current.IsLibraryExcluded(m.Libraries);

    /// <summary>When the current pass started: films re-checked since then are done for this pass.</summary>
    public static DateTime PassCutoff(int months) => DateTime.UtcNow.AddMonths(-Math.Max(1, months));

    /// <summary>Films a day that finish one pass in the chosen interval.</summary>
    public static int PerDay(int eligible, int months) =>
        months <= 0 || eligible == 0 ? 0 : (int)Math.Ceiling(eligible / (months * 30.4));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        if (s.RescanMonths <= 0) { Jobs.Idle(Key, Title, "Off (AI page)"); return; }
        if (string.IsNullOrWhiteSpace(s.ApiKey)) { Jobs.Idle(Key, Title, "Off: no AI key yet (AI page)"); return; }

        var movies = (await _store.GetAllMoviesAsync(cancellationToken)).Where(Eligible).ToList();
        var cutoff = PassCutoff(s.RescanMonths);
        var due = movies.Where(m => m.Rescanned is null || m.Rescanned < cutoff)
            .OrderBy(m => m.Rescanned ?? DateTime.MinValue).ThenBy(m => m.Popularity).ToList();
        var perDay = PerDay(movies.Count, s.RescanMonths);
        var startOfDay = DateTime.UtcNow.Date;
        var doneToday = movies.Count(m => m.Rescanned >= startOfDay);
        var tomorrow = startOfDay.AddDays(1);

        if (due.Count == 0)
        {
            var next = movies.Where(m => m.Rescanned is not null).Select(m => m.Rescanned!.Value).DefaultIfEmpty(DateTime.UtcNow).Min().AddMonths(s.RescanMonths);
            Jobs.Idle(Key, Title, "This pass is done: every film was re-checked", next);
            return;
        }
        if (doneToday >= perDay)
        {
            Jobs.Idle(Key, Title, $"Today's {perDay} done; {due.Count:N0} to go in this pass", tomorrow);
            return;
        }
        var lookUp = WebLookup.Configured;
        if (lookUp && _web.Blocked() is { } why)
        {
            var firstOfNextMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            Jobs.Idle(Key, Title, $"Paused: {why} {due.Count:N0} to go in this pass", firstOfNextMonth, error: true);
            return;
        }

        var batch = due.Take(Math.Min(PerRound, perDay - doneToday)).ToList();
        Jobs.Running(Key, Title, $"{(lookUp ? "Looking up and describing again" : "Describing again")}: {batch.Count} films ({due.Count:N0} to go in this pass)");
        var before = batch.ToDictionary(m => m.ItemId, Tags);

        if (lookUp)
            foreach (var movie in batch)
            {
                if (_web.Blocked() is not null) break;
                try
                {
                    string? notes;
                    using (Activity.Begin($"Re-check: looking up {movie.Title} on the web"))
                        notes = await _web.FindAsync(movie, cancellationToken);
                    CountLookup();
                    await _classifier.RecordLookupAsync(movie, notes, cancellationToken);
                    if (!string.IsNullOrEmpty(notes)) movie.WebNotes = notes; // nothing new found: keep what we had
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _logger.LogWarning("Re-check: web lookup failed for {Title}: {Error}", movie.Title, error.Message);
                    break;
                }
            }

        foreach (var movie in batch) { movie.Recheck = true; movie.Rescanned = DateTime.UtcNow; }
        var provider = _ai.GetProvider();
        try
        {
            using (Activity.Begin($"Re-check: describing {batch.Count} films again: waiting for {provider.Name}"))
                await _classifier.DescribeAgainAsync(provider, batch, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning("Re-check paused: {Error}. Trying again in a few minutes.", error.Message);
            Jobs.Idle(Key, Title, $"Paused after an error: {(error.Message.Length > 160 ? error.Message[..160] + "…" : error.Message)}", DateTime.UtcNow.AddMinutes(10), error: true);
            return;
        }

        var changed = batch.Where(m => before.TryGetValue(m.ItemId, out var was) && was != Tags(m)).Select(m => m.Title).ToList();
        _logger.LogInformation("Re-check: {Count} films described again, {Changed} changed{Names}; {Left} to go in this pass",
            batch.Count, changed.Count, changed.Count > 0 ? ": " + string.Join(", ", changed.Take(8)) + (changed.Count > 8 ? ", …" : "") : "", due.Count - batch.Count);
        Jobs.Idle(Key, Title, $"{due.Count - batch.Count:N0} to go in this pass; last round changed {changed.Count} of {batch.Count}", DateTime.UtcNow.AddMinutes(5));
    }

    private static string Tags(MovieMetadata m) =>
        string.Join("|", m.SubcategorySet.Items) + "/" + string.Join("|", m.MoodSet.Items);
}
