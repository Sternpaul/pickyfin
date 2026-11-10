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

    /// <summary>Libraries whose movies are never recommended (and not classified).</summary>
    public List<Guid> ExcludedLibraries { get; set; } = new();
    /// <summary>"Because You Watched …" rows per user, 0 to 2.</summary>
    public int BecauseRows { get; set; } = 2;

    public bool IsLibraryExcluded(string? libraries) =>
        ExcludedLibraries.Count > 0 && !string.IsNullOrEmpty(libraries)
        && libraries.Split(',', StringSplitOptions.RemoveEmptyEntries).All(id => Guid.TryParse(id, out var g) && ExcludedLibraries.Contains(g));

    /// <summary>Picks are refreshed at least this often, and also soon after a user finishes something.</summary>
    public int RefreshHours { get; set; } = 24;
    public int PicksPerRow { get; set; } = 20;
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
