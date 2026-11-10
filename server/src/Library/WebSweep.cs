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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        if (s.WebLookupsPerDay <= 0 || string.IsNullOrWhiteSpace(s.ApiKey) || _web.Blocked() is not null) return;
        var left = s.WebLookupsPerDay - _web.ThisMonth.Today;
        if (left <= 0) return;

        var todo = (await _store.GetAllMoviesAsync(cancellationToken)).Where(Wanted).ToList();
        if (todo.Count == 0) return;
        var batch = todo
            .OrderByDescending(m => m.Issue is not null)
            .ThenBy(m => m.Popularity)
            .Take(Math.Min(PerRound, left))
            .ToList();

        var looked = await _classifier.LookUpAndTagManyAsync(_ai.GetProvider(), batch, cancellationToken);
        var remaining = todo.Count - looked;
        var days = (int)Math.Ceiling(remaining / (double)Math.Max(1, s.WebLookupsPerDay));
        _logger.LogInformation("Library lookup: {Looked} looked up, {Today} today, {Remaining} to go (about {Days} day(s))",
            looked, _web.ThisMonth.Today, remaining, days);
    }
}
