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

admin.MapGet("/status", async (RefreshService worker, LibrarySync library, MovieStore store, LogBuffer log, CancellationToken ct) =>
{
    var (total, classified, issues, reviewed) = await store.GetStatsAsync(ct);
    var (waitingFor, seconds) = Activity.Current;
    return Results.Ok(new
    {
        State = waitingFor is not null && Activity.Detail is { } detail ? $"{waitingFor} ({detail})" : waitingFor ?? Activity.Background ?? worker.State,
        Waiting = waitingFor is null ? 0 : seconds,
        TaggingLeft = TaggingEta.SecondsLeft,
        worker.LastError,
        LibrarySynced = library.LastSync,
        Movies = total,
        Classified = classified,
        Issues = issues,
        CheckEnabled = ServerSettings.Current.CheckEnabled,
        Reviewed = reviewed,
        Jobs = Jobs.Snapshot().Select(j => new { j.Key, j.Title, j.Now, j.Running, j.Error, j.Next,
            RunningFor = j.Running ? (int)(DateTime.UtcNow - j.Since).TotalSeconds : 0 }),
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
    });
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
        || !before.CheckModels.SequenceEqual(settings.CheckModels))
        SecondOpinion.ResetPause();
    if (!before.ClassificationModels.SequenceEqual(settings.ClassificationModels) || before.ApiKey != settings.ApiKey)
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
        ClassificationModels = ServerSettings.Chain(s.ClassificationModels, s.ClassificationModel),
        CheckModels = ServerSettings.Chain(s.CheckModels, s.CheckModel),
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
