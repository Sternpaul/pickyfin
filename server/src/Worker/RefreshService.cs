using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Curation;
using Pickyfin.Server.Data;
using Pickyfin.Server.Jellyfin;
using Pickyfin.Server.Library;

namespace Pickyfin.Server.Worker;

/// <summary>
/// The background loop. Every few minutes it:
/// - syncs the library (every 6 hours, or when asked)
/// - picks up new Jellyfin users
/// - refreshes a user's picks when they're due: asked for, brief changed, a new
///   finished movie, or older than the refresh interval
/// </summary>
public sealed class RefreshService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LibraryInterval = TimeSpan.FromHours(6);

    private readonly JellyfinClient _jellyfin;
    private readonly Database _database;
    private readonly LibrarySync _library;
    private readonly BriefInterpreter _briefs;
    private readonly Curator _curator;
    private readonly PlaylistWriter _writer;
    private readonly Letterboxd.LetterboxdSync _letterboxd;
    private readonly ILogger<RefreshService> _logger;
    private readonly SemaphoreSlim _wake = new(0);
    private bool _librarySyncRequested = true;

    public RefreshService(JellyfinClient jellyfin, Database database, LibrarySync library, BriefInterpreter briefs,
        Curator curator, PlaylistWriter writer, Letterboxd.LetterboxdSync letterboxd, ILogger<RefreshService> logger)
    {
        _jellyfin = jellyfin;
        _database = database;
        _library = library;
        _briefs = briefs;
        _curator = curator;
        _writer = writer;
        _letterboxd = letterboxd;
        _logger = logger;
    }

    public string State { get; private set; } = "Starting";
    public string? LastError { get; private set; }

    /// <summary>Run the loop now instead of waiting for the next tick.</summary>
    public void Wake(bool syncLibrary = false)
    {
        if (syncLibrary) _librarySyncRequested = true;
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LogStartupAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                LastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                LastError = error.Message;
                _logger.LogError("Refresh failed: {Error}", error.Message);
            }
            if (_jellyfin.IsConfigured) State = "Idle";
            await _wake.WaitAsync(Tick, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!_jellyfin.IsConfigured)
        {
            State = "Waiting for the Jellyfin address and API key";
            return;
        }

        if (_librarySyncRequested || _library.LastSync is null || DateTime.UtcNow - _library.LastSync > LibraryInterval)
        {
            State = "Syncing and classifying the library";
            _librarySyncRequested = false;
            var started = DateTime.UtcNow;
            _logger.LogInformation("Library sync started");
            await _library.SyncAsync(cancellationToken);
            _logger.LogInformation("Library sync finished in {Duration}", Pretty(DateTime.UtcNow - started));

            // New movies may now match someone's Letterboxd films
            await using var db = _database.Open();
            foreach (var userId in await db.Letterboxd.Select(f => f.UserId).Distinct().ToListAsync(cancellationToken))
                await _letterboxd.MatchAsync(userId, cancellationToken);
        }

        State = "Checking users";
        await SyncUsersAsync(cancellationToken);

        List<UserProfile> users;
        await using (var db = _database.Open())
            users = await db.Users.AsNoTracking().OrderBy(u => u.Name).ToListAsync(cancellationToken);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!user.Enabled) continue;
            try
            {
                await RefreshIfDueAsync(user, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogError("Picks for {User} failed: {Error}", user.Name, error.Message);
                await UpdateAsync(user.UserId, u => u.LastResult = "Failed: " + error.Message, cancellationToken);
            }
        }
    }

    /// <summary>One block at start-up saying what this server runs with, and whether it can reach Jellyfin.</summary>
    private async Task LogStartupAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        var build = Environment.GetEnvironmentVariable("PICKYFIN_VERSION") ?? "dev";
        _logger.LogInformation("Pickyfin Server {Version} starting", build.Length == 40 ? build[..7] : build);

        if (!_jellyfin.IsConfigured)
        {
            _logger.LogWarning("Jellyfin: not configured. Set JELLYFIN_URL and JELLYFIN_API_KEY on the container.");
        }
        else
        {
            try
            {
                var (name, version) = await _jellyfin.GetServerInfoAsync(cancellationToken);
                _logger.LogInformation("Jellyfin: connected to \"{Name}\" ({Version}) at {Url}", name, version, s.JellyfinUrl);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogError("Jellyfin: can't reach {Url}: {Error}", s.JellyfinUrl, error.Message);
            }
        }

        _logger.LogInformation(string.IsNullOrWhiteSpace(s.ApiKey)
            ? "AI: no key set yet (Settings → AI)"
            : $"AI: {s.AIProvider}, picking with {s.ChatModel}, tagging with {s.ClassificationModel}");
        _logger.LogInformation("TMDB keywords: {State}", string.IsNullOrWhiteSpace(s.TmdbApiKey) ? "off (no key)" : "on");
        _logger.LogInformation("Rows: Recommended for you + {Because} Because You Watched, {Picks} movies each, fresh at least every {Hours} h; {Excluded} librar{Plural} switched off",
            s.BecauseRows, s.PicksPerRow, s.RefreshHours, s.ExcludedLibraries.Count, s.ExcludedLibraries.Count == 1 ? "y" : "ies");

        await using var db = _database.Open();
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var on = users.Where(u => u.Enabled).Select(u => u.Name).OrderBy(n => n).ToList();
        _logger.LogInformation("Users: {On} of {Total} switched on{Names}", on.Count, users.Count, on.Count > 0 ? ": " + string.Join(", ", on) : "");
    }

    private async Task SyncUsersAsync(CancellationToken cancellationToken)
    {
        var jellyfinUsers = await _jellyfin.GetUsersAsync(cancellationToken);
        await using var db = _database.Open();
        var known = await db.Users.ToDictionaryAsync(u => u.UserId, cancellationToken);
        foreach (var user in jellyfinUsers)
        {
            if (known.TryGetValue(user.Id, out var profile))
            {
                profile.Name = user.Name;
            }
            else
            {
                // Off until the admin switches them on: no playlists, no AI cost
                db.Users.Add(new UserProfile { UserId = user.Id, Name = user.Name, Enabled = false });
                _logger.LogInformation("New user {User}", user.Name);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshIfDueAsync(UserProfile user, CancellationToken cancellationToken)
    {
        // Letterboxd: feed and links twice a day; new films mean fresh picks
        var letterboxdNews = false;
        if (user.UseLetterboxd && Letterboxd.LetterboxdSync.HasOnlineSource(user)
            && (user.LetterboxdSynced is null || DateTime.UtcNow - user.LetterboxdSynced > TimeSpan.FromHours(12)))
        {
            State = $"Reading Letterboxd for {user.Name}";
            letterboxdNews = await _letterboxd.SyncAsync(user, cancellationToken) > 0;
        }

        var lastPlayed = await _jellyfin.GetLastPlayedAsync(user.UserId, cancellationToken);
        var briefChanged = (user.Brief ?? "") != (user.BriefProfileFor ?? "");
        var newWatch = lastPlayed is not null && lastPlayed != user.LastSeenPlay;
        var stale = user.LastRefresh is null || DateTime.UtcNow - user.LastRefresh > TimeSpan.FromHours(Math.Max(1, ServerSettings.Current.RefreshHours));
        if (!user.RefreshRequested && !briefChanged && !newWatch && !stale && !letterboxdNews) return;

        State = $"Picking for {user.Name}";
        _logger.LogInformation("Refreshing picks for {User} ({Reason})", user.Name,
            user.RefreshRequested ? "asked" : briefChanged ? "brief changed" : newWatch ? "new watch" : letterboxdNews ? "new on Letterboxd" : "scheduled");

        var profile = BriefProfile.FromJson(user.BriefProfile);
        if (briefChanged)
        {
            profile = string.IsNullOrWhiteSpace(user.Brief) ? null : await _briefs.InterpretAsync(user.Brief, cancellationToken);
            await UpdateAsync(user.UserId, u =>
            {
                u.BriefProfile = profile?.ToJson();
                u.BriefProfileFor = user.Brief ?? "";
            }, cancellationToken);
        }

        var result = await _curator.CurateAsync(user, profile, cancellationToken);
        await _writer.WriteAsync(user.UserId, result.Rows, cancellationToken);

        await UpdateAsync(user.UserId, u =>
        {
            u.LastRefresh = DateTime.UtcNow;
            u.LastSeenPlay = lastPlayed;
            u.RefreshRequested = false;
            u.LastResult = result.Summary;
        }, cancellationToken);
        _logger.LogInformation("Picks for {User}: {Summary}", user.Name, result.Summary);
    }

    private static string Pretty(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.Seconds} s";

    public async Task SwitchOffAsync(Guid userId, CancellationToken cancellationToken) => await _writer.RemoveAllAsync(userId, cancellationToken);

    private async Task UpdateAsync(Guid userId, Action<UserProfile> change, CancellationToken cancellationToken)
    {
        await using var db = _database.Open();
        var user = await db.Users.FindAsync(new object[] { userId }, cancellationToken);
        if (user is null) return;
        change(user);
        await db.SaveChangesAsync(cancellationToken);
    }
}
