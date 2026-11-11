using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pickyfin.Server;

public enum AIProviderType
{
    GoogleAI,
    OpenRouter,
    OpenAI,
    Anthropic,
}

/// <summary>
/// Everything the admin can change on the settings page, stored as JSON in the data
/// folder. The Jellyfin address and API key can also come from the environment
/// (JELLYFIN_URL, JELLYFIN_API_KEY), which then wins over the stored values.
/// </summary>
public sealed class ServerSettings
{
    public string JellyfinUrl { get; set; } = "";
    public string JellyfinApiKey { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AIProviderType AIProvider { get; set; } = AIProviderType.OpenRouter;
    public string ApiKey { get; set; } = "";
    public string ClassificationModel { get; set; } = "google/gemini-2.5-flash";
    /// <summary>Model that picks the recommendations; it reads the brief, so a strong model pays off.</summary>
    public string ChatModel { get; set; } = "google/gemini-2.5-flash";
    public string CustomEndpoint { get; set; } = "";
    public string TmdbApiKey { get; set; } = "";
    public bool ReclassifyOutdatedMovies { get; set; }

    // ---- checking the AI's work ----
    /// <summary>A second model reviews every movie's tags and flags what looks wrong.</summary>
    public bool CheckEnabled { get; set; }
    public string CheckModel { get; set; } = "nvidia/nemotron-3.5-super-vl:free";

    /// <summary>
    /// OpenRouter: the models to try, in order, for describing films and for the second opinion.
    /// Each gets three tries before the next. Empty = the single model above plus the defaults.
    /// The first entry is also kept in ClassificationModel / CheckModel.
    /// </summary>
    public List<string> ClassificationModels { get; set; } = new();
    public List<string> CheckModels { get; set; } = new();
    /// <summary>True: follow OpenRouter's most used free models this week instead of the lists above.</summary>
    public bool ClassificationModelsAuto { get; set; } = true;
    public bool CheckModelsAuto { get; set; } = true;
    /// <summary>Step 3, describing again after a web lookup. Auto = the same order as describing.</summary>
    public List<string> LookupModels { get; set; } = new();
    public bool LookupModelsAuto { get; set; } = true;
    /// <summary>Step 4: re-check every film (fresh lookup, described again) every this many months. 0 = off.</summary>
    public int RescanMonths { get; set; }
    /// <summary>A re-check pass started by hand: every film re-checked before this time is due again.</summary>
    public DateTime? RescanPassStarted { get; set; }
    /// <summary>How many films "describe every film again" queued, for the progress on Status.</summary>
    public int RedoDescribeTotal { get; set; }

    /// <summary>Free models to fall back on until an order is set on the AI page.</summary>
    public static readonly string[] DefaultFreeModels =
    {
        "google/gemma-4-31b-it:free",
        "nvidia/nemotron-3.5-super-vl:free",
        "nvidia/nemotron-3-ultra-550b-a55b:free",
        "google/gemma-4-26b-a4b-it:free",
    };

    public static List<string> Chain(List<string> order, string single) =>
        order.Count > 0 ? order.ToList()
        : new[] { single }.Concat(DefaultFreeModels).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    /// <summary>Exa web search: finds facts about films the AI doesn't know, then they're tagged again.</summary>
    public string ExaApiKey { get; set; } = "";
    public bool WebLookupAuto { get; set; } = true;
    /// <summary>Exa spending stops for the month at this many dollars.</summary>
    public double WebLookupBudget { get; set; } = 9;
    /// <summary>Slowly look up the whole library: this many films a day (0 = off).</summary>
    public int WebLookupsPerDay { get; set; }

    /// <summary>Libraries whose movies are never recommended (and not classified).</summary>
    public List<Guid> ExcludedLibraries { get; set; } = new();
    /// <summary>"Because You Watched …" rows per user, 0 to 2.</summary>
    public int BecauseRows { get; set; } = 2;
    /// <summary>"Because You Watched" rows, 0..100 (50 default): same director/lead actors, uncommon country, nearby years.</summary>
    public int BecausePeople { get; set; } = 50;
    public int BecauseCountry { get; set; } = 50;
    public int BecauseEra { get; set; } = 50;

    public bool IsLibraryExcluded(string? libraries) =>
        ExcludedLibraries.Count > 0 && !string.IsNullOrEmpty(libraries)
        && libraries.Split(',', StringSplitOptions.RemoveEmptyEntries).All(id => Guid.TryParse(id, out var g) && ExcludedLibraries.Contains(g));

    /// <summary>Picks are refreshed at least this often, and also soon after a user finishes something.</summary>
    public int RefreshHours { get; set; } = 24;
    public int PicksPerRow { get; set; } = 20;
    // ---- tuning: what gets picked ----
    /// <summary>0 = follow watch history, 100 = follow the description.</summary>
    public int BriefWeight { get; set; } = 65;
    /// <summary>0 = safe, well-known picks, 100 = lesser-known discoveries.</summary>
    public int Discovery { get; set; } = 40;
    /// <summary>"low", "medium" or "high": how mixed the genres are.</summary>
    public string Variety { get; set; } = "medium";
    /// <summary>"off", "some" or "lots": push movies added in the last 30 days.</summary>
    public string NewArrivals { get; set; } = "some";
    /// <summary>"low", "medium" or "high": how strongly last time's picks move down.</summary>
    public string Rotation { get; set; } = "medium";
    /// <summary>Skip films whose audience rating is below this (0 = off).</summary>
    public double MinRating { get; set; }

    /// <summary>Candidates the AI chooses from per user.</summary>
    public int ShortlistSize { get; set; } = 150;

    // ---- storage ----

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static string _path = "settings.json";

    public static ServerSettings Current { get; private set; } = new();

    public static void Load(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "settings.json");
        lock (Gate)
        {
            var settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(_path), JsonOptions) ?? new()
                : new ServerSettings();
            settings.ApplyEnvironment();
            Current = settings;
        }
    }

    public static void Save(ServerSettings settings)
    {
        lock (Gate)
        {
            settings.ApplyEnvironment();
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
            Current = settings;
        }
    }

    /// <summary>Values from the environment are fixed and can't be changed on the page.</summary>
    [JsonIgnore] public bool JellyfinFromEnvironment => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JELLYFIN_URL"));

    private void ApplyEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("JELLYFIN_URL");
        var key = Environment.GetEnvironmentVariable("JELLYFIN_API_KEY");
        if (!string.IsNullOrWhiteSpace(url)) JellyfinUrl = url;
        if (!string.IsNullOrWhiteSpace(key)) JellyfinApiKey = key;
        JellyfinUrl = JellyfinUrl.Trim().TrimEnd('/');
    }

    public ServerSettings Clone() => JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}
