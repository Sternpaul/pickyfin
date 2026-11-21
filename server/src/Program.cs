using Microsoft.Extensions.Logging.Console;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Curation;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Jellyfin;
using Pickyfin.Server.Library;
using Pickyfin.Server.Worker;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Environment.GetEnvironmentVariable("PICKYFIN_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDirectory);
ServerSettings.Load(dataDirectory);

// Container log: one line per event, our own messages only; ASP.NET's per-request
// chatter stays off unless something goes wrong
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.FormatterName = LogFormat.Name).AddConsoleFormatter<LogFormat, ConsoleFormatterOptions>();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
var logBuffer = new LogBuffer();
builder.Logging.AddProvider(logBuffer);
builder.Services.AddSingleton(logBuffer);

builder.Services.AddHttpClient();
builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(Net.CreateHandler));
// The page reads PascalCase names, exactly as the C# properties are called
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null);
builder.Services.AddSingleton(new Database(dataDirectory));
builder.Services.AddSingleton<MovieStore>();
builder.Services.AddSingleton(sp => new JellyfinClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("jellyfin")));
builder.Services.AddHttpClient("jellyfin", c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<AIProviderFactory>();
builder.Services.AddSingleton(new DataDirectory(dataDirectory));
builder.Services.AddSingleton<WebLookup>();
builder.Services.AddSingleton<MovieClassifier>();
builder.Services.AddSingleton<SecondOpinion>();
builder.Services.AddSingleton<WebSweep>();
builder.Services.AddSingleton<Rescan>();
builder.Services.AddSingleton<OmdbScores>();
builder.Services.AddSingleton<LetterboxdRatings>();
builder.Services.AddSingleton<TmdbDetails>();
builder.Services.AddSingleton<Meaning>();
builder.Services.AddSingleton<MovieHistory>();
builder.Services.AddSingleton(sp => new TmdbKeywordService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("tmdb"),
    sp.GetRequiredService<MovieStore>(),
    sp.GetRequiredService<ILogger<TmdbKeywordService>>(),
    dataDirectory));
builder.Services.AddSingleton<LibrarySync>();
builder.Services.AddSingleton<BriefInterpreter>();
builder.Services.AddSingleton<Curator>();
builder.Services.AddSingleton<PlaylistWriter>();
builder.Services.AddSingleton(sp => new Pickyfin.Server.Letterboxd.LetterboxdSync(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("letterboxd"),
    sp.GetRequiredService<Database>(), sp.GetRequiredService<MovieStore>(),
    sp.GetRequiredService<ILogger<Pickyfin.Server.Letterboxd.LetterboxdSync>>()));
builder.Services.AddSingleton<RefreshService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RefreshService>());

// The admin page logs in with a Jellyfin administrator account and keeps a cookie
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "pickyfin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
// Jellyfin unreachable or refusing: answer with a clear message instead of a crash page
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (JellyfinException error) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new { title = "Jellyfin", detail = error.Message });
    }
    catch (HttpRequestException error) when (!context.Response.HasStarted && context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new { title = "Network", detail = "Can't reach Jellyfin: " + error.Message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
// Scripts may send the Jellyfin API key instead of logging in (it's an admin key anyway)
app.Use(async (context, next) =>
{
    var key = context.Request.Headers["X-Api-Key"].ToString();
    var expected = ServerSettings.Current.JellyfinApiKey;
    if (key.Length > 0 && expected.Length > 0
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(expected)))
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "API key") }, "ApiKey"));
    await next();
});
app.UseAuthorization();

var api = app.MapGroup("/api");
var admin = api.MapGroup("").RequireAuthorization();

// ---------- login ----------

api.MapPost("/login", async (LoginRequest request, JellyfinClient jellyfin, HttpContext http, CancellationToken ct) =>
{
    if (!jellyfin.IsConfigured) return Results.Problem("Set JELLYFIN_URL and JELLYFIN_API_KEY first.", statusCode: 503);
    JellyfinUser? user;
    try { user = await jellyfin.AuthenticateAdminAsync(request.Username, request.Password, ct); }
    catch (Exception error) { return Results.Problem("Could not reach Jellyfin: " + error.Message, statusCode: 502); }
    if (user is null) return Results.Problem("Wrong username or password, or not a Jellyfin administrator.", statusCode: 401);

    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user.Name) }, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { user.Name });
});

api.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

api.MapGet("/me", (HttpContext http) =>
    http.User.Identity?.IsAuthenticated == true ? Results.Ok(new { Name = http.User.Identity.Name }) : Results.Unauthorized());

// ---------- status ----------

admin.MapGet("/status", async (RefreshService worker, LibrarySync library, MovieStore store, Database database, LogBuffer log, MovieHistory history, CancellationToken ct) =>
{
    var s = ServerSettings.Current;
    int rescanned = 0, rescanOf = 0;
    var queuedAgain = await store.CountRedoAsync(ct);
    if (s.RescanMonths > 0 || s.RescanPassStarted is not null)
    {
        var cutoff = Rescan.Cutoff(s);
        await using var db = database.Open();
        var rows = await db.Movies.AsNoTracking().Where(m => m.IsClassified && !m.ManuallyEdited && !m.Verified)
            .Select(m => new { m.Libraries, m.Rescanned }).ToListAsync(ct);
        var eligible = rows.Where(r => !s.IsLibraryExcluded(r.Libraries)).ToList();
        rescanOf = eligible.Count;
        rescanned = eligible.Count(r => r.Rescanned >= cutoff);
    }
    var (total, classified, issues, reviewed, lookedUp) = await store.GetStatsAsync(ct);
    int scored = 0, detailed = 0, embedded = 0, lbRated = 0;
    if (LetterboxdRatings.Last is { Reached: true })
    {
        await using var db = database.Open();
        var rows = await db.Movies.AsNoTracking().Where(m => m.LbRating != null).Select(m => m.Libraries).ToListAsync(ct);
        lbRated = rows.Count(l => !s.IsLibraryExcluded(l));
    }
    string? meaningTip = null;
    if (Meaning.On(s))
    {
        await using var db = database.Open();
        var byModel = await db.Embeddings.AsNoTracking().Where(e => e.Kind == "film").GroupBy(e => e.Model).Select(g => new { Model = g.Key, Films = g.Count() }).ToListAsync(ct);
        var lines = new List<string>();
        // every model in the order builds its own matrix
        var order = Meaning.Order(s);
        var shown = order.Concat(byModel.Select(m => m.Model)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (shown.Count > 0) lines.Add("Each model builds its own matrix:\n" + string.Join("\n", shown.Select(m =>
        {
            var films = byModel.FirstOrDefault(b => string.Equals(b.Model, m, StringComparison.OrdinalIgnoreCase))?.Films ?? 0;
            var state = OpenRouterModels.IsGone(m) ? "gone from OpenRouter" : !order.Contains(m, StringComparer.OrdinalIgnoreCase) ? "not in the order, not used"
                : Meaning.RestingWhy(m) is { } why ? (why.Contains("tried again") ? why : $"{why}, tried again next round") : Meaning.Left.TryGetValue(m, out var left) && left > 0 ? $"{left:N0} to match" : "up to date";
            var refused = Meaning.RefusedCount(m);
            return $"  {Meaning.Short(m)}: {films:N0} film{(films == 1 ? "" : "s")} · {state}" + (refused > 0 ? $" · {refused} text{(refused == 1 ? "" : "s")} it refused, set aside (see the log)" : "");
        })));
        var failed = OpenRouterModels.StepFailures.Where(f => f.Key.StartsWith("meaning|")).OrderByDescending(f => f.Value).ToList();
        if (failed.Count > 0) lines.Add("Failed since the server started:\n" + string.Join("\n", failed.Take(6).Select(f => $"  {f.Key[8..]}: {f.Value}×")));
        meaningTip = lines.Count == 0 ? "No model used yet" : string.Join("\n\n", lines);
        // a film counts as matched once any model has it
        var ids = await db.Embeddings.AsNoTracking().Where(e => e.Kind == "film" && order.Contains(e.Model)).Select(e => e.ItemId).Distinct().ToListAsync(ct);
        var set = ids.ToHashSet();
        var rows = await db.Movies.AsNoTracking().Select(m => new { m.ItemId, m.Libraries }).ToListAsync(ct);
        embedded = rows.Count(r => set.Contains(r.ItemId) && !s.IsLibraryExcluded(r.Libraries));
    }
    if (!string.IsNullOrWhiteSpace(s.OmdbApiKey) || !string.IsNullOrWhiteSpace(s.TmdbApiKey))
    {
        await using var db = database.Open();
        var rows = await db.Movies.AsNoTracking().Select(m => new { m.Libraries, Scored = m.ScoresFetched != null, Detailed = m.DetailsFetched != null }).ToListAsync(ct);
        var counted = rows.Where(r => !s.IsLibraryExcluded(r.Libraries)).ToList();
        scored = counted.Count(r => r.Scored);
        detailed = counted.Count(r => r.Detailed);
    }
    var (waitingFor, seconds) = Activity.Current;
    var hasKey = !string.IsNullOrWhiteSpace(s.ApiKey);
    var anyRunning = Jobs.Snapshot().Any(j => j.Running);
    var lookupAll = Jobs.Get("lookupall");

    // Hover text per step: which models handled the films, and which failed since the start
    var used = await history.ModelsByStepAsync(ct);
    string? Tip(string step, string verb)
    {
        static string Short(string model) => model.Contains('/') ? model[(model.IndexOf('/') + 1)..].Replace(":free", "") : model;
        var lines = new List<string>();
        if (used.TryGetValue(step, out var models) && models.Count > 0)
            lines.Add($"{verb}:\n" + string.Join("\n", models.Take(6).Select(m => $"  {Short(m.Model)}: {m.Films:N0} film{(m.Films == 1 ? "" : "s")}")));
        var failed = OpenRouterModels.StepFailures.Where(f => f.Key.StartsWith(step + "|")).OrderByDescending(f => f.Value).ToList();
        if (failed.Count > 0)
            lines.Add("Failed since the server started:\n" + string.Join("\n", failed.Take(6).Select(f => $"  {Short(f.Key[(step.Length + 1)..])}: {f.Value}×")));
        return lines.Count == 0 ? "No model used yet since history is kept" : string.Join("\n\n", lines);
    }

    // One row per job, in the order a round runs them
    // rest: what the films not done are, when the step has done all it can (e.g. "not found on TMDB")
    object Row(string key, string title, bool on, string off, int? done = null, int? of = null, int queued = 0, string? tip = null, string? rest = null)
    {
        var j = Jobs.Get(key);
        var state = !on ? "off" : j is { Running: true } ? "running" : j is { Error: true } ? "paused"
            : queued > 0 || (of is { } o && done < o) ? "waiting" : "idle";
        var text = !on ? off : j?.Now ?? "";
        // Instead of a bare "Waiting": say why the films not done yet aren't done
        string? badge = null;
        if (state == "waiting")
        {
            var left = queued > 0 ? queued : (of ?? 0) - (done ?? 0);
            if (text.StartsWith("Today's", StringComparison.Ordinal))
            {
                badge = "Today's done";
                text += $" · {left:N0} left for the next days";
            }
            else if (rest is not null && !text.Contains("to go") && !text.Contains("to match"))
            {
                state = "idle";
                badge = "Done";
                text = rest;
            }
            else
            {
                badge = "Next round";
                // a round stops when the library sync fails, so the steps after it wait for that
                if (text.Length == 0) text = Jobs.Get("library") is { Error: true } ? "Waits until the library sync works again" : "Starts in the next round";
            }
        }
        return new
        {
            Key = key, Title = title, State = state, Badge = badge,
            Text = text,
            Next = on && state != "running" ? j?.Next : null,
            RunningFor = j is { Running: true } ? (int)(DateTime.UtcNow - j.Since).TotalSeconds : 0,
            Done = done, Of = of, Queued = queued, Tip = tip, At = j?.Since,
        };
    }
    var lookupKey = lookupAll is { Running: true } ? "lookupall" : "sweep";
    var queue = new[]
    {
        Row("library", "Library sync", true, ""),
        Row("picks", "Rows for each person", true, ""),
        // While a "describe again" pass runs, count what this pass has done
        Row("tagging", "1 · Describe", hasKey, "Off: no AI key", queuedAgain > 0 ? Math.Max(0, classified - queuedAgain) : classified, total, queuedAgain,
            Tip("describe", "Described with")),
        Row("second", "2 · Check", s.CheckEnabled && hasKey, "Off", reviewed, classified, tip: Tip("check", "Checked with")),
        Row(lookupKey, "3 · Look up", WebLookup.Configured && (s.WebLookupsPerDay > 0 || s.WebLookupAuto || lookupAll is { Running: true }),
            WebLookup.Configured ? "Off" : "Off: no web search set up", lookedUp, classified, tip: Tip("lookup", "Described again from the web with")),
        Row("rescan", "4 · Re-check", (s.RescanMonths > 0 || s.RescanPassStarted is not null) && hasKey, "Off", rescanned, rescanOf, tip: Tip("rescan", "Re-checked with")),
        Row(OmdbScores.Key, OmdbScores.Title, !string.IsNullOrWhiteSpace(s.OmdbApiKey), "Off: no OMDb key", scored, total),
        Row(LetterboxdRatings.Key, LetterboxdRatings.Title, s.LetterboxdOn, "Off", LetterboxdRatings.Last is { Reached: true } ? lbRated : null, LetterboxdRatings.Last is { Reached: true } ? total : null,
            tip: $"From letterboxd-collector at {LetterboxdRatings.Address(s)}" + (LetterboxdRatings.Last is { } lbLast ? $"\n{lbLast.Message}" + (lbLast.WithRating is { } wr ? $"; it has ratings for {wr:N0} films" : "") : ""),
            rest: $"Up to date · {total - lbRated:N0} film{(total - lbRated == 1 ? " has" : "s have")} no Letterboxd rating in the collector (yet)"),
        Row(TmdbDetails.Key, TmdbDetails.Title, !string.IsNullOrWhiteSpace(s.TmdbApiKey), "Off: no TMDB key", detailed, total,
            rest: $"Up to date · {total - detailed:N0} film{(total - detailed == 1 ? "" : "s")} without a TMDB or IMDb id in Jellyfin"),
        Row(Meaning.Key, Meaning.Title, Meaning.On(s), s.AIProvider == AIProviderType.OpenRouter ? "Off" : "Off: needs OpenRouter", embedded, classified, tip: meaningTip),
    };

    int gone;
    await using (var goneDb = database.Open())
        gone = await goneDb.Movies.IgnoreQueryFilters().CountAsync(m => m.GoneSince != null, ct);
    return Results.Ok(new
    {
        // What is happening this second: the job, what it waits for, which model and which try
        Now = waitingFor ?? Activity.Background ?? (worker.State == "Idle" ? null : worker.State),
        NowDetail = waitingFor is not null ? Activity.Detail : null,
        Waiting = waitingFor is null ? 0 : seconds,
        TaggingLeft = TaggingEta.SecondsLeft,
        // films Jellyfin no longer lists, waiting for the admin to confirm their removal
        Gone = gone,
        worker.LastError,
        LastErrorAt = worker.LastErrorAt,
        LibrarySynced = library.LastSync,
        Movies = total,
        Classified = classified,
        Issues = issues,
        Queue = queue,
        Log = log.Lines.Reverse().Take(80),
    });
});

// Embedding models OpenRouter offers, free ones first, for matching by meaning
// ---------- Letterboxd (letterboxd-collector) ----------

admin.MapPost("/letterboxd/check", async (LetterboxdRatings letterboxd, RefreshService worker, CancellationToken ct) =>
{
    var state = await letterboxd.CheckAsync(ct);
    if (state.Reached) { LetterboxdRatings.ResetPause(); worker.Wake(); }
    return Results.Ok(new { state.Reached, state.Url, state.Message, state.Schema, state.WithRating, state.InCollector, state.Matched });
});

// ---------- for the TV app: scores of one film, signed in with the viewer's Jellyfin token ----------

var tvTokens = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
// The viewer's own Jellyfin token, checked with Jellyfin once an hour
async Task<IResult?> TvSignedIn(HttpContext http, JellyfinClient jellyfin, CancellationToken ct)
{
    var token = http.Request.Headers["X-Emby-Token"].ToString();
    if (token.Length == 0) token = http.Request.Headers["X-MediaBrowser-Token"].ToString();
    if (token.Length == 0 && System.Text.RegularExpressions.Regex.Match(http.Request.Headers.Authorization.ToString(), "Token=\"([^\"]+)\"") is { Success: true } match)
        token = match.Groups[1].Value;
    if (token.Length == 0) return Results.Unauthorized();
    if (tvTokens.TryGetValue(token, out var until) && until > DateTime.UtcNow) return null;
    bool valid;
    try { valid = await jellyfin.IsValidTokenAsync(token, ct); }
    catch (Exception) { return Results.Problem("Can't reach Jellyfin", statusCode: 502); }
    if (!valid) return Results.Unauthorized();
    if (tvTokens.Count > 1000) tvTokens.Clear();
    tvTokens[token] = DateTime.UtcNow.AddHours(1);
    return null;
}

// Every film's scores in one list: the TV app downloads it at start and every few hours.
// I = Jellyfin item id, L = Letterboxd (0.5–5), R = Rotten Tomatoes (0–100)
api.MapGet("/tv/ratings", async (HttpContext http, JellyfinClient jellyfin, Database database, CancellationToken ct) =>
{
    if (await TvSignedIn(http, jellyfin, ct) is { } refused) return refused;
    await using var db = database.Open();
    var films = await db.Movies.AsNoTracking().Where(m => m.LbRating != null || m.RtScore != null)
        .Select(m => new { I = m.ItemId, L = m.LbRating, R = m.RtScore }).ToListAsync(ct);
    return Results.Ok(new { Hours = Math.Clamp(ServerSettings.Current.LetterboxdHours, 1, 168), Films = films });
});

admin.MapGet("/ai/embedding-models", async (IHttpClientFactory http, CancellationToken ct) =>
{
    try
    {
        var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        using var doc = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("https://openrouter.ai/api/v1/models?output_modalities=embeddings&sort=top-weekly", ct));
        var list = doc.RootElement.GetProperty("data").EnumerateArray().Select(m =>
        {
            var id = m.GetProperty("id").GetString() ?? "";
            var price = m.TryGetProperty("pricing", out var p) && p.TryGetProperty("prompt", out var pp) && double.TryParse(pp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            return new { Id = id, Name = m.TryGetProperty("name", out var n) ? n.GetString() : id, Free = price == 0, PerMillion = Math.Round(price * 1_000_000, 3) };
        }).Where(m => m.Id.Length > 0).OrderByDescending(m => m.Free).ToList();
        return Results.Ok(list);
    }
    catch (Exception error) { return Results.Problem("Couldn't reach OpenRouter: " + error.Message, statusCode: 502); }
});

// Models OpenRouter offers right now, and how each did since the server started, for the model order
admin.MapGet("/ai/models", async (IHttpClientFactory http, ILoggerFactory logs, CancellationToken ct) =>
{
    var live = await OpenRouterModels.GetAsync(http.CreateClient(), logs.CreateLogger("OpenRouter"), ct);
    return Results.Ok(new
    {
        Known = live is not null,
        Free = OpenRouterModels.Free,
        Offered = live?.Keys.ToList() ?? new List<string>(),
        Stats = OpenRouterModels.Stats,
        OpenRouterProvider.TriesPerModel,
        OpenRouterModels.AutoCount,
    });
});

// ---------- run a step again for the whole library ----------
// Films a step would touch: described, not edited or checked by you, in a library that's on
async Task<List<MovieMetadata>> RedoableAsync(Database database, CancellationToken ct)
{
    await using var db = database.Open();
    var rows = await db.Movies.AsNoTracking().Where(m => m.IsClassified && !m.ManuallyEdited && !m.Verified).ToListAsync(ct);
    return rows.Where(m => !ServerSettings.Current.IsLibraryExcluded(m.Libraries)).ToList();
}

admin.MapGet("/steps/again", async (Database database, MovieStore store, CancellationToken ct) =>
{
    var films = await RedoableAsync(database, ct);
    var s = ServerSettings.Current;
    return Results.Ok(new
    {
        Describe = films.Count, DescribeQueued = await store.CountRedoAsync(ct),
        Check = films.Count(m => string.IsNullOrEmpty(m.WebNotes)), CheckPending = films.Count(m => string.IsNullOrEmpty(m.WebNotes) && !m.Checked),
        Lookup = films.Count, LookupPassStarted = s.RescanPassStarted,
        LookupPerDay = s.RescanMonths > 0 ? Rescan.PerDay(films.Count, s.RescanMonths) : Rescan.ManualPace(s),
    });
});

admin.MapPost("/steps/{step}/again", async (string step, Database database, RefreshService worker, ILoggerFactory logs, CancellationToken ct) =>
{
    var ids = (await RedoableAsync(database, ct)).Select(m => m.ItemId).ToList();
    var log = logs.CreateLogger("Pickyfin.Server.Steps");
    await using var db = database.Open();
    int count;
    switch (step)
    {
        case "describe":
            count = await db.Movies.Where(m => ids.Contains(m.ItemId)).ExecuteUpdateAsync(u => u.SetProperty(m => m.RedoDescribe, true), ct);
            var redo = ServerSettings.Current.Clone(); redo.RedoDescribeTotal = count; ServerSettings.Save(redo);
            MovieClassifier.RetryNow();
            log.LogInformation("Step 1 runs again: {Count} films queued to be described again, 8 calls per round", count);
            break;
        case "check":
            // Looked-up films are past the second opinion; the web text is the stronger evidence
            count = await db.Movies.Where(m => ids.Contains(m.ItemId) && (m.WebNotes == null || m.WebNotes == "")).ExecuteUpdateAsync(u => u.SetProperty(m => m.Checked, false), ct);
            SecondOpinion.ResetPause();
            log.LogInformation("Step 2 runs again: {Count} films to be checked again", count);
            break;
        case "lookup":
            var settings = ServerSettings.Current.Clone();
            settings.RescanPassStarted = DateTime.UtcNow;
            ServerSettings.Save(settings);
            count = ids.Count;
            log.LogInformation("Steps 3 and 4 run again: a re-check pass over {Count} films starts now", count);
            break;
        default:
            return Results.NotFound();
    }
    worker.Wake();
    return Results.Ok(new { Count = count });
});

admin.MapPost("/steps/{step}/stop", async (string step, Database database, ILoggerFactory logs, CancellationToken ct) =>
{
    var log = logs.CreateLogger("Pickyfin.Server.Steps");
    if (step == "describe")
    {
        await using var db = database.Open();
        var count = await db.Movies.Where(m => m.RedoDescribe).ExecuteUpdateAsync(u => u.SetProperty(m => m.RedoDescribe, false), ct);
        log.LogInformation("Step 1: stopped describing again; {Count} films taken out of the queue", count);
        return Results.Ok(new { Count = count });
    }
    if (step == "lookup")
    {
        var settings = ServerSettings.Current.Clone();
        settings.RescanPassStarted = null;
        ServerSettings.Save(settings);
        log.LogInformation("Steps 3 and 4: the pass started by hand is stopped");
        return Results.Ok(new { Count = 0 });
    }
    return Results.NotFound();
});

// Films Jellyfin no longer lists: hidden, and deleted only when the admin confirms
admin.MapGet("/library/gone", async (MovieStore store, CancellationToken ct) =>
    Results.Ok((await store.GetGoneAsync(ct)).Select(m => new { Id = m.ItemId, m.Title, Year = m.ReleaseYear, Since = m.GoneSince, m.IsClassified })));

admin.MapPost("/library/gone/remove", async (GoneRemoval request, MovieStore store, Meaning meaning, ILoggerFactory logs, CancellationToken ct) =>
{
    var removed = await store.DeleteGoneAsync(request.Ids, ct);
    if (removed.Count > 0)
        logs.CreateLogger("Library").LogInformation("Library: removed {Count} film(s) Jellyfin no longer has, as confirmed: {Titles}", removed.Count, string.Join(", ", removed.Take(20)) + (removed.Count > 20 ? ", …" : ""));
    return Results.Ok(new { Removed = removed.Count });
});

admin.MapPost("/library/sync", (RefreshService worker) =>
{
    worker.Wake(syncLibrary: true);
    return Results.Accepted();
});

// ---------- settings ----------

admin.MapGet("/settings", () => Results.Ok(SettingsView.From(ServerSettings.Current)));

admin.MapPut("/settings", async (SettingsView view, RefreshService worker, Database database, CancellationToken ct) =>
{
    var before = ServerSettings.Current;
    var settings = before.Clone();
    view.ApplyTo(settings);
    ServerSettings.Save(settings);

    // Anything that changes what gets picked: everyone gets fresh picks
    var picksChanged = !before.ExcludedLibraries.OrderBy(g => g).SequenceEqual(settings.ExcludedLibraries.OrderBy(g => g))
        || before.BecauseRows != settings.BecauseRows || before.PicksPerRow != settings.PicksPerRow
        || before.BriefWeight != settings.BriefWeight || before.MeaningWeight != settings.MeaningWeight || before.Discovery != settings.Discovery || before.Variety != settings.Variety
        || before.BecausePeople != settings.BecausePeople || before.BecauseCountry != settings.BecauseCountry || before.BecauseEra != settings.BecauseEra
        || before.NewArrivals != settings.NewArrivals || before.Rotation != settings.Rotation || before.MinRating != settings.MinRating;
    if (picksChanged)
    {
        await using var db = database.Open();
        await db.Users.Where(u => u.Enabled).ExecuteUpdateAsync(u => u.SetProperty(x => x.RefreshRequested, true), ct);
    }
    // A new model or key deserves a try right away, not after the pause from the last error
    if (before.CheckModel != settings.CheckModel || before.ApiKey != settings.ApiKey || before.CheckEnabled != settings.CheckEnabled
        || !before.CheckModels.SequenceEqual(settings.CheckModels) || before.CheckModelsAuto != settings.CheckModelsAuto)
        SecondOpinion.ResetPause();
    if (!before.ClassificationModels.SequenceEqual(settings.ClassificationModels) || before.ClassificationModelsAuto != settings.ClassificationModelsAuto || before.ApiKey != settings.ApiKey)
        MovieClassifier.RetryNow();
    // A library switched back on may have movies that were never classified
    if (before.OmdbApiKey != settings.OmdbApiKey || before.OmdbPerDay != settings.OmdbPerDay) OmdbScores.ResetPause();
    if (before.TmdbApiKey != settings.TmdbApiKey) TmdbDetails.ResetPause();
    if (before.LetterboxdOn != settings.LetterboxdOn || before.LetterboxdUrl != settings.LetterboxdUrl || before.LetterboxdToken != settings.LetterboxdToken
        || before.LetterboxdHours != settings.LetterboxdHours)
        LetterboxdRatings.ResetPause();
    if (before.MeaningOn != settings.MeaningOn || before.EmbeddingModelsAuto != settings.EmbeddingModelsAuto
        || !before.EmbeddingModels.SequenceEqual(settings.EmbeddingModels) || before.ApiKey != settings.ApiKey) Meaning.ResetPause();
    var librariesChanged = !before.ExcludedLibraries.OrderBy(g => g).SequenceEqual(settings.ExcludedLibraries.OrderBy(g => g));
    worker.Wake(syncLibrary: librariesChanged || before.ApiKey != settings.ApiKey || before.TmdbApiKey != settings.TmdbApiKey
        || (settings.CheckEnabled && (!before.CheckEnabled || before.CheckModel != settings.CheckModel)) || before.ExaApiKey != settings.ExaApiKey || before.UseExa != settings.UseExa || before.UseParallel != settings.UseParallel
        || System.Text.Json.JsonSerializer.Serialize(before.SearchEngines) != System.Text.Json.JsonSerializer.Serialize(settings.SearchEngines));
    return Results.Ok(SettingsView.From(settings));
});

admin.MapGet("/libraries", async (JellyfinClient jellyfin, MovieStore store, CancellationToken ct) =>
{
    var libraries = await jellyfin.GetMovieLibrariesAsync(ct);
    var movies = await store.GetAllMoviesAsync(ct);
    return Results.Ok(libraries.Select(l => new
    {
        l.Id,
        l.Name,
        Movies = movies.Count(m => m.Libraries?.Contains(l.Id.ToString(), StringComparison.OrdinalIgnoreCase) == true),
        Enabled = !ServerSettings.Current.ExcludedLibraries.Contains(l.Id),
    }));
});

admin.MapPost("/settings/test-ai", async (AIProviderFactory ai, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(ServerSettings.Current.ApiKey)) return Results.Ok(new { Ok = false, Message = "No API key set." });
    try
    {
        var answer = await ai.GetProvider().ChatAsync("Reply with the single word: ready", "You are a connection test.", ct);
        return Results.Ok(new { Ok = !string.IsNullOrWhiteSpace(answer), Message = string.IsNullOrWhiteSpace(answer) ? "The model answered with nothing." : "Connected. The model answered." });
    }
    catch (Exception error)
    {
        return Results.Ok(new { Ok = false, Message = error.Message });
    }
});


// ---------- users ----------

admin.MapGet("/users", async (Database database, CancellationToken ct) =>
{
    await using var db = database.Open();
    var users = await db.Users.AsNoTracking().OrderBy(u => u.Name).ToListAsync(ct);
    var playlists = await db.Playlists.AsNoTracking().ToListAsync(ct);
    return Results.Ok(users.Select(u => new
    {
        u.UserId,
        u.Name,
        u.Enabled,
        u.Brief,
        BriefTags = BriefProfile.FromJson(u.BriefProfile),
        u.CountHistoryFrom,
        u.LastRefresh,
        u.LastResult,
        Pending = u.RefreshRequested || (u.Brief ?? "") != (u.BriefProfileFor ?? ""),
        Rows = playlists.Where(p => p.UserId == u.UserId).OrderBy(p => p.Slot).Select(p => p.Name),
    }));
});

admin.MapPut("/users/{id:guid}", async (Guid id, UserUpdate update, Database database, RefreshService worker, CancellationToken ct) =>
{
    await using var db = database.Open();
    var user = await db.Users.FindAsync(new object[] { id }, ct);
    if (user is null) return Results.NotFound();

    var switchedOff = user.Enabled && !update.Enabled;
    user.Enabled = update.Enabled;
    user.Brief = (update.Brief ?? "").Trim();
    if (update.CountHistoryFrom != user.CountHistoryFrom) user.RefreshRequested = true;
    user.CountHistoryFrom = update.CountHistoryFrom;
    if (update.Enabled && user.LastRefresh is null) user.RefreshRequested = true;
    await db.SaveChangesAsync(ct);

    if (switchedOff) await worker.SwitchOffAsync(id, ct);
    worker.Wake();
    return Results.Ok();
});

admin.MapPost("/users/{id:guid}/refresh", async (Guid id, Database database, RefreshService worker, CancellationToken ct) =>
{
    await using var db = database.Open();
    var user = await db.Users.FindAsync(new object[] { id }, ct);
    if (user is null) return Results.NotFound();
    user.RefreshRequested = true;
    await db.SaveChangesAsync(ct);
    worker.Wake();
    return Results.Accepted();
});

admin.MapGet("/users/{id:guid}/rows", async (Guid id, Database database, MovieStore store, CancellationToken ct) =>
{
    await using var db = database.Open();
    var playlists = await db.Playlists.AsNoTracking().Where(p => p.UserId == id).OrderBy(p => p.Slot).ToListAsync(ct);
    var movies = (await store.GetAllMoviesAsync(ct)).ToDictionary(m => m.ItemId);
    return Results.Ok(playlists.Select(p => new
    {
        p.Name,
        Movies = p.Items.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(Guid.Parse)
            .Select(itemId => movies.TryGetValue(itemId, out var m) ? $"{m.Title} ({m.ReleaseYear})" : "(removed)"),
    }));
});

admin.MapBrowserApi();

app.MapGet("/health", () => Results.Ok("ok"));

app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("Admin page listening on {Urls}", string.Join(", ", app.Urls)));

app.Run();

record LoginRequest(string Username, string Password);

record UserUpdate(bool Enabled, string? Brief, DateTime? CountHistoryFrom);

/// <summary>What the settings page sees. Secrets are never sent back, only whether they're set.</summary>
record SettingsView
{
    public string JellyfinUrl { get; init; } = "";
    public bool JellyfinFromEnvironment { get; init; }
    public bool HasJellyfinApiKey { get; init; }
    public string? JellyfinApiKey { get; init; }
    public string AIProvider { get; init; } = "";
    public bool HasApiKey { get; init; }
    public string? ApiKey { get; init; }
    public string ClassificationModel { get; init; } = "";
    public string ChatModel { get; init; } = "";
    public string CustomEndpoint { get; init; } = "";
    public bool HasTmdbApiKey { get; init; }
    public string? TmdbApiKey { get; init; }
    public bool HasOmdbApiKey { get; init; }
    public string? OmdbApiKey { get; init; }
    public int OmdbPerDay { get; init; } = 1000;
    public bool LetterboxdOn { get; init; } = true;
    public string LetterboxdUrl { get; init; } = "";
    public bool HasLetterboxdToken { get; init; }
    public string? LetterboxdToken { get; init; }
    public int LetterboxdHours { get; init; } = 6;
    public int RefreshHours { get; init; }
    public int PicksPerRow { get; init; }
    public int BecauseRows { get; init; }
    public int BecausePeople { get; init; } = 50;
    public int BecauseCountry { get; init; } = 50;
    public int BecauseEra { get; init; } = 50;
    public List<Guid> ExcludedLibraries { get; init; } = new();
    public int BriefWeight { get; init; }
    public bool MeaningOn { get; init; } = true;
    public List<string>? EmbeddingModels { get; init; }
    public bool? EmbeddingModelsAuto { get; init; }
    public int MeaningWeight { get; init; } = 50;
    public int Discovery { get; init; }
    public string Variety { get; init; } = "medium";
    public string NewArrivals { get; init; } = "some";
    public string Rotation { get; init; } = "medium";
    public double MinRating { get; init; }
    public bool CheckEnabled { get; init; }
    public string CheckModel { get; init; } = "";
    public List<string>? ClassificationModels { get; init; }
    public List<string>? CheckModels { get; init; }
    public bool? ClassificationModelsAuto { get; init; }
    public bool? CheckModelsAuto { get; init; }
    public List<string>? LookupModels { get; init; }
    public bool? LookupModelsAuto { get; init; }
    public int RescanMonths { get; init; }
    public bool HasExaApiKey { get; init; }
    public string? ExaApiKey { get; init; }
    public string WebSearchProvider { get; init; } = "exa";
    public bool HasParallelApiKey { get; init; }
    public string? ParallelApiKey { get; init; }
    public string ParallelMode { get; init; } = "advanced";
    public bool UseExa { get; init; }
    public bool UseParallel { get; init; }
    public double ParallelBudget { get; init; }
    public string WebSearchOrder { get; init; } = "both";
    public List<EngineView>? SearchEngines { get; init; }
    public int SearchPerLookup { get; init; } = 1;
    public bool WikipediaAlways { get; init; } = true;
    public bool HasLinkupApiKey { get; init; }
    public string? LinkupApiKey { get; init; }
    public bool HasTavilyApiKey { get; init; }
    public string? TavilyApiKey { get; init; }

    public sealed record EngineView(string Id, bool On, int Allowance, string Period, double Budget,
        string Name = "", string Note = "", bool NeedsKey = false, bool HasKey = false, bool Free = false);
    public bool WebLookupAuto { get; init; }
    public double WebLookupBudget { get; init; }
    public int WebLookupsPerDay { get; init; }

    public static SettingsView From(ServerSettings s) => new()
    {
        JellyfinUrl = s.JellyfinUrl,
        JellyfinFromEnvironment = s.JellyfinFromEnvironment,
        HasJellyfinApiKey = !string.IsNullOrWhiteSpace(s.JellyfinApiKey),
        AIProvider = s.AIProvider.ToString(),
        HasApiKey = !string.IsNullOrWhiteSpace(s.ApiKey),
        ClassificationModel = s.ClassificationModel,
        ChatModel = s.ChatModel,
        CustomEndpoint = s.CustomEndpoint,
        HasTmdbApiKey = !string.IsNullOrWhiteSpace(s.TmdbApiKey),
        HasOmdbApiKey = !string.IsNullOrWhiteSpace(s.OmdbApiKey),
        OmdbPerDay = s.OmdbPerDay,
        LetterboxdOn = s.LetterboxdOn,
        LetterboxdUrl = s.LetterboxdUrl,
        HasLetterboxdToken = !string.IsNullOrWhiteSpace(s.LetterboxdToken),
        LetterboxdHours = s.LetterboxdHours,
        RefreshHours = s.RefreshHours,
        PicksPerRow = s.PicksPerRow,
        BecauseRows = s.BecauseRows,
        BecausePeople = s.BecausePeople,
        BecauseCountry = s.BecauseCountry,
        BecauseEra = s.BecauseEra,
        ExcludedLibraries = s.ExcludedLibraries,
        BriefWeight = s.BriefWeight,
        MeaningOn = s.MeaningOn,
        EmbeddingModels = Meaning.Order(s),
        EmbeddingModelsAuto = s.EmbeddingModelsAuto,
        MeaningWeight = s.MeaningWeight,
        Discovery = s.Discovery,
        Variety = s.Variety,
        NewArrivals = s.NewArrivals,
        Rotation = s.Rotation,
        MinRating = s.MinRating,
        CheckEnabled = s.CheckEnabled,
        CheckModel = s.CheckModel,
        ClassificationModels = OpenRouterModels.DescribeOrder(s),
        CheckModels = OpenRouterModels.CheckOrder(s),
        ClassificationModelsAuto = s.ClassificationModelsAuto,
        CheckModelsAuto = s.CheckModelsAuto,
        LookupModels = OpenRouterModels.LookupOrder(s),
        LookupModelsAuto = s.LookupModelsAuto,
        RescanMonths = s.RescanMonths,
        HasExaApiKey = !string.IsNullOrWhiteSpace(s.ExaApiKey),
        WebSearchProvider = s.WebSearchProvider,
        HasParallelApiKey = !string.IsNullOrWhiteSpace(s.ParallelApiKey),
        ParallelMode = s.ParallelMode,
        UseExa = s.UseExa ?? s.WebSearchProvider == "exa",
        UseParallel = s.ParallelOn,
        ParallelBudget = s.ParallelBudget,
        WebSearchOrder = s.WebSearchOrder,
        SearchEngines = Pickyfin.Server.Library.SearchEngines.For(s).Select(e =>
        {
            var info = Pickyfin.Server.Library.SearchEngines.Get(e.Id);
            return new EngineView(e.Id, e.On, e.Allowance, e.Period, e.Budget, info.Name, info.Note, info.NeedsKey,
                Pickyfin.Server.Library.SearchEngines.HasKey(s, e.Id), Pickyfin.Server.Library.SearchEngines.Free(s, e.Id));
        }).ToList(),
        SearchPerLookup = WebLookup.PerLookup(s),
        WikipediaAlways = s.WikipediaAlways,
        HasLinkupApiKey = !string.IsNullOrWhiteSpace(s.LinkupApiKey),
        HasTavilyApiKey = !string.IsNullOrWhiteSpace(s.TavilyApiKey),
        WebLookupAuto = s.WebLookupAuto,
        WebLookupBudget = s.WebLookupBudget,
        WebLookupsPerDay = s.WebLookupsPerDay,
    };

    /// <summary>Empty secret fields mean "keep the current value".</summary>
    public void ApplyTo(ServerSettings s)
    {
        if (!s.JellyfinFromEnvironment)
        {
            s.JellyfinUrl = JellyfinUrl;
            if (!string.IsNullOrWhiteSpace(JellyfinApiKey)) s.JellyfinApiKey = JellyfinApiKey.Trim();
        }
        if (Enum.TryParse<AIProviderType>(AIProvider, out var provider)) s.AIProvider = provider;
        if (!string.IsNullOrWhiteSpace(ApiKey)) s.ApiKey = ApiKey.Trim();
        s.ClassificationModel = ClassificationModel.Trim();
        s.ChatModel = ChatModel.Trim();
        s.CustomEndpoint = CustomEndpoint.Trim();
        if (!string.IsNullOrWhiteSpace(TmdbApiKey)) s.TmdbApiKey = TmdbApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(OmdbApiKey)) s.OmdbApiKey = OmdbApiKey.Trim();
        s.OmdbPerDay = Math.Clamp(OmdbPerDay, 1, 1_000_000);
        s.LetterboxdOn = LetterboxdOn;
        s.LetterboxdUrl = (LetterboxdUrl ?? "").Trim().TrimEnd('/');
        s.LetterboxdHours = Math.Clamp(LetterboxdHours, 1, 168);
        // A single "-" clears the token
        if (LetterboxdToken?.Trim() == "-") s.LetterboxdToken = "";
        else if (!string.IsNullOrWhiteSpace(LetterboxdToken)) s.LetterboxdToken = LetterboxdToken.Trim();
        if (!string.IsNullOrWhiteSpace(ExaApiKey)) s.ExaApiKey = ExaApiKey.Trim();
        s.WebSearchProvider = WebSearchProvider == "parallel" ? "parallel" : "exa";
        if (!string.IsNullOrWhiteSpace(ParallelApiKey)) s.ParallelApiKey = ParallelApiKey.Trim();
        s.ParallelMode = ParallelMode == "fast" ? "fast" : "advanced";
        s.UseExa = UseExa;
        s.UseParallel = UseParallel;
        s.ParallelBudget = Math.Clamp(ParallelBudget, 0, 1000);
        s.WebSearchOrder = WebSearchOrder is "exa-first" or "parallel-first" ? WebSearchOrder : "both";
        if (SearchEngines is { Count: > 0 } engines)
            s.SearchEngines = engines.Where(e => Pickyfin.Server.Library.SearchEngines.All.Any(a => a.Id == e.Id)).DistinctBy(e => e.Id)
                .Select(e => new Pickyfin.Server.Library.SearchEngineSetting
                {
                    Id = e.Id, On = e.On, Allowance = Math.Clamp(e.Allowance, 0, 1_000_000),
                    Period = e.Period == "day" ? "day" : "month", Budget = Math.Clamp(e.Budget, 0, 1000),
                }).ToList();
        s.SearchPerLookup = Math.Clamp(SearchPerLookup, 1, 3);
        s.WikipediaAlways = WikipediaAlways;
        if (!string.IsNullOrWhiteSpace(LinkupApiKey)) s.LinkupApiKey = LinkupApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(TavilyApiKey)) s.TavilyApiKey = TavilyApiKey.Trim();
        s.CheckEnabled = CheckEnabled;
        if (!string.IsNullOrWhiteSpace(CheckModel)) s.CheckModel = CheckModel.Trim();
        static List<string> Clean(List<string> list) => list.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // The order only applies to OpenRouter; other providers use the single model fields
        if (s.AIProvider == AIProviderType.OpenRouter && ClassificationModels is { Count: > 0 } describe) { s.ClassificationModels = Clean(describe); s.ClassificationModel = s.ClassificationModels[0]; }
        if (s.AIProvider == AIProviderType.OpenRouter && CheckModels is { Count: > 0 } check) { s.CheckModels = Clean(check); s.CheckModel = s.CheckModels[0]; }
        if (ClassificationModelsAuto is { } describeAuto) s.ClassificationModelsAuto = describeAuto;
        if (CheckModelsAuto is { } checkAuto) s.CheckModelsAuto = checkAuto;
        if (s.AIProvider == AIProviderType.OpenRouter && LookupModels is { Count: > 0 } lookup) s.LookupModels = Clean(lookup);
        if (LookupModelsAuto is { } lookupAuto) s.LookupModelsAuto = lookupAuto;
        s.RescanMonths = RescanMonths is 0 or 1 or 3 or 6 or 12 ? RescanMonths : 0;
        s.WebLookupAuto = WebLookupAuto;
        s.WebLookupBudget = Math.Clamp(WebLookupBudget, 0, 1000);
        s.WebLookupsPerDay = Math.Clamp(WebLookupsPerDay, 0, 2000);
        s.RefreshHours = Math.Clamp(RefreshHours, 1, 168);
        s.PicksPerRow = Math.Clamp(PicksPerRow, 5, 50);
        s.BecauseRows = Math.Clamp(BecauseRows, 0, 2);
        s.BecausePeople = Math.Clamp(BecausePeople, 0, 100);
        s.BecauseCountry = Math.Clamp(BecauseCountry, 0, 100);
        s.BecauseEra = Math.Clamp(BecauseEra, 0, 100);
        s.ExcludedLibraries = ExcludedLibraries.Distinct().ToList();
        s.BriefWeight = Math.Clamp(BriefWeight, 0, 100);
        s.MeaningOn = MeaningOn;
        if (EmbeddingModels is { Count: > 0 } embed) s.EmbeddingModels = Clean(embed);
        if (EmbeddingModelsAuto is { } embedAuto) s.EmbeddingModelsAuto = embedAuto;
        s.MeaningWeight = Math.Clamp(MeaningWeight, 0, 100);
        s.Discovery = Math.Clamp(Discovery, 0, 100);
        s.Variety = Variety is "low" or "medium" or "high" ? Variety : "medium";
        s.NewArrivals = NewArrivals is "off" or "some" or "lots" ? NewArrivals : "some";
        s.Rotation = Rotation is "low" or "medium" or "high" ? Rotation : "medium";
        s.MinRating = Math.Clamp(MinRating, 0, 9);
    }
}

/// <summary>Which hidden films to delete; null = all of them.</summary>
public sealed record GoneRemoval(List<Guid>? Ids);
