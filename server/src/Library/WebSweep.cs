using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;

namespace Pickyfin.Server.Library;

/// <summary>
/// Slowly looks up the whole library on the web, a few films every few minutes, up to
/// the daily number set on the AI page (and never past the monthly limit). Flagged
/// films go first, then the least known ones, since those are where the AI guesses most.
/// </summary>
public sealed class WebSweep
{
    private const int PerRound = 10;

    private readonly MovieStore _store;
    private readonly MovieClassifier _classifier;
    private readonly AIProviderFactory _ai;
    private readonly WebLookup _web;
    private readonly ILogger<WebSweep> _logger;

    public WebSweep(MovieStore store, MovieClassifier classifier, AIProviderFactory ai, WebLookup web, ILogger<WebSweep> logger)
    {
        _store = store;
        _classifier = classifier;
        _ai = ai;
        _web = web;
        _logger = logger;
    }

    public static bool Wanted(Data.Models.MovieMetadata m) =>
        m.IsClassified && m.WebNotes is null && !m.ManuallyEdited && !m.Verified && !ServerSettings.Current.IsLibraryExcluded(m.Libraries);

    /// <summary>Step 3 comes after step 2: with the second opinion on, a film waits until it has been checked (flagged films can go any time).</summary>
    public static bool Ready(Data.Models.MovieMetadata m) =>
        !ServerSettings.Current.CheckEnabled || m.Checked || m.Issue is not null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        const string key = "sweep", title = "Looking up the whole library";
        var s = ServerSettings.Current;
        if (s.WebLookupsPerDay <= 0 || string.IsNullOrWhiteSpace(s.ApiKey)) { Jobs.Idle(key, title, "Off"); return; }
        if (_web.Blocked() is { } why) { Jobs.Idle(key, title, WebLookup.Configured ? "Paused: monthly Exa limit reached" : "Off: no Exa key", null, WebLookup.Configured); return; }
        var left = s.WebLookupsPerDay - (_web.ThisMonth.Today - Rescan.LookupsToday);
        var midnight = DateTime.Today.AddDays(1).ToUniversalTime();
        if (left <= 0) { Jobs.Idle(key, title, $"Today's {s.WebLookupsPerDay} done", midnight); return; }

        var todo = (await _store.GetAllMoviesAsync(cancellationToken)).Where(Wanted).ToList();
        if (todo.Count == 0) { Jobs.Idle(key, title, "Up to date"); return; }
        var ready = todo.Where(Ready).ToList();
        if (ready.Count == 0) { Jobs.Idle(key, title, "Waiting for step 2 to check films", DateTime.UtcNow.AddMinutes(5)); return; }
        Jobs.Running(key, title, $"{Math.Min(PerRound, left)} films");
        var batch = ready
            .OrderByDescending(m => m.Issue is not null)
            .ThenBy(m => m.Popularity)
            .Take(Math.Min(PerRound, left))
            .ToList();

        var looked = await _classifier.LookUpAndTagManyAsync(_ai.GetProvider(), batch, cancellationToken);
        var remaining = todo.Count - looked;
        var days = (int)Math.Ceiling(remaining / (double)Math.Max(1, s.WebLookupsPerDay));
        _logger.LogInformation("Library lookup: {Looked} looked up, {Today} today, {Remaining} to go (about {Days} day(s))",
            looked, _web.ThisMonth.Today, remaining, days);
        Jobs.Idle(key, title, $"Today {_web.ThisMonth.Today - Rescan.LookupsToday} of {s.WebLookupsPerDay}",
            _web.ThisMonth.Today >= s.WebLookupsPerDay ? midnight : DateTime.UtcNow.AddMinutes(5));
    }
}
