using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pickyfin.Server.Jellyfin;

public sealed record JellyfinUser(Guid Id, string Name, bool IsAdministrator, bool IsDisabled);

/// <summary>A movie as Jellyfin returns it, with only the fields this service reads.</summary>
public sealed class JellyfinItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int? ProductionYear { get; set; }
    public string? Overview { get; set; }
    public float? CommunityRating { get; set; }
    public float? CriticRating { get; set; }
    public DateTime? DateCreated { get; set; }
    public List<string>? Genres { get; set; }
    public Dictionary<string, string>? ProviderIds { get; set; }
    public List<JellyfinPerson>? People { get; set; }
    public JellyfinUserData? UserData { get; set; }
}

public sealed class JellyfinPerson
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
}

public sealed class JellyfinUserData
{
    public bool Played { get; set; }
    public int PlayCount { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime? LastPlayedDate { get; set; }
    public long PlaybackPositionTicks { get; set; }
}

public sealed class JellyfinException(string message) : Exception(message);

/// <summary>
/// Talks to Jellyfin's web API with an API key. An API key acts as an administrator,
/// so it can read every user's library and history and create playlists for them.
/// </summary>
public sealed class JellyfinClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new FlexibleGuidConverter() },
    };

    private const string MovieFields = "Overview,ProviderIds,People,Genres,DateCreated,CriticRating,CommunityRating,ProductionYear";

    private readonly HttpClient _http;

    public JellyfinClient(HttpClient http) => _http = http;

    private static ServerSettings Settings => ServerSettings.Current;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Settings.JellyfinUrl) && !string.IsNullOrWhiteSpace(Settings.JellyfinApiKey);

    private HttpRequestMessage Request(HttpMethod method, string path, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(Settings.JellyfinUrl)) throw new JellyfinException("Jellyfin address is not set.");
        var request = new HttpRequestMessage(method, Settings.JellyfinUrl + path);
        var auth = "MediaBrowser Client=\"Pickyfin Server\", Device=\"Pickyfin Server\", DeviceId=\"pickyfin-server\", Version=\"1.0\"";
        var key = token ?? Settings.JellyfinApiKey;
        if (!string.IsNullOrWhiteSpace(key)) auth += $", Token=\"{key}\"";
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new JellyfinException($"Jellyfin {request.Method} {request.RequestUri?.AbsolutePath} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new JellyfinException($"Jellyfin {request.Method} {request.RequestUri?.AbsolutePath} failed: {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private sealed class ItemsPage
    {
        public List<JellyfinItem> Items { get; set; } = new();
        public int TotalRecordCount { get; set; }
    }

    private sealed class UserDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public PolicyDto? Policy { get; set; }
    }

    private sealed class PolicyDto
    {
        public bool IsAdministrator { get; set; }
        public bool IsDisabled { get; set; }
    }

    public async Task<string> GetServerNameAsync(CancellationToken cancellationToken = default)
    {
        using var document = await SendAsync<JsonDocument>(Request(HttpMethod.Get, "/System/Info"), cancellationToken);
        return document.RootElement.GetProperty("ServerName").GetString() ?? "Jellyfin";
    }

    public async Task<List<JellyfinUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await SendAsync<List<UserDto>>(Request(HttpMethod.Get, "/Users"), cancellationToken);
        return users.Select(u => new JellyfinUser(u.Id, u.Name, u.Policy?.IsAdministrator ?? false, u.Policy?.IsDisabled ?? false)).ToList();
    }

    /// <summary>All movies the given user may see, with their watch state.</summary>
    public async Task<List<JellyfinItem>> GetMoviesAsync(Guid userId, bool withDetails, CancellationToken cancellationToken = default)
    {
        var fields = withDetails ? MovieFields : "ProductionYear";
        var all = new List<JellyfinItem>();
        const int pageSize = 500;
        for (var start = 0; ; start += pageSize)
        {
            var page = await SendAsync<ItemsPage>(Request(HttpMethod.Get,
                $"/Items?userId={userId}&Recursive=true&IncludeItemTypes=Movie&Fields={fields}&EnableImages=false&EnableUserData=true&StartIndex={start}&Limit={pageSize}"),
                cancellationToken);
            all.AddRange(page.Items);
            if (page.Items.Count < pageSize) break;
        }
        return all;
    }

    /// <summary>When the user last finished a movie, or null if never. A cheap way to notice new watches.</summary>
    public async Task<DateTime?> GetLastPlayedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var page = await SendAsync<ItemsPage>(Request(HttpMethod.Get,
            $"/Items?userId={userId}&Recursive=true&IncludeItemTypes=Movie&IsPlayed=true&SortBy=DatePlayed&SortOrder=Descending&Limit=1&EnableImages=false&EnableUserData=true"),
            cancellationToken);
        return page.Items.FirstOrDefault()?.UserData?.LastPlayedDate;
    }

    public sealed record Library(Guid Id, string Name, string? CollectionType);

    private sealed class VirtualFolder
    {
        public string Name { get; set; } = "";
        public string? CollectionType { get; set; }
        public Guid ItemId { get; set; }
    }

    /// <summary>Libraries that can hold movies (movie libraries and mixed ones).</summary>
    public async Task<List<Library>> GetMovieLibrariesAsync(CancellationToken cancellationToken = default)
    {
        var folders = await SendAsync<List<VirtualFolder>>(Request(HttpMethod.Get, "/Library/VirtualFolders"), cancellationToken);
        return folders
            .Where(f => f.CollectionType is null or "" or "movies" or "mixed" or "homevideos")
            .Select(f => new Library(f.ItemId, f.Name, f.CollectionType))
            .ToList();
    }

    /// <summary>Ids of all movies in one library.</summary>
    public async Task<List<Guid>> GetLibraryMovieIdsAsync(Guid userId, Guid libraryId, CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>();
        const int pageSize = 2000;
        for (var start = 0; ; start += pageSize)
        {
            var page = await SendAsync<ItemsPage>(Request(HttpMethod.Get,
                $"/Items?userId={userId}&ParentId={libraryId}&Recursive=true&IncludeItemTypes=Movie&EnableImages=false&EnableUserData=false&StartIndex={start}&Limit={pageSize}"),
                cancellationToken);
            ids.AddRange(page.Items.Select(i => i.Id));
            if (page.Items.Count < pageSize) break;
        }
        return ids;
    }


    private sealed class CreatedPlaylist
    {
        public Guid Id { get; set; }
    }

    /// <summary>Creates a private playlist owned by the user.</summary>
    public async Task<Guid> CreatePlaylistAsync(Guid userId, string name, IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        var request = Request(HttpMethod.Post, "/Playlists");
        request.Content = JsonContent.Create(new
        {
            Name = name,
            Ids = itemIds,
            UserId = userId,
            MediaType = "Video",
            IsPublic = false,
        }, options: Json);
        var created = await SendAsync<CreatedPlaylist>(request, cancellationToken);
        return created.Id;
    }

    /// <summary>Deletes an item (here: one of our playlists). A playlist already gone is fine.</summary>
    public async Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Delete, $"/Items/{itemId}"), cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new JellyfinException($"Jellyfin could not delete playlist {itemId}: {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    /// <summary>Names and ids of the playlists the user can see.</summary>
    public async Task<List<JellyfinItem>> GetPlaylistsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var page = await SendAsync<ItemsPage>(Request(HttpMethod.Get,
            $"/Items?userId={userId}&Recursive=true&IncludeItemTypes=Playlist&EnableImages=false"), cancellationToken);
        return page.Items;
    }

    // ---- admin login for the settings page ----

    private sealed class AuthResult
    {
        public UserDto? User { get; set; }
        public string? AccessToken { get; set; }
    }

    /// <summary>Checks a Jellyfin username and password; returns the user if they're an administrator.</summary>
    public async Task<JellyfinUser?> AuthenticateAdminAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var request = Request(HttpMethod.Post, "/Users/AuthenticateByName", token: "");
        request.Content = JsonContent.Create(new { Username = username, Pw = password }, options: Json);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var result = await response.Content.ReadFromJsonAsync<AuthResult>(Json, cancellationToken);
        if (result?.User is not { } user || user.Policy?.IsAdministrator != true) return null;

        // Log the session we just opened out again; the page keeps its own cookie
        if (!string.IsNullOrEmpty(result.AccessToken))
        {
            try { await SendAsync(Request(HttpMethod.Post, "/Sessions/Logout", result.AccessToken), cancellationToken); }
            catch (JellyfinException) { }
        }
        return new JellyfinUser(user.Id, user.Name, true, false);
    }
}
