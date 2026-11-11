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
    object Row(string key, string title, bool on, string off, int? done = null, int? of = null, int queued = 0, string? tip = null)
    {
        var j = Jobs.Get(key);
        var state = !on ? "off" : j is { Running: true } ? "running" : j is { Error: true } ? "paused"
            : queued > 0 || (of is { } o && done < o) ? "waiting" : "idle";
        return new
        {
            Key = key, Title = title, State = state,
            Text = !on ? off : j?.Now ?? "",
            Next = on && state != "running" ? j?.Next : null,
            RunningFor = j is { Running: true } ? (int)(DateTime.UtcNow - j.Since).TotalSeconds : 0,
            Done = done, Of = of, Queued = queued, Tip = tip,
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
            WebLookup.Configured ? "Off" : "Off: no Exa key", lookedUp, classified, tip: Tip("lookup", "Described again from the web with")),
        Row("rescan", "4 · Re-check", (s.RescanMonths > 0 || s.RescanPassStarted is not null) && hasKey, "Off", rescanned, rescanOf, tip: Tip("rescan", "Re-checked with")),
    };

    return Results.Ok(new
    {
        // What is happening this second: the job, what it waits for, which model and which try
        Now = waitingFor ?? Activity.Background ?? (worker.State == "Idle" ? null : worker.State),
        NowDetail = waitingFor is not null ? Activity.Detail : null,
        Waiting = waitingFor is null ? 0 : seconds,
        TaggingLeft = TaggingEta.SecondsLeft,
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
        || before.BriefWeight != settings.BriefWeight || before.Discovery != settings.Discovery || before.Variety != settings.Variety
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
    var librariesChanged = !before.ExcludedLibraries.OrderBy(g => g).SequenceEqual(settings.ExcludedLibraries.OrderBy(g => g));
    worker.Wake(syncLibrary: librariesChanged || before.ApiKey != settings.ApiKey || before.TmdbApiKey != settings.TmdbApiKey
        || (settings.CheckEnabled && (!before.CheckEnabled || before.CheckModel != settings.CheckModel)) || before.ExaApiKey != settings.ExaApiKey);
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
    public int RefreshHours { get; init; }
    public int PicksPerRow { get; init; }
    public int BecauseRows { get; init; }
    public int BecausePeople { get; init; } = 50;
    public int BecauseCountry { get; init; } = 50;
    public int BecauseEra { get; init; } = 50;
    public List<Guid> ExcludedLibraries { get; init; } = new();
    public int BriefWeight { get; init; }
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
        RefreshHours = s.RefreshHours,
        PicksPerRow = s.PicksPerRow,
        BecauseRows = s.BecauseRows,
        BecausePeople = s.BecausePeople,
        BecauseCountry = s.BecauseCountry,
        BecauseEra = s.BecauseEra,
        ExcludedLibraries = s.ExcludedLibraries,
        BriefWeight = s.BriefWeight,
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
        if (!string.IsNullOrWhiteSpace(ExaApiKey)) s.ExaApiKey = ExaApiKey.Trim();
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
        s.Discovery = Math.Clamp(Discovery, 0, 100);
        s.Variety = Variety is "low" or "medium" or "high" ? Variety : "medium";
        s.NewArrivals = NewArrivals is "off" or "some" or "lots" ? NewArrivals : "some";
        s.Rotation = Rotation is "low" or "medium" or "high" ? Rotation : "medium";
        s.MinRating = Math.Clamp(MinRating, 0, 9);
    }
}
