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
    /// <summary>Last seen "most recent episode", and the series levels then (SeriesTaste.Signature): a series moving up means fresh picks.</summary>
    public DateTime? LastSeenEpisode { get; set; }
    public string? SeriesLevels { get; set; }
    public bool RefreshRequested { get; set; }
    public string? LastResult { get; set; }

    /// <summary>Movies the admin took out of this person's rows: never recommended to them again. Comma separated ids.</summary>
    public string HiddenItems { get; set; } = "";

    public HashSet<Guid> HiddenIds() => Ids.Parse(HiddenItems).ToHashSet();
}

/// <summary>Movies the admin put into one of a person's rows; they stay there (first) until watched.</summary>
public sealed class RowPin
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>"recommended", "because-1", "because-2".</summary>
    public string Slot { get; set; } = "";
    /// <summary>Item ids in order, comma separated.</summary>
    public string Items { get; set; } = "";
}

public static class Ids
{
    public static List<Guid> Parse(string? list) => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    public static string Join(IEnumerable<Guid> ids) => string.Join(",", ids.Distinct());
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
    public DbSet<RowPin> Pins => Set<RowPin>();
    public DbSet<Library.HistoryEntry> History => Set<Library.HistoryEntry>();
    public DbSet<Library.Embedding> Embeddings => Set<Library.Embedding>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite($"Data Source={_path}");

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MovieMetadata>().HasKey(m => m.ItemId);
        // Films Jellyfin no longer has stay in the database until the admin confirms, but nothing sees them
        model.Entity<MovieMetadata>().HasQueryFilter(m => m.GoneSince == null);
        model.Entity<UserProfile>().HasKey(u => u.UserId);
        model.Entity<ManagedPlaylist>().HasIndex(p => new { p.UserId, p.Slot }).IsUnique();
        model.Entity<LetterboxdFilm>().HasIndex(f => new { f.UserId, f.Kind });
        model.Entity<RowPin>().HasIndex(r => new { r.UserId, r.Slot }).IsUnique();
        model.Entity<Library.HistoryEntry>().ToTable("History").HasIndex(h => h.ItemId);
        model.Entity<Library.Embedding>().ToTable("Embeddings").HasKey(e => new { e.ItemId, e.Model });
    }
}

/// <summary>Opens short-lived contexts on the one database file.</summary>
public sealed class Database
{
    private readonly string _path;
    /// <summary>The data folder the database lives in.</summary>
    public string Folder => Path.GetDirectoryName(_path)!;

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

        int KeyColumns(string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE pk > 0";
            return Convert.ToInt32(command.ExecuteScalar());
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
        AddColumn("Movies", "Genres", "TEXT NULL");
        AddColumn("Movies", "OriginalTitle", "TEXT NULL");
        AddColumn("Movies", "Countries", "TEXT NULL");
        AddColumn("Movies", "Issue", "TEXT NULL");
        AddColumn("Movies", "Checked", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("Movies", "WebNotes", "TEXT NULL");
        AddColumn("Movies", "Verified", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("Movies", "Rescanned", "TEXT NULL");
        AddColumn("Movies", "RedoDescribe", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("Movies", "CriticsScore", "INTEGER NULL");
        AddColumn("Movies", "AudienceScore", "INTEGER NULL");
        AddColumn("Movies", "ScoreSource", "TEXT NULL");
        AddColumn("Movies", "RtScore", "INTEGER NULL");
        AddColumn("Movies", "Metascore", "INTEGER NULL");
        AddColumn("Movies", "ImdbRating", "REAL NULL");
        AddColumn("Movies", "ScoresFetched", "TEXT NULL");
        AddColumn("Movies", "Minutes", "INTEGER NULL");
        AddColumn("Movies", "Awards", "TEXT NULL");
        AddColumn("Movies", "OscarWins", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("Movies", "OscarNominations", "INTEGER NOT NULL DEFAULT 0");
        AddColumn("Movies", "CollectionId", "INTEGER NULL");
        AddColumn("Movies", "CollectionName", "TEXT NULL");
        AddColumn("Movies", "TmdbRecs", "TEXT NULL");
        AddColumn("Movies", "DetailsFetched", "TEXT NULL");
        AddColumn("Movies", "Pacing", "TEXT NULL");
        AddColumn("Movies", "Ending", "TEXT NULL");
        AddColumn("Movies", "Occasions", "TEXT NULL");
        AddColumn("Movies", "GoneSince", "TEXT NULL");
        AddColumn("Movies", "LbRating", "REAL NULL");
        AddColumn("Movies", "LbCount", "INTEGER NULL");
        AddColumn("Movies", "LbUrl", "TEXT NULL");
        AddColumn("Movies", "LbHistogram", "TEXT NULL");
        AddColumn("Movies", "LbReviews", "TEXT NULL");
        AddColumn("Movies", "LbFetched", "TEXT NULL");
        AddColumn("Movies", "ContentNotes", "TEXT NULL");
        AddColumn("Movies", "BasedOn", "TEXT NULL");
        AddColumn("Movies", "SettingEra", "TEXT NULL");
        AddColumn("Movies", "SettingPlace", "TEXT NULL");
        AddColumn("Movies", "Kind", "TEXT NOT NULL DEFAULT 'movie'");
        AddColumn("Movies", "Seasons", "INTEGER NULL");
        AddColumn("Movies", "Episodes", "INTEGER NULL");
        AddColumn("Movies", "EndYear", "INTEGER NULL");
        AddColumn("Movies", "Status", "TEXT NULL");
        AddColumn("Movies", "Network", "TEXT NULL");
        AddColumn("Movies", "Format", "TEXT NULL");
        AddColumn("Movies", "LastWatched", "TEXT NULL");
        AddColumn("Movies", "SeriesKinds", "TEXT NULL");
        AddColumn("Movies", "FirstRecheckDue", "TEXT NULL");
        AddColumn("Users", "UseLetterboxd", "INTEGER NOT NULL DEFAULT 1");
        AddColumn("Users", "LetterboxdUsername", "TEXT NULL");
        AddColumn("Users", "LetterboxdWatchlistUrl", "TEXT NULL");
        AddColumn("Users", "LetterboxdRatingsUrl", "TEXT NULL");
        AddColumn("Users", "LetterboxdSynced", "TEXT NULL");
        AddColumn("Users", "LetterboxdStatus", "TEXT NULL");
        AddColumn("Users", "HiddenItems", "TEXT NOT NULL DEFAULT ''");
        AddColumn("Users", "LastSeenEpisode", "TEXT NULL");
        AddColumn("Users", "SeriesLevels", "TEXT NULL");

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
        Run(@"CREATE TABLE IF NOT EXISTS ""Pins"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Pins"" PRIMARY KEY AUTOINCREMENT,
            ""UserId"" TEXT NOT NULL,
            ""Slot"" TEXT NOT NULL,
            ""Items"" TEXT NOT NULL)");
        Run(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Pins_UserId_Slot"" ON ""Pins"" (""UserId"", ""Slot"")");
        Run(@"CREATE TABLE IF NOT EXISTS ""History"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_History"" PRIMARY KEY AUTOINCREMENT,
            ""ItemId"" TEXT NOT NULL,
            ""At"" TEXT NOT NULL,
            ""Step"" TEXT NOT NULL,
            ""Model"" TEXT NULL,
            ""Before"" TEXT NULL,
            ""After"" TEXT NULL,
            ""Text"" TEXT NULL)");
        Run(@"CREATE INDEX IF NOT EXISTS ""IX_History_ItemId"" ON ""History"" (""ItemId"")");
        Run(@"CREATE TABLE IF NOT EXISTS ""Embeddings"" (
            ""ItemId"" TEXT NOT NULL,
            ""Kind"" TEXT NOT NULL,
            ""Model"" TEXT NOT NULL,
            ""Hash"" TEXT NOT NULL,
            ""Vector"" BLOB NOT NULL,
            ""At"" TEXT NOT NULL,
            CONSTRAINT ""PK_Embeddings"" PRIMARY KEY (""ItemId"", ""Model""))");
        // Before: one meaning per film. Now one per film per model, so every model builds its own matrix
        if (KeyColumns("Embeddings") == 1)
        {
            Run(@"ALTER TABLE ""Embeddings"" RENAME TO ""EmbeddingsOld""");
            Run(@"CREATE TABLE ""Embeddings"" (
                ""ItemId"" TEXT NOT NULL,
                ""Kind"" TEXT NOT NULL,
                ""Model"" TEXT NOT NULL,
                ""Hash"" TEXT NOT NULL,
                ""Vector"" BLOB NOT NULL,
                ""At"" TEXT NOT NULL,
                CONSTRAINT ""PK_Embeddings"" PRIMARY KEY (""ItemId"", ""Model""))");
            Run(@"INSERT INTO ""Embeddings"" SELECT ""ItemId"", ""Kind"", ""Model"", ""Hash"", ""Vector"", ""At"" FROM ""EmbeddingsOld""");
            Run(@"DROP TABLE ""EmbeddingsOld""");
        }
    }

    public PickyDb Open() => new(_path);
}
