using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Curation;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;
using Pickyfin.Server.Letterboxd;
using Pickyfin.Server.Library;
using Pickyfin.Server.Worker;

namespace Pickyfin.Server;

/// <summary>Endpoints behind the Movies page and each person's details.</summary>
public static class BrowserApi
{
    public sealed record MovieEdit(
        List<string>? Subcategories, List<string>? Moods, List<string>? Themes,
        string? NarrativeStyle, string? Accessibility, string? Intensity, int CriticalAcclaimScore);

    public sealed record LetterboxdSettings(bool Use, string? Username);

    private static List<string> Tags(TagSet set) => set.Items.ToList();

    private static async Task<Dictionary<Guid, string>> LibraryNamesAsync(LibrarySync sync, JellyfinClient jellyfin, CancellationToken ct)
    {
        var libraries = sync.Libraries;
        if (libraries.Count == 0)
        {
            try { libraries = await jellyfin.GetMovieLibrariesAsync(ct); }
            catch (Exception error) when (error is JellyfinException or HttpRequestException) { }
        }
        return libraries.ToDictionary(l => l.Id, l => l.Name);
    }

    private static object Summary(MovieMetadata m, Dictionary<Guid, string> libraryNames) => new
    {
        Id = m.ItemId,
        m.Title,
        Year = m.ReleaseYear,
        Tagged = m.IsClassified,
        Edited = m.ManuallyEdited,
        Subcategories = Tags(m.SubcategorySet),
        Moods = Tags(m.MoodSet),
        Acclaim = Math.Round(m.Acclaim, 1),
        Libraries = (m.Libraries ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => Guid.TryParse(id, out var g) && libraryNames.TryGetValue(g, out var n) ? n : "?").ToList(),
        Excluded = ServerSettings.Current.IsLibraryExcluded(m.Libraries),
    };

    private static object Detail(MovieMetadata m, Dictionary<Guid, string> libraryNames) => new
    {
        Summary = Summary(m, libraryNames),
        m.Plot,
        m.Director,
        m.Cast,
        Themes = Tags(m.ThemeSet),
        Keywords = Tags(m.KeywordSet),
        KeywordsFetched = m.Keywords is not null,
        m.NarrativeStyle,
        m.Accessibility,
        m.Intensity,
        m.CriticalAcclaimScore,
        m.CommunityRating,
        m.CriticRating,
        m.DateAdded,
        m.LastUpdated,
    };

    public static void MapBrowserApi(this RouteGroupBuilder admin)
    {
        admin.MapGet("/vocabulary", () => Results.Ok(new
        {
            ClassificationVocabulary.Subcategories,
            ClassificationVocabulary.Moods,
            ClassificationVocabulary.NarrativeStyles,
            ClassificationVocabulary.Accessibility,
            ClassificationVocabulary.Intensity,
        }));

        // ---------- movies ----------

        admin.MapGet("/movies", async (string? q, string? state, Guid? library, string? tag, string? sort, int? skip, int? take,
            MovieStore store, LibrarySync sync, JellyfinClient jellyfin, CancellationToken ct) =>
        {
            var names = await LibraryNamesAsync(sync, jellyfin, ct);
            IEnumerable<MovieMetadata> movies = await store.GetAllMoviesAsync(ct);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim();
                movies = movies.Where(m => m.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || (m.Director ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || m.ReleaseYear?.ToString() == needle);
            }
            movies = state switch
            {
                "tagged" => movies.Where(m => m.IsClassified),
                "untagged" => movies.Where(m => !m.IsClassified),
                "edited" => movies.Where(m => m.ManuallyEdited),
                _ => movies,
            };
            if (library is { } lib) movies = movies.Where(m => (m.Libraries ?? "").Contains(lib.ToString(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(tag))
                movies = movies.Where(m => m.SubcategorySet.Contains(tag) || m.MoodSet.Contains(tag) || m.ThemeSet.Contains(tag) || m.KeywordSet.Contains(tag));

            movies = sort switch
            {
                "added" => movies.OrderByDescending(m => m.DateAdded),
                "acclaim" => movies.OrderByDescending(m => m.Acclaim),
                "year" => movies.OrderByDescending(m => m.ReleaseYear),
                _ => movies.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase),
            };
            var list = movies.ToList();
            return Results.Ok(new
            {
                Total = list.Count,
                Items = list.Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 60, 1, 200)).Select(m => Summary(m, names)),
            });
        });

        admin.MapGet("/movies/{id:guid}", async (Guid id, Database database, LibrarySync sync, JellyfinClient jellyfin, CancellationToken ct) =>
        {
            await using var db = database.Open();
            var movie = await db.Movies.AsNoTracking().FirstOrDefaultAsync(m => m.ItemId == id, ct);
            return movie is null ? Results.NotFound() : Results.Ok(Detail(movie, await LibraryNamesAsync(sync, jellyfin, ct)));
        });

        admin.MapPut("/movies/{id:guid}", async (Guid id, MovieEdit edit, Database database, LibrarySync sync, JellyfinClient jellyfin, CancellationToken ct) =>
        {
            await using var db = database.Open();
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.ItemId == id, ct);
            if (movie is null) return Results.NotFound();

            static string Json(IEnumerable<string> values) => JsonSerializer.Serialize(values.ToList());
            List<string> Canon(IEnumerable<string>? raw, Func<string?, string?> canonical) =>
                (raw ?? Enumerable.Empty<string>()).Select(canonical).Where(v => v is not null).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var subcategories = Canon(edit.Subcategories, ClassificationVocabulary.CanonicalSubcategory);
            movie.Subcategories = Json(subcategories);
            movie.Moods = Json(Canon(edit.Moods, ClassificationVocabulary.CanonicalMood));
            movie.Themes = Json((edit.Themes ?? new()).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct());
            movie.NarrativeStyle = ClassificationVocabulary.CanonicalNarrativeStyle(edit.NarrativeStyle);
            movie.Accessibility = ClassificationVocabulary.CanonicalAccessibility(edit.Accessibility);
            movie.Intensity = ClassificationVocabulary.CanonicalIntensity(edit.Intensity);
            movie.CriticalAcclaimScore = Math.Clamp(edit.CriticalAcclaimScore, 0, 10);
            movie.IsClassified = subcategories.Count > 0;
            movie.VocabularyVersion = ClassificationVocabulary.Version;
            movie.ManuallyEdited = true;
            movie.LastUpdated = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(Detail(movie, await LibraryNamesAsync(sync, jellyfin, ct)));
        });

        admin.MapPost("/movies/{id:guid}/retag", async (Guid id, Database database, MovieClassifier classifier, LibrarySync sync, JellyfinClient jellyfin, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(ServerSettings.Current.ApiKey)) return Results.Problem("No AI key set (Settings → AI).", statusCode: 400);
            // Hand it back to the AI: from now on it may tag this movie again
            await using (var db = database.Open())
                await db.Movies.Where(m => m.ItemId == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.ManuallyEdited, false), ct);
            MovieMetadata? movie;
            await using (var db = database.Open())
                movie = await db.Movies.AsNoTracking().FirstOrDefaultAsync(m => m.ItemId == id, ct);
            if (movie is null) return Results.NotFound();
            try
            {
                var ok = await classifier.TagNowAsync(new List<MovieMetadata> { movie }, ct);
                if (!ok) return Results.Problem("The AI answered, but without usable tags. Try again.", statusCode: 502);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Results.Problem("The AI call failed: " + error.Message, statusCode: 502);
            }
            await using (var db = database.Open())
                movie = await db.Movies.AsNoTracking().FirstAsync(m => m.ItemId == id, ct);
            return Results.Ok(Detail(movie, await LibraryNamesAsync(sync, jellyfin, ct)));
        });

        admin.MapGet("/movies/{id:guid}/poster", async (Guid id, JellyfinClient jellyfin, HttpContext http, CancellationToken ct) =>
        {
            HttpResponseMessage response;
            try { response = await jellyfin.GetPosterAsync(id, ct); }
            catch (Exception error) when (error is JellyfinException or HttpRequestException) { return Results.NotFound(); }
            using var _ = response;
            if (!response.IsSuccessStatusCode) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=86400";
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return Results.File(bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        });

        // ---------- one person's data ----------

        admin.MapGet("/users/{id:guid}/details", async (Guid id, Database database, JellyfinClient jellyfin, MovieStore store, CancellationToken ct) =>
        {
            UserProfile? user;
            List<LetterboxdFilm> films;
            List<ManagedPlaylist> playlists;
            await using (var db = database.Open())
            {
                user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == id, ct);
                films = await db.Letterboxd.AsNoTracking().Where(f => f.UserId == id).ToListAsync(ct);
                playlists = await db.Playlists.AsNoTracking().Where(p => p.UserId == id).OrderBy(p => p.Slot).ToListAsync(ct);
            }
            if (user is null) return Results.NotFound();
            var library = (await store.GetAllMoviesAsync(ct)).ToDictionary(m => m.ItemId);
            string Name(Guid? itemId) => itemId is { } i && library.TryGetValue(i, out var m) ? $"{m.Title} ({m.ReleaseYear})" : "";

            List<JellyfinItem> items;
            try { items = await jellyfin.GetMoviesAsync(id, withDetails: false, ct); }
            catch (Exception error) when (error is JellyfinException or HttpRequestException) { items = new(); }
            var played = items
                .Where(i => i.UserData is { Played: true })
                .OrderByDescending(i => i.UserData!.LastPlayedDate ?? DateTime.MinValue)
                .Take(300)
                .Select(i => new
                {
                    i.Id,
                    Title = i.Name,
                    Year = i.ProductionYear,
                    LastPlayed = i.UserData!.LastPlayedDate,
                    Counted = user.CountHistoryFrom is null || (i.UserData.LastPlayedDate ?? DateTime.MinValue) >= user.CountHistoryFrom,
                })
                .ToList();

            object Film(LetterboxdFilm f) => new { f.Title, f.Year, f.Rating, f.Date, f.Source, InLibrary = Name(f.ItemId), f.ItemId };
            return Results.Ok(new
            {
                user.UserId,
                user.Name,
                History = played,
                Letterboxd = new
                {
                    Use = user.UseLetterboxd,
                    Username = user.LetterboxdUsername,
                    Synced = user.LetterboxdSynced,
                    Status = user.LetterboxdStatus,
                    Watched = films.Where(f => f.Kind == "watched")
                        .GroupBy(f => f.TmdbId?.ToString() ?? $"{LetterboxdSync.Normalize(f.Title)}|{f.Year}")
                        .Select(g => g.OrderByDescending(f => f.Rating is not null).ThenByDescending(f => f.Date).First())
                        .OrderByDescending(f => f.Date ?? DateTime.MinValue).Select(Film),
                    // Once watched, a film is no longer "to watch"
                    Watchlist = films.Where(f => f.Kind == "watchlist")
                        .Where(w => !films.Any(f => f.Kind == "watched" && (w.ItemId is not null ? f.ItemId == w.ItemId : LetterboxdSync.Normalize(f.Title) == LetterboxdSync.Normalize(w.Title) && f.Year == w.Year)))
                        .DistinctBy(f => $"{LetterboxdSync.Normalize(f.Title)}|{f.Year}")
                        .OrderByDescending(f => f.ItemId is not null).ThenBy(f => f.Title).Select(Film),
                },
                BriefTags = BriefProfile.FromJson(user.BriefProfile),
                Rows = playlists.Select(p => new
                {
                    p.Name,
                    Movies = p.Items.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                        .Select(g => new { Id = g, Title = Name(g) }),
                }),
            });
        });

        admin.MapPut("/users/{id:guid}/letterboxd", async (Guid id, LetterboxdSettings body, Database database, LetterboxdSync letterboxd, RefreshService worker, CancellationToken ct) =>
        {
            UserProfile? user;
            await using (var db = database.Open())
            {
                user = await db.Users.FindAsync(new object[] { id }, ct);
                if (user is null) return Results.NotFound();
                user.UseLetterboxd = body.Use;
                user.LetterboxdUsername = LetterboxdSync.CleanUsername(body.Username);
                // Links from the old plugin are no longer used
                user.LetterboxdWatchlistUrl = null;
                user.LetterboxdRatingsUrl = null;
                user.RefreshRequested = true;
                await db.SaveChangesAsync(ct);
            }
            // Fetch right away so the page can show what was found
            if (user.UseLetterboxd && LetterboxdSync.HasOnlineSource(user)) await letterboxd.SyncAsync(user, ct);
            worker.Wake();
            return Results.Ok();
        });

        admin.MapPost("/users/{id:guid}/letterboxd/import", async (Guid id, IFormFile file, Database database, LetterboxdSync letterboxd, RefreshService worker, CancellationToken ct) =>
        {
            UserProfile? user;
            await using (var db = database.Open())
                user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == id, ct);
            if (user is null) return Results.NotFound();
            try
            {
                await using var stream = file.OpenReadStream();
                var status = await letterboxd.ImportExportAsync(user, stream, file.FileName, ct);
                worker.Wake();
                return Results.Ok(new { Status = status });
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
            {
                return Results.Problem(error.Message, statusCode: 400);
            }
        }).DisableAntiforgery();

        admin.MapDelete("/users/{id:guid}/letterboxd", async (Guid id, Database database, RefreshService worker, CancellationToken ct) =>
        {
            await using var db = database.Open();
            await db.Letterboxd.Where(f => f.UserId == id).ExecuteDeleteAsync(ct);
            await db.Users.Where(u => u.UserId == id).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.LetterboxdStatus, (string?)null)
                .SetProperty(x => x.LetterboxdSynced, (DateTime?)null)
                .SetProperty(x => x.RefreshRequested, true), ct);
            worker.Wake();
            return Results.Ok();
        });
    }
}
