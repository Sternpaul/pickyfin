using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Letterboxd;

/// <summary>
/// Brings in a person's Letterboxd films from two sources:
/// - "feed": their public diary feed (letterboxd.com/USER/rss), recent watches with
///   ratings and TMDB ids; fetched twice a day and merged, so it grows over time
/// - "export": the ZIP from letterboxd.com → Settings → Data (all ratings, watched
///   films and the watchlist); replaced on each upload
/// Each film is then matched to a movie in the library, if there is one.
/// </summary>
public sealed class LetterboxdSync
{
    private static readonly XNamespace Lb = "https://letterboxd.com";
    private static readonly XNamespace Tmdb = "https://themoviedb.org";

    private readonly HttpClient _http;
    private readonly Database _database;
    private readonly MovieStore _store;
    private readonly ILogger<LetterboxdSync> _logger;

    public LetterboxdSync(HttpClient http, Database database, MovieStore store, ILogger<LetterboxdSync> logger)
    {
        _http = http;
        _database = database;
        _store = store;
        _logger = logger;
    }

    public static bool HasOnlineSource(UserProfile u) => !string.IsNullOrWhiteSpace(u.LetterboxdUsername);

    /// <summary>"dave", "@dave" or "https://letterboxd.com/dave/films/" all become "dave".</summary>
    public static string? CleanUsername(string? input)
    {
        var name = (input ?? "").Trim();
        var at = name.IndexOf("letterboxd.com/", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) name = name[(at + 15)..];
        name = name.Trim('/', '@', ' ').Split('/')[0];
        return name.Length == 0 ? null : name;
    }

    /// <summary>Fetches the feed and links, re-matches everything. Returns how many films are new.</summary>
    public async Task<int> SyncAsync(UserProfile user, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var added = 0;

        if (!string.IsNullOrWhiteSpace(user.LetterboxdUsername))
        {
            try { added += await MergeFeedAsync(user, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { problems.Add("diary feed: " + error.Message); }
        }

        var (watched, watchlist, matched) = await MatchAsync(user.UserId, cancellationToken);
        var status = problems.Count > 0
            ? "Problem with " + string.Join("; ", problems)
            : $"{watched} watched, {watchlist} on the watchlist; {matched} found in the library";

        await using (var db = _database.Open())
        {
            var row = await db.Users.FindAsync(new object[] { user.UserId }, cancellationToken);
            if (row is not null)
            {
                row.LetterboxdSynced = DateTime.UtcNow;
                row.LetterboxdStatus = status;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        _logger.LogInformation("Letterboxd for {User}: {Status}", user.Name, status);
        return added;
    }

    // ---------- diary feed ----------

    private async Task<int> MergeFeedAsync(UserProfile user, CancellationToken cancellationToken)
    {
        var name = CleanUsername(user.LetterboxdUsername)!;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://letterboxd.com/{Uri.EscapeDataString(name)}/rss/");
        request.Headers.UserAgent.ParseAdd("PickyfinServer/1.0 (+https://github.com/Sternpaul/pickyfin)");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException($"no Letterboxd user \"{name}\"");
        response.EnsureSuccessStatusCode();
        var films = ParseFeed(await response.Content.ReadAsStringAsync(cancellationToken), user.UserId);

        // Merge: the feed only shows recent entries, so keep what we saw before
        await using var db = _database.Open();
        var known = await db.Letterboxd.Where(f => f.UserId == user.UserId && f.Source == "feed").ToListAsync(cancellationToken);
        var added = 0;
        foreach (var film in films)
        {
            var existing = known.FirstOrDefault(k => SameFilm(k, film));
            if (existing is null)
            {
                db.Letterboxd.Add(film);
                known.Add(film);
                added++;
            }
            else
            {
                // Newest diary entry wins (e.g. a rewatch with a new rating)
                if (film.Date >= existing.Date || existing.Date is null)
                {
                    existing.Date = film.Date;
                    if (film.Rating is not null) existing.Rating = film.Rating;
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>Films from a diary feed; lists and other posts are skipped.</summary>
    internal static List<LetterboxdFilm> ParseFeed(string xmlText, Guid userId)
    {
        var xml = XDocument.Parse(xmlText);
        var films = new List<LetterboxdFilm>();
        foreach (var item in xml.Descendants("item"))
        {
            var title = item.Element(Lb + "filmTitle")?.Value;
            if (string.IsNullOrWhiteSpace(title)) continue; // lists and other posts
            films.Add(new LetterboxdFilm
            {
                UserId = userId,
                Kind = "watched",
                Source = "feed",
                Title = title,
                Year = int.TryParse(item.Element(Lb + "filmYear")?.Value, out var y) ? y : null,
                TmdbId = int.TryParse(item.Element(Tmdb + "movieId")?.Value, out var t) ? t : null,
                Rating = double.TryParse(item.Element(Lb + "memberRating")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : null,
                Date = DateTime.TryParse(item.Element(Lb + "watchedDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null,
            });
        }
        return films;
    }

    private static bool SameFilm(LetterboxdFilm a, LetterboxdFilm b) =>
        a.TmdbId is not null && b.TmdbId is not null
            ? a.TmdbId == b.TmdbId
            : Normalize(a.Title) == Normalize(b.Title) && a.Year == b.Year;

    // ---------- export upload ----------

    /// <summary>Imports the Letterboxd export (the ZIP, or a single CSV). Replaces the previous upload.</summary>
    public async Task<string> ImportExportAsync(UserProfile user, Stream upload, string fileName, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/')))
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                files[entry.Name] = await reader.ReadToEndAsync(cancellationToken);
            }
        }
        else
        {
            using var reader = new StreamReader(buffer, Encoding.UTF8);
            files[Path.GetFileName(fileName)] = await reader.ReadToEndAsync(cancellationToken);
        }

        // watched.csv: everything seen; ratings.csv: stars; diary.csv: dates of watching
        var watched = new Dictionary<string, LetterboxdFilm>();
        LetterboxdFilm Watched(Dictionary<string, string> row)
        {
            var key = row.GetValueOrDefault("Letterboxd URI") ?? $"{row.GetValueOrDefault("Name")}|{row.GetValueOrDefault("Year")}";
            if (!watched.TryGetValue(key, out var film))
                watched[key] = film = new LetterboxdFilm { Kind = "watched", Title = row.GetValueOrDefault("Name") ?? "", Year = int.TryParse(row.GetValueOrDefault("Year"), out var y) ? y : null };
            return film;
        }
        foreach (var row in Rows(files.GetValueOrDefault("watched.csv"))) { var f = Watched(row); f.Date ??= ParseDate(row.GetValueOrDefault("Date")); }
        foreach (var row in Rows(files.GetValueOrDefault("diary.csv")))
        {
            var f = Watched(row);
            var date = ParseDate(row.GetValueOrDefault("Watched Date")) ?? ParseDate(row.GetValueOrDefault("Date"));
            if (date > f.Date || f.Date is null) f.Date = date;
        }
        foreach (var row in Rows(files.GetValueOrDefault("ratings.csv")))
            Watched(row).Rating = double.TryParse(row.GetValueOrDefault("Rating"), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : null;

        var watchlist = Rows(files.GetValueOrDefault("watchlist.csv"))
            .Select(row => new LetterboxdFilm { Kind = "watchlist", Title = row.GetValueOrDefault("Name") ?? "", Year = int.TryParse(row.GetValueOrDefault("Year"), out var y) ? y : null, Date = ParseDate(row.GetValueOrDefault("Date")) })
            .Where(f => f.Title.Length > 0)
            .ToList();

        if (watched.Count == 0 && watchlist.Count == 0)
            throw new InvalidOperationException("No Letterboxd films found. Upload the ZIP from letterboxd.com → Settings → Data → Export your data.");

        // A single uploaded CSV only replaces its own kind
        var hasWatched = files.Keys.Any(k => k is "watched.csv" or "ratings.csv" or "diary.csv");
        var hasWatchlist = files.ContainsKey("watchlist.csv");
        if (hasWatched) await ReplaceAsync(user.UserId, "export", "watched", watched.Values.Where(f => f.Title.Length > 0).ToList(), cancellationToken);
        if (hasWatchlist) await ReplaceAsync(user.UserId, "export", "watchlist", watchlist, cancellationToken);

        var (w, l, matched) = await MatchAsync(user.UserId, cancellationToken);
        var status = $"{w} watched, {l} on the watchlist; {matched} found in the library";
        await using (var db = _database.Open())
        {
            var row = await db.Users.FindAsync(new object[] { user.UserId }, cancellationToken);
            if (row is not null) { row.LetterboxdSynced = DateTime.UtcNow; row.LetterboxdStatus = status; row.RefreshRequested = true; await db.SaveChangesAsync(cancellationToken); }
        }
        _logger.LogInformation("Letterboxd export for {User}: {Watched} watched, {Watchlist} on the watchlist", user.Name, watched.Count, watchlist.Count);
        return status;
    }

    private static DateTime? ParseDate(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>Reads a CSV with a header row; handles quoted fields with commas and quotes.</summary>
    internal static List<Dictionary<string, string>> Rows(string? csv)
    {
        var result = new List<Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(csv)) return result;
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear();
                if (record.Any(f => f.Length > 0)) records.Add(record);
                record = new List<string>();
            }
            else field.Append(c);
        }
        record.Add(field.ToString());
        if (record.Any(f => f.Length > 0)) records.Add(record);
        if (records.Count < 2) return result;

        var header = records[0].Select(h => h.Trim().TrimStart('﻿')).ToList();
        foreach (var r in records.Skip(1))
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < r.Count; i++) row[header[i]] = r[i].Trim();
            result.Add(row);
        }
        return result;
    }

    // ---------- storing and matching ----------

    private async Task<int> ReplaceAsync(Guid userId, string source, string kind, List<LetterboxdFilm> films, CancellationToken cancellationToken)
    {
        await using var db = _database.Open();
        var old = await db.Letterboxd.Where(f => f.UserId == userId && f.Source == source && f.Kind == kind).ToListAsync(cancellationToken);
        var added = films.Count(f => !old.Any(o => SameFilm(o, f)));
        db.Letterboxd.RemoveRange(old);
        foreach (var film in films)
        {
            film.Id = 0;
            film.UserId = userId;
            film.Source = source;
            film.Kind = kind;
        }
        db.Letterboxd.AddRange(films);
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    /// <summary>Links each film to a library movie: TMDB id, IMDb id, then title and year.</summary>
    public async Task<(int Watched, int Watchlist, int Matched)> MatchAsync(Guid userId, CancellationToken cancellationToken)
    {
        var movies = await _store.GetAllMoviesAsync(cancellationToken);
        var byTmdb = movies.Where(m => m.TmdbId is not null).GroupBy(m => m.TmdbId!.Value).ToDictionary(g => g.Key, g => g.First());
        var byImdb = movies.Where(m => !string.IsNullOrWhiteSpace(m.ImdbId)).GroupBy(m => m.ImdbId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byTitle = movies.GroupBy(m => Normalize(m.Title)).ToDictionary(g => g.Key, g => g.ToList());

        MovieMetadata? Find(LetterboxdFilm f)
        {
            if (f.TmdbId is { } t && byTmdb.TryGetValue(t, out var a)) return a;
            if (!string.IsNullOrWhiteSpace(f.ImdbId) && byImdb.TryGetValue(f.ImdbId!, out var b)) return b;
            if (!byTitle.TryGetValue(Normalize(f.Title), out var same)) return null;
            if (f.Year is { } y)
                return same.FirstOrDefault(m => m.ReleaseYear == y) ?? same.FirstOrDefault(m => m.ReleaseYear is { } my && Math.Abs(my - y) == 1);
            return same.Count == 1 ? same[0] : null;
        }

        await using var db = _database.Open();
        var films = await db.Letterboxd.Where(f => f.UserId == userId).ToListAsync(cancellationToken);
        foreach (var film in films) film.ItemId = Find(film)?.ItemId;
        await db.SaveChangesAsync(cancellationToken);

        var watched = films.Where(f => f.Kind == "watched").Select(Key).Distinct().Count();
        var watchlist = films.Where(f => f.Kind == "watchlist").Select(Key).Distinct().Count();
        var matched = films.Where(f => f.ItemId is not null).Select(f => f.ItemId).Distinct().Count();
        return (watched, watchlist, matched);
    }

    private static string Key(LetterboxdFilm f) => f.TmdbId?.ToString() ?? $"{Normalize(f.Title)}|{f.Year}";

    /// <summary>Lower case, accents and punctuation removed, "the"/"a" at the start dropped.</summary>
    public static string Normalize(string title)
    {
        var decomposed = title.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        var s = sb.ToString().Trim();
        foreach (var article in new[] { "the ", "a ", "an " })
            if (s.StartsWith(article, StringComparison.Ordinal)) { s = s[article.Length..]; break; }
        return s;
    }
}
