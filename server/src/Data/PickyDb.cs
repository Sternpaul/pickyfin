using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Data;

/// <summary>Per-user settings the admin edits on the page.</summary>
public sealed class UserProfile
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }

    /// <summary>What the admin knows about this person's taste, in plain words.</summary>
    public string Brief { get; set; } = "";
    /// <summary>The brief translated into the classification vocabulary (JSON), refreshed when the brief changes.</summary>
    public string? BriefProfile { get; set; }
    public string? BriefProfileFor { get; set; }

    /// <summary>
    /// Only watches on or after this date count. Null = count all history. Shared
    /// devices skew old history, so the admin can start counting from "now".
    /// </summary>
    public DateTime? CountHistoryFrom { get; set; }

    public DateTime? LastRefresh { get; set; }
    /// <summary>Last seen "most recent watch", to notice new watches cheaply.</summary>
    public DateTime? LastSeenPlay { get; set; }
    public bool RefreshRequested { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>A playlist this service wrote for a user.</summary>
public sealed class ManagedPlaylist
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>"recommended" or "because-1", "because-2", ...</summary>
    public string Slot { get; set; } = "";
    public Guid PlaylistId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Item ids in order, comma separated, to skip rewrites when nothing changed.</summary>
    public string Items { get; set; } = "";
}

public sealed class PickyDb : DbContext
{
    private readonly string _path;

    public PickyDb(string path) => _path = path;

    public DbSet<MovieMetadata> Movies => Set<MovieMetadata>();
    public DbSet<UserProfile> Users => Set<UserProfile>();
    public DbSet<ManagedPlaylist> Playlists => Set<ManagedPlaylist>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite($"Data Source={_path}");

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MovieMetadata>().HasKey(m => m.ItemId);
        model.Entity<UserProfile>().HasKey(u => u.UserId);
        model.Entity<ManagedPlaylist>().HasIndex(p => new { p.UserId, p.Slot }).IsUnique();
    }
}

/// <summary>Opens short-lived contexts on the one database file.</summary>
public sealed class Database
{
    private readonly string _path;

    public Database(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "pickyfin.db");
        using var db = Open();
        db.Database.EnsureCreated();
    }

    public PickyDb Open() => new(_path);
}
