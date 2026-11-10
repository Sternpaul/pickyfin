using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data;
using Pickyfin.Server.Jellyfin;

namespace Pickyfin.Server.Curation;

/// <summary>
/// Turns rows into private playlists owned by the user. The TV app shows them as
/// rows; other clients show them as playlists.
///
/// Jellyfin lets an API key create playlists for any user but not edit them, so a
/// changed row gets a new playlist, and the old one is deleted after the new one
/// exists (no moment without a row). Unchanged rows are left alone.
/// </summary>
public sealed class PlaylistWriter
{
    private readonly JellyfinClient _jellyfin;
    private readonly Database _database;
    private readonly ILogger<PlaylistWriter> _logger;

    public PlaylistWriter(JellyfinClient jellyfin, Database database, ILogger<PlaylistWriter> logger)
    {
        _jellyfin = jellyfin;
        _database = database;
        _logger = logger;
    }

    public async Task<int> WriteAsync(Guid userId, IReadOnlyList<PickRow> rows, CancellationToken cancellationToken)
    {
        await using var db = _database.Open();
        var existing = await db.Playlists.Where(p => p.UserId == userId).ToListAsync(cancellationToken);
        var written = 0;

        foreach (var row in rows)
        {
            var items = string.Join(",", row.Items);
            var current = existing.FirstOrDefault(p => p.Slot == row.Slot);
            if (current is not null && current.Name == row.Name && current.Items == items) continue;

            var playlistId = await _jellyfin.CreatePlaylistAsync(userId, row.Name, row.Items, cancellationToken);
            if (current is not null)
            {
                await _jellyfin.DeleteItemAsync(current.PlaylistId, cancellationToken);
                current.PlaylistId = playlistId;
                current.Name = row.Name;
                current.Items = items;
            }
            else
            {
                db.Playlists.Add(new ManagedPlaylist { UserId = userId, Slot = row.Slot, PlaylistId = playlistId, Name = row.Name, Items = items });
            }
            await db.SaveChangesAsync(cancellationToken);
            written++;
        }

        // Rows that aren't needed any more (e.g. fewer "Because You Watched" rows)
        foreach (var stale in existing.Where(p => rows.All(r => r.Slot != p.Slot)))
        {
            await _jellyfin.DeleteItemAsync(stale.PlaylistId, cancellationToken);
            db.Playlists.Remove(stale);
            await db.SaveChangesAsync(cancellationToken);
            written++;
        }

        if (written > 0) _logger.LogInformation("Wrote {Count} playlist change(s) for user {User}", written, userId);
        return written;
    }

    /// <summary>Removes everything this service wrote for a user (when they're switched off).</summary>
    public Task RemoveAllAsync(Guid userId, CancellationToken cancellationToken) => WriteAsync(userId, Array.Empty<PickRow>(), cancellationToken);
}
