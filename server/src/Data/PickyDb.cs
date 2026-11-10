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

    // Letterboxd: any combination of sources
    public bool UseLetterboxd { get; set; } = true;
    /// <summary>Public diary feed (recent watches and ratings), fetched daily.</summary>
    public string? LetterboxdUsername { get; set; }
    /// <summary>JSON links as used by the AI Movie Recommender plugin.</summary>
    public string? LetterboxdWatchlistUrl { get; set; }
    public string? LetterboxdRatingsUrl { get; set; }
    public DateTime? LetterboxdSynced { get; set; }
    public string? LetterboxdStatus { get; set; }

    public DateTime? LastRefresh { get; set; }
    /// <summary>Last seen "most recent watch", to notice new watches cheaply.</summary>
    public DateTime? LastSeenPlay { get; set; }
    public bool RefreshRequested { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>A film from someone's Letterboxd: watched (maybe rated) or on their watchlist.</summary>
public sealed class LetterboxdFilm
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>"watched" or "watchlist".</summary>
    public string Kind { get; set; } = "";
    /// <summary>"feed" (diary feed), "export" (uploaded export) or "link" (JSON link).</summary>
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
    /// <summary>0.5 to 5 stars; null when not rated.</summary>
    public double? Rating { get; set; }
    public DateTime? Date { get; set; }
    /// <summary>The library movie it was matched to, if any.</summary>
    public Guid? ItemId { get; set; }
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
    public DbSet<LetterboxdFilm> Letterboxd => Set<LetterboxdFilm>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite($"Data Source={_path}");

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MovieMetadata>().HasKey(m => m.ItemId);
        model.Entity<UserProfile>().HasKey(u => u.UserId);
        model.Entity<ManagedPlaylist>().HasIndex(p => new { p.UserId, p.Slot }).IsUnique();
        model.Entity<LetterboxdFilm>().HasIndex(f => new { f.UserId, f.Kind });
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
        Upgrade(db);
    }

    /// <summary>
    /// Brings a database from an older version up to date: EnsureCreated only builds a
    /// new database, so columns and tables added later are added here.
    /// </summary>
    private static void Upgrade(PickyDb db)
    {
        var connection = db.Database.GetDbConnection();
        connection.Open();

        HashSet<string> Columns(string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = command.ExecuteReader();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) names.Add(reader.GetString(1));
            return names;
        }

        void Run(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        void AddColumn(string table, string column, string definition)
        {
            if (!Columns(table).Contains(column)) Run($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
        }

        AddColumn("Movies", "ManuallyEdited", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("Users", "UseLetterboxd", "INTEGER NOT NULL DEFAULT 1");
        AddColumn("Users", "LetterboxdUsername", "TEXT NULL");
        AddColumn("Users", "LetterboxdWatchlistUrl", "TEXT NULL");
        AddColumn("Users", "LetterboxdRatingsUrl", "TEXT NULL");
        AddColumn("Users", "LetterboxdSynced", "TEXT NULL");
        AddColumn("Users", "LetterboxdStatus", "TEXT NULL");

        Run(@"CREATE TABLE IF NOT EXISTS ""Letterboxd"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Letterboxd"" PRIMARY KEY AUTOINCREMENT,
            ""UserId"" TEXT NOT NULL,
            ""Kind"" TEXT NOT NULL,
            ""Source"" TEXT NOT NULL,
            ""Title"" TEXT NOT NULL,
            ""Year"" INTEGER NULL,
            ""ImdbId"" TEXT NULL,
            ""TmdbId"" INTEGER NULL,
            ""Rating"" REAL NULL,
            ""Date"" TEXT NULL,
            ""ItemId"" TEXT NULL)");
        Run(@"CREATE INDEX IF NOT EXISTS ""IX_Letterboxd_UserId_Kind"" ON ""Letterboxd"" (""UserId"", ""Kind"")");
    }

    public PickyDb Open() => new(_path);
}
