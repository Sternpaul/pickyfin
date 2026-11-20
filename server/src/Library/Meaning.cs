using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Curation;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>A film's (or a description's) meaning as a vector from an embedding model.</summary>
public sealed class Embedding
{
    /// <summary>The film's item id, or the person's user id for their description.</summary>
    public Guid ItemId { get; set; }
    /// <summary>film or brief</summary>
    public string Kind { get; set; } = "film";
    public string Model { get; set; } = "";
    /// <summary>Hash of the text and model, so a film is embedded again when its description changes.</summary>
    public string Hash { get; set; } = "";
    public byte[] Vector { get; set; } = Array.Empty<byte>();
    public DateTime At { get; set; }
}

/// <summary>
/// Matching by meaning: every film's whole description (plot, tags, web text) becomes a
/// vector from a free embedding model on OpenRouter, and so does what each person wants to
/// watch. Films whose vectors point the same way are related, even when they share no tag.
/// A batch every round; films are embedded again when their description changes.
/// </summary>
public sealed class Meaning
{
    public const string Key = "meaning", Title = "Related by meaning";
    private const int PerRequest = 16, RequestsPerRound = 8, MaxChars = 6000;

    private readonly IHttpClientFactory _http;
    private readonly Database _database;
    private readonly ILogger<Meaning> _logger;
    private static DateTime? _pausedUntil;
    private static string _pauseReason = "";
    private static long _version;
    private Snapshot? _snapshot;
    private readonly object _gate = new();

    public Meaning(IHttpClientFactory http, Database database, ILogger<Meaning> logger)
    {
        _http = http;
        _database = database;
        _logger = logger;
    }

    public static void ResetPause() => _pausedUntil = null;

    public static bool On(ServerSettings s) =>
        s.AIProvider == AIProviderType.OpenRouter && !string.IsNullOrWhiteSpace(s.ApiKey) && !string.IsNullOrWhiteSpace(s.EmbeddingModel);

    /// <summary>The text a film is understood by: everything we know about it, most telling first.</summary>
    public static string Document(MovieMetadata m)
    {
        var b = new StringBuilder();
        b.Append($"{m.Title}{(m.ReleaseYear is { } y ? $" ({y})" : "")}.");
        if (!string.IsNullOrWhiteSpace(m.OriginalTitle)) b.Append($" Original title: {m.OriginalTitle}.");
        if (!string.IsNullOrWhiteSpace(m.Director)) b.Append($" Directed by {m.Director}.");
        if (!string.IsNullOrWhiteSpace(m.Cast)) b.Append($" With {m.Cast}.");
        if (m.GenreList.Count > 0) b.Append($" Genres: {string.Join(", ", m.GenreList)}.");
        if (!string.IsNullOrWhiteSpace(m.Countries)) b.Append($" From {m.Countries}.");
        if (m.SubcategorySet.Items.Count > 0) b.Append($" Kind: {string.Join(", ", m.SubcategorySet.Items)}.");
        if (m.MoodSet.Items.Count > 0) b.Append($" Mood: {string.Join(", ", m.MoodSet.Items)}.");
        if (m.ThemeSet.Items.Count > 0) b.Append($" Themes: {string.Join(", ", m.ThemeSet.Items)}.");
        if (m.KeywordSet.Items.Count > 0) b.Append($" Keywords: {string.Join(", ", m.KeywordSet.Items.Take(15))}.");
        if (m.Pacing is not null || m.Ending is not null) b.Append($" {m.Pacing} pace, {m.Ending} ending.");
        if (m.BasedOn is { } basis && basis != "original") b.Append($" Based on a {basis}.");
        if (m.SettingEra is not null || m.SettingPlace is not null) b.Append($" Set in {string.Join(", ", new[] { m.SettingEra, m.SettingPlace }.Where(x => x is not null))}.");
        if (!string.IsNullOrWhiteSpace(m.Plot)) b.Append($"\n{m.Plot.Trim()}");
        if (!string.IsNullOrWhiteSpace(m.WebNotes)) b.Append($"\n{m.WebNotes.Trim()}");
        var text = b.ToString();
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    /// <summary>What a person wants: the description's positive summary, else the description itself.</summary>
    public static string? BriefText(UserProfile user)
    {
        var profile = BriefProfile.FromJson(user.BriefProfile);
        var text = !string.IsNullOrWhiteSpace(profile?.LookingFor) ? profile!.LookingFor! : user.Brief;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string Hash(string model, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model + "\n" + text)))[..20];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        if (!On(s)) { Jobs.Idle(Key, Title, s.AIProvider == AIProviderType.OpenRouter ? "Off" : "Off: needs OpenRouter"); return; }
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return; }
        var model = s.EmbeddingModel.Trim();

        List<(Guid Id, string Kind, string Text, string Hash)> todo;
        await using (var db = _database.Open())
        {
            var stored = await db.Embeddings.AsNoTracking().Select(e => new { e.ItemId, e.Hash }).ToDictionaryAsync(e => e.ItemId, e => e.Hash, cancellationToken);
            var films = (await db.Movies.AsNoTracking().Where(m => m.IsClassified).ToListAsync(cancellationToken))
                .Where(m => !s.IsLibraryExcluded(m.Libraries))
                .Select(m => (Id: m.ItemId, Kind: "film", Text: Document(m)));
            var briefs = (await db.Users.AsNoTracking().Where(u => u.Enabled).ToListAsync(cancellationToken))
                .Select(u => (Id: u.UserId, Kind: "brief", Text: BriefText(u)))
                .Where(x => x.Text is not null)
                .Select(x => (x.Id, x.Kind, Text: x.Text!));
            // Descriptions first (few, and they matter for every pick), then films never embedded, then changed ones
            todo = briefs.Concat(films)
                .Select(x => (x.Id, x.Kind, x.Text, Hash: Hash(model, x.Text)))
                .Where(x => !stored.TryGetValue(x.Id, out var h) || h != x.Hash)
                .OrderBy(x => x.Kind == "brief" ? 0 : stored.ContainsKey(x.Id) ? 2 : 1)
                .ToList();
        }
        if (todo.Count == 0) { Jobs.Idle(Key, Title, "Up to date"); return; }

        var round = todo.Take(PerRequest * RequestsPerRound).ToList();
        Jobs.Running(Key, Title, $"{round.Count} films");
        var done = 0;
        foreach (var chunk in round.Chunk(PerRequest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<float[]>? vectors;
            using (Activity.Begin($"Matching by meaning: waiting for {model}"))
                vectors = await EmbedAsync(s, model, chunk.Select(c => c.Text).ToList(), cancellationToken);
            if (vectors is null) break;
            await using (var db = _database.Open())
            {
                var ids = chunk.Select(c => c.Id).ToList();
                var rows = await db.Embeddings.Where(e => ids.Contains(e.ItemId)).ToDictionaryAsync(e => e.ItemId, cancellationToken);
                for (var i = 0; i < chunk.Length; i++)
                {
                    var (id, kind, _, hash) = chunk[i];
                    if (!rows.TryGetValue(id, out var row)) db.Embeddings.Add(row = new Embedding { ItemId = id });
                    row.Kind = kind;
                    row.Model = model;
                    row.Hash = hash;
                    row.Vector = Pack(Normalize(vectors[i]));
                    row.At = DateTime.UtcNow;
                }
                await db.SaveChangesAsync(cancellationToken);
            }
            done += chunk.Length;
            Interlocked.Increment(ref _version);
        }

        if (done > 0) _logger.LogInformation("Meaning: {Done} embedded with {Model}, {Left} to go", done, model, todo.Count - done);
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return; }
        var left = todo.Count - done;
        Jobs.Idle(Key, Title, left > 0 ? $"{left:N0} to go" : "Up to date", left > 0 ? DateTime.UtcNow.AddMinutes(5) : null);
    }

    /// <summary>One request to OpenRouter's embeddings endpoint; null (and paused) on failure.</summary>
    private async Task<List<float[]>?> EmbedAsync(ServerSettings s, string model, List<string> texts, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, input = texts }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey.Trim());
        request.Headers.Add("HTTP-Referer", "https://github.com/Sternpaul/pickyfin");
        request.Headers.Add("X-Title", "Pickyfin");
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var message = ErrorText(body) ?? $"HTTP {code}";
                if (code == 404 || code == 400) Pause(TimeSpan.FromHours(6), $"Paused: {model} isn't available (pick another on the AI page)", $"Meaning: {model}: {message}");
                else if (code == 429) Pause(TimeSpan.FromMinutes(15), "Paused: the model is busy", $"Meaning: busy ({message})");
                else if (code is 401 or 402 or 403) Pause(TimeSpan.FromHours(1), $"Paused: OpenRouter answered HTTP {code}", $"Meaning: {message}");
                else Pause(TimeSpan.FromMinutes(15), $"Paused: OpenRouter answered HTTP {code}", $"Meaning: {message}");
                return null;
            }
            var vectors = Parse(body, texts.Count);
            if (vectors is null) { Pause(TimeSpan.FromMinutes(15), "Paused: odd answer from the model", $"Meaning: odd answer: {(body.Length > 200 ? body[..200] : body)}"); return null; }
            return vectors;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Pause(TimeSpan.FromMinutes(15), "Paused: OpenRouter didn't answer", $"Meaning: no answer: {error.Message}");
            return null;
        }
    }

    /// <summary>Reads {"data":[{"index":0,"embedding":[…]}, …]}; null unless every text got a vector of the same length.</summary>
    public static List<float[]>? Parse(string body, int count)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            var result = new float[count][];
            var position = 0;
            foreach (var item in data.EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i) ? i : position;
                position++;
                if (index < 0 || index >= count || !item.TryGetProperty("embedding", out var e) || e.ValueKind != JsonValueKind.Array) return null;
                result[index] = e.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            }
            if (result.Any(v => v is null || v.Length == 0) || result.Select(v => v.Length).Distinct().Count() != 1) return null;
            return result.ToList();
        }
        catch (Exception) { return null; }
    }

    private static string? ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (Exception) { return null; }
    }

    private void Pause(TimeSpan howLong, string status, string log)
    {
        _pausedUntil = DateTime.UtcNow + howLong;
        _pauseReason = status;
        _logger.LogWarning("{Message}", log);
    }

    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var length = Math.Sqrt(sum);
        if (length <= 0) return v;
        return v.Select(x => (float)(x / length)).ToArray();
    }

    private static byte[] Pack(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] Unpack(byte[] b)
    {
        var v = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    /// <summary>All vectors of the current model, loaded once and kept until new ones arrive.</summary>
    public async Task<Snapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        var model = s.EmbeddingModel?.Trim() ?? "";
        var version = Interlocked.Read(ref _version);
        lock (_gate)
            if (_snapshot is { } cached && cached.Version == version && cached.Model == model) return cached;
        if (!On(s)) return Snapshot.Empty;
        List<Embedding> rows;
        await using (var db = _database.Open())
            rows = await db.Embeddings.AsNoTracking().Where(e => e.Model == model).ToListAsync(cancellationToken);
        var snapshot = new Snapshot(model, version,
            rows.Where(r => r.Kind == "film").ToDictionary(r => r.ItemId, r => Unpack(r.Vector)),
            rows.Where(r => r.Kind == "brief").ToDictionary(r => r.ItemId, r => Unpack(r.Vector)));
        lock (_gate) _snapshot = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Vectors at one moment. A score is where the target ranks among all films for the
    /// source (the closest ones near 1, an average film 0), so models with different
    /// similarity ranges behave the same.
    /// </summary>
    public sealed class Snapshot
    {
        public static readonly Snapshot Empty = new("", -1, new(), new());
        public string Model { get; }
        public long Version { get; }
        public Dictionary<Guid, float[]> Films { get; }
        public Dictionary<Guid, float[]> Briefs { get; }
        private readonly Dictionary<(Guid, bool), Dictionary<Guid, double>> _ranks = new();

        public Snapshot(string model, long version, Dictionary<Guid, float[]> films, Dictionary<Guid, float[]> briefs)
        {
            Model = model;
            Version = version;
            Films = films;
            Briefs = briefs;
        }

        public bool Any => Films.Count >= 20;

        /// <summary>How close a film is to another, 0..0.7 (as strong as a very good tag match at most); null when unknown.</summary>
        public double? Score(Guid source, Guid target) => Rank(source, target, brief: false);

        /// <summary>How close a film is to what this person wants, 0..0.7; null when unknown.</summary>
        public double? BriefScore(Guid userId, Guid target) => Rank(userId, target, brief: true);

        private double? Rank(Guid source, Guid target, bool brief)
        {
            if (!Any || source == target && !brief) return null;
            if (!Films.ContainsKey(target)) return null;
            Dictionary<Guid, double> ranks;
            lock (_ranks)
            {
                if (!_ranks.TryGetValue((source, brief), out ranks!))
                {
                    var from = brief ? Briefs.GetValueOrDefault(source) : Films.GetValueOrDefault(source);
                    if (from is null) return null;
                    var ordered = Films.Where(f => f.Key != source)
                        .Select(f => (Id: f.Key, Cos: Dot(from, f.Value)))
                        .OrderBy(x => x.Cos).ToList();
                    ranks = new Dictionary<Guid, double>(ordered.Count);
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        var percentile = ordered.Count == 1 ? 1 : i / (double)(ordered.Count - 1);
                        var above = Math.Max(0, (percentile - 0.5) / 0.5);
                        ranks[ordered[i].Id] = 0.7 * above * above;
                    }
                    _ranks[(source, brief)] = ranks;
                }
            }
            return ranks.TryGetValue(target, out var r) ? r : null;
        }

        private static double Dot(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0;
            double sum = 0;
            for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
            return sum;
        }
    }

    /// <summary>Tags and meaning together: weight 0 = tags only, 1 = meaning only.</summary>
    public static double Blend(double tags, double? meaning, double weight) =>
        meaning is { } m && weight > 0 ? tags * (1 - weight) + m * weight : tags;
}
