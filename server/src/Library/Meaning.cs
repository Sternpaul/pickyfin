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
        s.MeaningOn && s.AIProvider == AIProviderType.OpenRouter && !string.IsNullOrWhiteSpace(s.ApiKey);

    /// <summary>The model the films are being matched with right now (the first in the order that works).</summary>
    public static string? ActiveModel { get; private set; }

    // The free embedding models on OpenRouter, most used this week first; refreshed every hour
    private static List<string> _free = new();
    private static DateTime _freeAt;
    private const string Fallback = "nvidia/nemotron-3.5-embed-8b:free";

    /// <summary>The order to try: the free models most used this week, or the admin's own list.</summary>
    public static List<string> Order(ServerSettings s) =>
        !s.EmbeddingModelsAuto && s.EmbeddingModels.Count > 0 ? s.EmbeddingModels.ToList()
        : _free.Count > 0 ? _free.Take(OpenRouterModels.AutoCount).ToList()
        : new List<string> { Fallback };

    private async Task RefreshFreeAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _freeAt < TimeSpan.FromHours(1)) return;
        _freeAt = DateTime.UtcNow;
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var doc = JsonDocument.Parse(await client.GetStringAsync("https://openrouter.ai/api/v1/models?output_modalities=embeddings&sort=top-weekly", cancellationToken));
            var free = doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => id.EndsWith(":free")).ToList();
            if (free.Count > 0) _free = free;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Couldn't read OpenRouter's embedding models: {Error}", error.Message);
        }
    }

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
        // What Letterboxd's most-liked reviewers said; sorted by text, so a change in likes alone doesn't match the film again
        var reviews = m.LbReviewList.Where(r => r.Text.Length >= 40).Take(3).Select(r => r.Text.Length > 400 ? r.Text[..400] : r.Text).OrderBy(t => t, StringComparer.Ordinal).ToList();
        if (reviews.Count > 0) b.Append("\nReviews: " + string.Join(" | ", reviews.Select(r => r.Replace('\n', ' '))));
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
        await RefreshFreeAsync(cancellationToken);
        foreach (var model in Order(s).Where(m => !OpenRouterModels.IsGone(m)))
        {
            ActiveModel = model;
            if (await RunWithAsync(s, model, cancellationToken) != Outcome.Gone) return;
            OpenRouterModels.MarkGone(model);
            _logger.LogWarning("Meaning: {Model} isn't available, every film moves to the next model in the order", model);
        }
        ActiveModel = null;
        Pause(TimeSpan.FromHours(1), "Paused: no model in the order is available", "Meaning: no model in the order is available");
        Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true);
    }

    private enum Outcome { Done, Gone, Paused }

    private async Task<Outcome> RunWithAsync(ServerSettings s, string model, CancellationToken cancellationToken)
    {
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
        if (todo.Count == 0) { Jobs.Idle(Key, Title, "Up to date"); return Outcome.Done; }

        var round = todo.Take(PerRequest * RequestsPerRound).ToList();
        Jobs.Running(Key, Title, $"{round.Count} films");
        var done = 0;
        foreach (var chunk in round.Chunk(PerRequest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (vectors, outcome) = await EmbedWithTriesAsync(s, model, chunk.Select(c => c.Text).ToList(), cancellationToken);
            if (outcome == Outcome.Gone) return Outcome.Gone;
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
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return Outcome.Paused; }
        var left = todo.Count - done;
        Jobs.Idle(Key, Title, left > 0 ? $"{left:N0} to go · {Short(model)}" : $"Up to date · {Short(model)}", left > 0 ? DateTime.UtcNow.AddMinutes(5) : null);
        return Outcome.Done;
    }

    private static string Short(string model) => model.Contains('/') ? model[(model.IndexOf('/') + 1)..].Replace(":free", "") : model;

    /// <summary>Three tries on the same model when it's busy or slow; a model that's gone hands over to the next.</summary>
    private async Task<(List<float[]>? Vectors, Outcome Outcome)> EmbedWithTriesAsync(ServerSettings s, string model, List<string> texts, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            (List<float[]>? Vectors, int Code, string? Error) result;
            using (Activity.Begin($"Matching by meaning: waiting for {model}" + (attempt > 1 ? $" (try {attempt} of 3)" : "")))
                result = await EmbedAsync(s, model, texts, cancellationToken);
            if (result.Vectors is not null) { OpenRouterModels.Record(model, null); return (result.Vectors, Outcome.Done); }
            OpenRouterModels.Record(model, result.Error);
            OpenRouterModels.StepFailures.AddOrUpdate($"meaning|{model}", 1, (_, n) => n + 1);
            if (result.Code is 404 || (result.Code is 400 && (result.Error ?? "").Contains("model", StringComparison.OrdinalIgnoreCase)))
                return (null, Outcome.Gone);
            if (result.Code is 401 or 402 or 403)
            {
                Pause(TimeSpan.FromHours(1), $"Paused: OpenRouter answered HTTP {result.Code}", $"Meaning: {result.Error}");
                return (null, Outcome.Paused);
            }
            if (attempt >= 3)
            {
                Pause(TimeSpan.FromMinutes(15), result.Code == 429 ? "Paused: the model is busy" : "Paused: the model didn't answer", $"Meaning: {model} failed 3 times: {result.Error}");
                return (null, Outcome.Paused);
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 10 : 30), cancellationToken);
        }
    }

    /// <summary>One request to OpenRouter's embeddings endpoint.</summary>
    private async Task<(List<float[]>? Vectors, int Code, string? Error)> EmbedAsync(ServerSettings s, string model, List<string> texts, CancellationToken cancellationToken)
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
            if (!response.IsSuccessStatusCode) return (null, code, $"HTTP {code}: {ErrorText(body) ?? "no details"}");
            var vectors = Parse(body, texts.Count);
            // OpenRouter sometimes answers 200 with an error inside
            if (vectors is null) return (null, ErrorCode(body) ?? 0, $"odd answer: {(body.Length > 200 ? body[..200] : body)}");
            return (vectors, code, null);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (null, 0, $"no answer: {error.Message}");
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

    private static int? ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : null;
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
        if (!On(s)) return Snapshot.Empty;
        var version = Interlocked.Read(ref _version);
        lock (_gate)
            if (_snapshot is { } cached && cached.Version == version) return cached;
        List<Embedding> rows;
        string model;
        await using (var db = _database.Open())
        {
            model = await db.Embeddings.AsNoTracking().Where(e => e.Kind == "film").GroupBy(e => e.Model)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefaultAsync(cancellationToken) ?? "";
            rows = await db.Embeddings.AsNoTracking().Where(e => e.Model == model).ToListAsync(cancellationToken);
        }
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

        private Dictionary<Guid, (double X, double Y)>? _map;

        /// <summary>
        /// Every film as a point on a flat map: the two directions in which the films' meanings
        /// differ most (principal components). Close points mean similar things; the map
        /// flattens a lot, so the closest-films list is the exact answer.
        /// </summary>
        public Dictionary<Guid, (double X, double Y)> Map()
        {
            lock (_ranks)
            {
                if (_map is not null) return _map;
                var ids = Films.Keys.ToList();
                if (ids.Count < 3) return _map = new();
                var dim = Films[ids[0]].Length;
                var rows = ids.Select(id => Films[id]).Where(v => v.Length == dim).ToList();
                var mean = new double[dim];
                foreach (var v in rows) for (var j = 0; j < dim; j++) mean[j] += v[j];
                for (var j = 0; j < dim; j++) mean[j] /= rows.Count;

                double[] Times(double[] v)
                {
                    var scores = rows.Select(r => { double t = 0; for (var j = 0; j < dim; j++) t += (r[j] - mean[j]) * v[j]; return t; }).ToArray();
                    var result = new double[dim];
                    for (var i = 0; i < rows.Count; i++) for (var j = 0; j < dim; j++) result[j] += scores[i] * (rows[i][j] - mean[j]);
                    return result;
                }
                static void Unit(double[] v) { var n = Math.Sqrt(v.Sum(x => x * x)); if (n > 0) for (var j = 0; j < v.Length; j++) v[j] /= n; }
                static void Away(double[] v, double[] from) { var d = 0.0; for (var j = 0; j < v.Length; j++) d += v[j] * from[j]; for (var j = 0; j < v.Length; j++) v[j] -= d * from[j]; }

                var random = new Random(7);
                double[] Component(double[]? before)
                {
                    var v = Enumerable.Range(0, dim).Select(_ => random.NextDouble() - 0.5).ToArray();
                    for (var k = 0; k < 30; k++) { if (before is not null) Away(v, before); Unit(v); v = Times(v); }
                    if (before is not null) Away(v, before);
                    Unit(v);
                    return v;
                }
                var first = Component(null);
                var second = Component(first);
                double Along(float[] r, double[] axis) { double t = 0; for (var j = 0; j < dim; j++) t += (r[j] - mean[j]) * axis[j]; return t; }
                _map = ids.Where(id => Films[id].Length == dim).ToDictionary(id => id, id => (Along(Films[id], first), Along(Films[id], second)));
                return _map;
            }
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
