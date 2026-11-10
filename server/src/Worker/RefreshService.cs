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
    private readonly ILogger<RefreshService> _logger;
    private readonly SemaphoreSlim _wake = new(0);
    private bool _librarySyncRequested = true;

    public RefreshService(JellyfinClient jellyfin, Database database, LibrarySync library, BriefInterpreter briefs,
        Curator curator, PlaylistWriter writer, ILogger<RefreshService> logger)
    {
        _jellyfin = jellyfin;
        _database = database;
        _library = library;
        _briefs = briefs;
        _curator = curator;
        _writer = writer;
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
            await _library.SyncAsync(cancellationToken);
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
        var lastPlayed = await _jellyfin.GetLastPlayedAsync(user.UserId, cancellationToken);
        var briefChanged = (user.Brief ?? "") != (user.BriefProfileFor ?? "");
        var newWatch = lastPlayed is not null && lastPlayed != user.LastSeenPlay;
        var stale = user.LastRefresh is null || DateTime.UtcNow - user.LastRefresh > TimeSpan.FromHours(Math.Max(1, ServerSettings.Current.RefreshHours));
        if (!user.RefreshRequested && !briefChanged && !newWatch && !stale) return;

        State = $"Picking for {user.Name}";
        _logger.LogInformation("Refreshing picks for {User} ({Reason})", user.Name,
            user.RefreshRequested ? "asked" : briefChanged ? "brief changed" : newWatch ? "new watch" : "scheduled");

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
