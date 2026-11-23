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
    private const int MaxChars = 6000;

    private readonly IHttpClientFactory _http;
    private readonly Database _database;
    private readonly ILogger<Meaning> _logger;
    private static DateTime? _pausedUntil;
    private static string _pauseReason = "";
    private static long _version;
    private Snapshot? _snapshot;
    private string _snapshotKey = "";
    private readonly object _gate = new();

    public Meaning(IHttpClientFactory http, Database database, ILogger<Meaning> logger)
    {
        _http = http;
        _database = database;
        _logger = logger;
    }

    public static void ResetPause()
    {
        _pausedUntil = null;
        lock (Resting) Resting.Clear();
    }

    public static bool On(ServerSettings s) =>
        s.MeaningOn && s.AIProvider == AIProviderType.OpenRouter && Providers.AnyEmbeddingKey(s);

    /// <summary>The model being asked right now.</summary>
    public static string? ActiveModel { get; private set; }

    /// <summary>A model that was busy or failed: skipped until then; the other models carry on.</summary>
    private static readonly Dictionary<string, (DateTime Until, string Why)> Resting = new();
    /// <summary>Per model: how many films and descriptions it still has to do (for Status).</summary>
    public static Dictionary<string, int> Left { get; private set; } = new();

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
        // A film's text must stay exactly as it was: any change would embed every film again
        if (m.IsSeries)
        {
            var run = new[] { Ai.ClassificationPrompt.Aired(m), m.Seasons is { } n ? $"{n} season{(n == 1 ? "" : "s")}" : null, m.Format }.Where(x => x is not null);
            b.Append($"{m.Title}. A TV series ({string.Join(", ", run)}).".Replace(" ().", "."));
            if (!string.IsNullOrWhiteSpace(m.Network)) b.Append($" On {m.Network}.");
        }
        else b.Append($"{m.Title}{(m.ReleaseYear is { } y ? $" ({y})" : "")}.");
        if (!string.IsNullOrWhiteSpace(m.OriginalTitle)) b.Append($" Original title: {m.OriginalTitle}.");
        if (!string.IsNullOrWhiteSpace(m.Director)) b.Append(m.IsSeries ? $" Created by {m.Director}." : $" Directed by {m.Director}.");
        if (!string.IsNullOrWhiteSpace(m.Cast)) b.Append($" With {m.Cast}.");
        if (m.GenreList.Count > 0) b.Append($" Genres: {string.Join(", ", m.GenreList)}.");
        if (!string.IsNullOrWhiteSpace(m.Countries)) b.Append($" From {m.Countries}.");
        if (m.SubcategorySet.Items.Count > 0) b.Append($" Kind: {string.Join(", ", m.SubcategorySet.Items)}.");
        if (m.IsSeries && m.SeriesKindSet.Count > 0) b.Append($" TV: {string.Join(", ", m.SeriesKindSet.Items)}.");
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

    /// <summary>
    /// Every model in the order builds its own matrix: each gets its batch every round. A model
    /// that's busy or failing is skipped for a while and the others carry on; one that's gone
    /// drops out. Scores are the average over the models that know both films.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        if (!On(s)) { Jobs.Idle(Key, Title, s.AIProvider == AIProviderType.OpenRouter ? "Off" : "Off: needs OpenRouter"); return; }
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return; }
        await RefreshFreeAsync(cancellationToken);
        var models = Order(s).Where(m => !OpenRouterModels.IsGone(m) && Providers.HasKey(s, m)).ToList();
        if (models.Count == 0 && Order(s).Any(m => !Providers.HasKey(s, m)))
        {
            Jobs.Idle(Key, Title, $"Off: the order needs a key for {string.Join(" or ", Order(s).Where(m => !Providers.HasKey(s, m)).Select(Providers.Name).Distinct())}", error: true);
            return;
        }
        if (models.Count == 0)
        {
            Pause(TimeSpan.FromHours(1), "Paused: no model in the order is available", "Meaning: no model in the order is available");
            Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true);
            return;
        }

        var todo = await TodoAsync(s, models, cancellationToken);
        Left = models.ToDictionary(m => m, m => todo[m].Count);
        if (todo.Values.All(t => t.Count == 0)) { Jobs.Idle(Key, Title, $"Up to date · {models.Count} model{(models.Count == 1 ? "" : "s")}"); return; }

        var done = 0;
        foreach (var model in models)
        {
            if (todo[model].Count == 0) continue;
            if (IsResting(model)) continue;
            ActiveModel = model;
            Jobs.Running(Key, Title, $"{Short(model)}: {todo[model].Count:N0} to match");
            var (count, outcome) = await RunWithAsync(s, model, todo[model], cancellationToken);
            done += count;
            Left[model] = todo[model].Count - count;
            if (outcome == Outcome.Gone)
            {
                OpenRouterModels.MarkGone(model);
                _logger.LogWarning("Meaning: {Model} isn't available any more ({Error}); the other models carry on",
                    model, OpenRouterModels.Stats.TryGetValue(model, out var st) ? st.LastError : "no details");
            }
            if (_pausedUntil > DateTime.UtcNow) break; // the key itself was refused
        }
        ActiveModel = null;
        if (_pausedUntil > DateTime.UtcNow) { Jobs.Idle(Key, Title, _pauseReason, _pausedUntil, error: true); return; }

        var left = Left.Values.Sum();
        var working = models.Count(m => !OpenRouterModels.IsGone(m) && !IsResting(m));
        Jobs.Idle(Key, Title,
            left > 0 ? $"{left:N0} to match (new films, or descriptions that changed) · {working} of {models.Count} models working right now" : $"Up to date · {models.Count} models",
            left > 0 ? DateTime.UtcNow.AddMinutes(5) : null, error: working == 0);
    }

    private static bool IsResting(string model)
    {
        if (Providers.RefusedWhy(model) is not null) return true;
        lock (Resting) return Resting.TryGetValue(model, out var r) && r.Until > DateTime.UtcNow;
    }

    /// <summary>Why a model is skipped right now, for Status; null when it's working.</summary>
    public static string? RestingWhy(string model)
    {
        if (Providers.RefusedWhy(model) is { } refused) return $"{refused}; tried again within the hour";
        lock (Resting) return Resting.TryGetValue(model, out var r) && r.Until > DateTime.UtcNow ? r.Why : null;
    }

    private enum Outcome { Done, Gone, Paused, Refused }

    /// <summary>
    /// Texts a model refused on their own (HTTP 400), per model and text hash: skipped until
    /// their description changes or the server restarts, so one odd text can't block the rest.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> RefusedTexts = new();
    public static int RefusedCount(string model)
    {
        lock (RefusedTexts) return RefusedTexts.TryGetValue(model, out var set) ? set.Count : 0;
    }
    private static bool IsRefused(string model, string hash)
    {
        lock (RefusedTexts) return RefusedTexts.TryGetValue(model, out var set) && set.Contains(hash);
    }

    /// <summary>
    /// What is sent: the document without web addresses (some servers read a link to a picture
    /// as an image to fetch, and links carry no meaning anyway), cut shorter for models with a
    /// small window. The hash stays on the full document, so this doesn't match films again.
    /// </summary>
    public static string Sendable(string text, int maxChars = MaxChars)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(text, @"(https?://|www\.)\S+|data:\S+", "");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"[ \t]{2,}", " ").Trim();
        return clean.Length > maxChars ? clean[..maxChars] : clean;
    }

    /// <summary>Per model, what it still has to embed: descriptions first, then films it never had, then changed ones.</summary>
    private async Task<Dictionary<string, List<(Guid Id, string Kind, string Text, string Hash)>>> TodoAsync(ServerSettings s, List<string> models, CancellationToken cancellationToken)
    {
        await using var db = _database.Open();
        var stored = (await db.Embeddings.AsNoTracking().Select(e => new { e.ItemId, e.Model, e.Hash }).ToListAsync(cancellationToken))
            .ToDictionary(e => (e.ItemId, e.Model), e => e.Hash);
        var films = (await db.Movies.AsNoTracking().Where(m => m.IsClassified).ToListAsync(cancellationToken))
            .Where(m => !s.IsLibraryExcluded(m.Libraries))
            .OrderBy(m => m.IsSeries) // films first
            .Select(m => (Id: m.ItemId, Kind: "film", Text: Document(m))).ToList();
        var briefs = (await db.Users.AsNoTracking().Where(u => u.Enabled).ToListAsync(cancellationToken))
            .Select(u => (Id: u.UserId, Kind: "brief", Text: BriefText(u)))
            .Where(x => x.Text is not null)
            .Select(x => (x.Id, x.Kind, Text: x.Text!)).ToList();
        return models.ToDictionary(model => model, model => briefs.Concat(films)
            .Select(x => (x.Id, x.Kind, x.Text, Hash: Hash(model, x.Text)))
            .Where(x => (!stored.TryGetValue((x.Id, model), out var h) || h != x.Hash) && !IsRefused(model, x.Hash))
            .OrderBy(x => x.Kind == "brief" ? 0 : stored.ContainsKey((x.Id, model)) ? 2 : 1)
            .ToList());
    }

    private async Task<(int Done, Outcome Outcome)> RunWithAsync(ServerSettings s, string model, List<(Guid Id, string Kind, string Text, string Hash)> todo, CancellationToken cancellationToken)
    {
        var round = todo.Take(Providers.Batch(model) * Providers.PerRound(model)).ToList();
        var done = 0;
        foreach (var chunk in round.GroupBy(c => c.Kind).SelectMany(g => g.Chunk(Providers.Batch(model))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (vectors, outcome, error) = await EmbedWithTriesAsync(s, model, chunk[0].Kind, chunk.Select(c => Sendable(c.Text)).ToList(), cancellationToken);
            if (outcome == Outcome.Gone) return (done, Outcome.Gone);
            var items = chunk.ToList();
            if (outcome == Outcome.Refused)
            {
                // One text may be the problem: send them one by one, keep what works, set the rest aside
                var (kept, keptVectors, refused) = await OneByOneAsync(s, model, items, cancellationToken);
                if (kept.Count == 0)
                {
                    var until = DateTime.UtcNow.AddHours(6);
                    lock (Resting) Resting[model] = (until, $"refuses every request ({error}); tried again at {until.ToLocalTime():HH:mm}");
                    _logger.LogWarning("Meaning: {Model} refuses every text, even one at a time ({Error}); the other models carry on, it's tried again in 6 hours", model, error);
                    return (done, Outcome.Paused);
                }
                if (refused.Count > 0)
                    _logger.LogWarning("Meaning: {Model} refused {Count} text(s) ({Error}), set aside: {Which}", model, refused.Count, error,
                        string.Join("; ", refused.Select(t => t.Text.Split('\n')[0]).Select(t => t.Length > 60 ? t[..60] + "…" : t)));
                items = kept;
                vectors = keptVectors;
            }
            if (vectors is null) return (done, Outcome.Paused);
            await using (var db = _database.Open())
            {
                var ids = items.Select(c => c.Id).ToList();
                var rows = await db.Embeddings.Where(e => e.Model == model && ids.Contains(e.ItemId)).ToDictionaryAsync(e => e.ItemId, cancellationToken);
                for (var i = 0; i < items.Count; i++)
                {
                    var (id, kind, _, hash) = items[i];
                    if (!rows.TryGetValue(id, out var row)) db.Embeddings.Add(row = new Embedding { ItemId = id, Model = model });
                    row.Kind = kind;
                    row.Hash = hash;
                    row.Vector = Pack(Normalize(vectors[i]));
                    row.At = DateTime.UtcNow;
                }
                await db.SaveChangesAsync(cancellationToken);
            }
            done += items.Count;
            Interlocked.Increment(ref _version);
        }
        if (done > 0) _logger.LogInformation("Meaning: {Done} embedded with {Model}, {Left} to go", done, model, todo.Count - done);
        return (done, Outcome.Done);
    }

    /// <summary>After a refused batch: each text alone, and a shorter version of any the model still refuses.</summary>
    private async Task<(List<(Guid Id, string Kind, string Text, string Hash)> Kept, List<float[]> Vectors, List<(Guid Id, string Kind, string Text, string Hash)> Refused)> OneByOneAsync(
        ServerSettings s, string model, List<(Guid Id, string Kind, string Text, string Hash)> items, CancellationToken cancellationToken)
    {
        var kept = new List<(Guid Id, string Kind, string Text, string Hash)>();
        var vectors = new List<float[]>();
        var refused = new List<(Guid Id, string Kind, string Text, string Hash)>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[]? vector = null;
            // full text, then a shorter one (the model's window may be small)
            foreach (var limit in new[] { MaxChars, 2000 })
            {
                using (Activity.Begin($"Matching by meaning: {model}, one text at a time"))
                {
                    var one = await EmbedAsync(s, model, item.Kind, new List<string> { Sendable(item.Text, limit) }, cancellationToken);
                    if (one.Vectors is { Count: 1 }) { vector = one.Vectors[0]; break; }
                    if (one.Code is not 400) break; // busy or down: leave it for the next round
                }
            }
            if (vector is not null) { kept.Add(item); vectors.Add(vector); }
            else
            {
                refused.Add(item);
                lock (RefusedTexts)
                {
                    if (!RefusedTexts.TryGetValue(model, out var set)) RefusedTexts[model] = set = new();
                    set.Add(item.Hash);
                }
            }
        }
        // nothing worked at all: don't set the texts aside, the model itself is the problem
        if (kept.Count == 0)
            lock (RefusedTexts)
                if (RefusedTexts.TryGetValue(model, out var set)) foreach (var t in refused) set.Remove(t.Hash);
        return (kept, vectors, refused);
    }

    public static string Short(string model)
    {
        var id = Providers.ApiModel(model);
        var name = id[(id.LastIndexOf('/') + 1)..].Replace(":free", "");
        return Providers.IsOpenRouter(model) ? name : $"{name} ({Providers.Name(model)})";
    }

    /// <summary>Three tries on the same model when it's busy or slow; a model that's gone hands over to the next.</summary>
    private async Task<(List<float[]>? Vectors, Outcome Outcome, string? Error)> EmbedWithTriesAsync(ServerSettings s, string model, string kind, List<string> texts, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            (List<float[]>? Vectors, int Code, string? Error) result;
            using (Activity.Begin($"Matching by meaning: waiting for {model}" + (attempt > 1 ? $" (try {attempt} of 3)" : "")))
                result = await EmbedAsync(s, model, kind, texts, cancellationToken);
            if (result.Vectors is not null) { OpenRouterModels.Record(model, null); return (result.Vectors, Outcome.Done, null); }
            OpenRouterModels.Record(model, result.Error);
            OpenRouterModels.StepFailures.AddOrUpdate($"meaning|{model}", 1, (_, n) => n + 1);
            // 404 or 410 (end of life): the model is gone; no need to try again
            if (result.Code is 404 or 410 || (result.Code is 400 && LooksGone(result.Error)))
            {
                if (Nim.Is(model) && Nim.SaysNotServed(result.Code, result.Error)) Nim.MarkNotServed(model);
                return (null, Outcome.Gone, result.Error);
            }
            // Any other 400 (or 422): something in this request is refused; trying the same again won't help
            if (result.Code is 400 or 422) return (null, Outcome.Refused, result.Error);
            if (result.Code is 401 or 402 or 403)
            {
                // that provider's models wait an hour; the other provider's carry on
                Providers.Refuse(model, $"{Providers.Name(model)} refused the key (HTTP {result.Code})");
                _logger.LogWarning("Meaning: {Provider} refused the key ({Error}); its models wait an hour, the others carry on", Providers.Name(model), result.Error);
                return (null, Outcome.Paused, result.Error);
            }
            if (attempt >= 3)
            {
                // skipped this round only; the other models carry on, and it's asked again next round
                lock (Resting) Resting[model] = (DateTime.UtcNow.AddMinutes(4), result.Code is 429 or 503 ? "busy at OpenRouter" : "didn't answer");
                _logger.LogWarning("Meaning: {Model} failed 3 times ({Error}); the other models carry on", model, result.Error);
                return (null, Outcome.Paused, result.Error);
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 10 : 30), cancellationToken);
        }
    }

    /// <summary>
    /// What is sent to the embeddings endpoint. NVIDIA's, Jina's and Voyage's models want to know whether a
    /// text is something searched for (a person's wishes) or something found (a film), and cut
    /// texts longer than their window instead of refusing them. The others get the plain request.
    /// </summary>
    public static string Body(string model, string kind, List<string> texts, int variant = 0)
    {
        var id = Providers.ApiModel(model);
        var query = kind == "brief";
        var source = Providers.Of(model);
        if (source == Providers.Nvidia)
            return variant switch
            {
                0 => JsonSerializer.Serialize(new { model = id, input = texts, input_type = query ? "query" : "passage", truncate = "END", encoding_format = "float" }),
                1 => JsonSerializer.Serialize(new { model = id, input = texts, input_type = query ? "query" : "passage" }),
                _ => JsonSerializer.Serialize(new { model = id, input = texts }),
            };
        if (source == Providers.Jina && variant == 0)
            return JsonSerializer.Serialize(new { model = id, input = texts, task = query ? "retrieval.query" : "retrieval.passage", truncate = true });
        if (source == Providers.Voyage && variant == 0)
            return JsonSerializer.Serialize(new { model = id, input = texts, input_type = query ? "query" : "document", truncation = true });
        return JsonSerializer.Serialize(new { model = id, input = texts });
    }

    /// <summary>How many request shapes a model's source has (see Body), most fields first.</summary>
    private static int Variants(string model) => Providers.Of(model) == Providers.Nvidia ? 3 : Providers.Of(model) == Providers.Jina || Providers.Of(model) == Providers.Voyage ? 2 : 1;

    /// <summary>
    /// Not every model takes the extra fields: some need input_type, some refuse it or truncate.
    /// A refused request is sent again with fewer fields; what worked is kept per model.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FieldVariant = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether an error says the model itself doesn't exist, not that the request was wrong.</summary>
    public static bool LooksGone(string? error) =>
        error is not null && System.Text.RegularExpressions.Regex.IsMatch(error,
            @"not a valid model|model .*(not found|does not exist|unknown|no longer)|(unknown|invalid|no such) model|no endpoints|end of life|deprecated|function .*not found",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>One request to the model's embeddings endpoint, at its own source.</summary>
    private async Task<(List<float[]>? Vectors, int Code, string? Error)> EmbedAsync(ServerSettings s, string model, string kind, List<string> texts, CancellationToken cancellationToken)
    {
        if (Variants(model) == 1) return await EmbedOnceAsync(s, model, kind, texts, 0, cancellationToken);
        var known = FieldVariant.TryGetValue(model, out var v) ? v : 0;
        var first = await EmbedOnceAsync(s, model, kind, texts, known, cancellationToken);
        if (first.Vectors is not null || first.Code is not (400 or 422) || known > 0) return first;
        // refused with every field: try with fewer, and remember what worked
        for (var variant = 1; variant < Variants(model); variant++)
        {
            var next = await EmbedOnceAsync(s, model, kind, texts, variant, cancellationToken);
            if (next.Vectors is not null)
            {
                FieldVariant[model] = variant;
                _logger.LogInformation("Meaning: {Model} refused some request fields ({Error}); it gets fewer from now on", model, first.Error);
                return next;
            }
            if (next.Code is not (400 or 422)) return next;
        }
        return first;
    }

    private async Task<(List<float[]>? Vectors, int Code, string? Error)> EmbedOnceAsync(ServerSettings s, string model, string kind, List<string> texts, int variant, CancellationToken cancellationToken)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        using var request = new HttpRequestMessage(HttpMethod.Post, Providers.EmbedUrl(s, model))
        {
            Content = new StringContent(Body(model, kind, texts, variant), Encoding.UTF8, "application/json"),
        };
        Providers.Authorize(request, s, model);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var why = System.Text.RegularExpressions.Regex.Replace(ErrorText(body) ?? "no details", @"^HTTP \d+:\s*", "");
                return (null, code, $"HTTP {code}: {why}");
            }
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
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.String ? e.GetString() : e.TryGetProperty("message", out var m) ? m.GetString() : null;
            // NVIDIA answers {"title": …, "detail": …}
            if (root.TryGetProperty("detail", out var d)) return d.ValueKind == JsonValueKind.String ? d.GetString() : d.ToString();
            return root.TryGetProperty("title", out var t) ? t.GetString() : null;
        }
        catch (Exception) { return body.Length > 0 && body.Length < 300 ? body.Trim() : null; }
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

    internal static byte[] Pack(float[] v)
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

    /// <summary>All models' vectors, loaded once and kept until new ones arrive.</summary>
    public async Task<Snapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var s = ServerSettings.Current;
        if (!On(s)) return Snapshot.Empty;
        var version = Interlocked.Read(ref _version);
        // Models in use right now. Every model's stored vectors keep counting, also when it was
        // switched off or retired by OpenRouter: films never change, so old vectors stay right.
        // Only a person's wishes change, so a model that no longer updates them leaves them out.
        var order = Order(s).Where(m => !OpenRouterModels.IsGone(m)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var key = string.Join("|", order.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
        lock (_gate)
            if (_snapshot is { } cached && cached.Version == version && _snapshotKey == key) return cached;
        List<Embedding> rows;
        HashSet<Guid> visible, series;
        await using (var db = _database.Open())
        {
            rows = await db.Embeddings.AsNoTracking().ToListAsync(cancellationToken);
            // films Jellyfin no longer has (hidden until the admin confirms) don't count
            visible = (await db.Movies.Select(m => m.ItemId).ToListAsync(cancellationToken)).ToHashSet();
            series = (await db.Movies.Where(m => m.Kind == MovieMetadata.SeriesKind).Select(m => m.ItemId).ToListAsync(cancellationToken)).ToHashSet();
        }
        rows = rows.Where(r => r.Kind != "film" || visible.Contains(r.ItemId)).ToList();
        var parts = rows.GroupBy(r => r.Model)
            .Select(g => new Part(g.Key,
                g.Where(r => r.Kind == "film").ToDictionary(r => r.ItemId, r => Unpack(r.Vector)),
                (order.Count == 0 || order.Contains(g.Key)) ? g.Where(r => r.Kind == "brief").ToDictionary(r => r.ItemId, r => Unpack(r.Vector)) : new(),
                series) { Active = order.Count == 0 || order.Contains(g.Key) })
            .Where(p => p.Films.Count > 0)
            .OrderByDescending(p => p.Films.Count).ToList();
        var snapshot = new Snapshot(version, parts);
        lock (_gate) { _snapshot = snapshot; _snapshotKey = key; }
        return snapshot;
    }

    /// <summary>
    /// One model's matrix. A score is where the target ranks among all films for the source
    /// (see Spread: the closest 1, the farthest 0), so models with different similarity
    /// ranges can be averaged.
    /// Films and series are two matrices: a film is ranked among films only, a series among
    /// series only, whatever the source is. So series never shift a film's score, and "how close
    /// is this film to that series" still works (the film ranked among films, seen from the series).
    /// </summary>
    public sealed class Part
    {
        public const int MinimumPool = 20;
        public string Model { get; }
        /// <summary>Every title with a vector: films and series.</summary>
        public Dictionary<Guid, float[]> Films { get; }
        public Dictionary<Guid, float[]> Briefs { get; }
        /// <summary>False when the model is switched off or retired: its films still count, nothing new is added.</summary>
        public bool Active { get; init; } = true;
        private readonly Dictionary<Guid, float[]> _movies, _series;
        private readonly Dictionary<(Guid, bool, bool), Dictionary<Guid, double>> _ranks = new();
        private readonly Dictionary<string, Dictionary<Guid, (double X, double Y)>> _maps = new();

        public Part(string model, Dictionary<Guid, float[]> films, Dictionary<Guid, float[]> briefs, IReadOnlySet<Guid>? series = null)
        {
            Model = model;
            Films = films;
            Briefs = briefs;
            _series = series is null ? new() : films.Where(f => series.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value);
            _movies = series is null ? films : films.Where(f => !series.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value);
        }

        /// <summary>Enough films for the film matrix to mean something.</summary>
        public bool Any => _movies.Count >= MinimumPool;
        public bool AnySeries => _series.Count >= MinimumPool;
        public bool IsSeries(Guid id) => _series.ContainsKey(id);
        /// <summary>The films, or the series: the titles one matrix ranks.</summary>
        public IReadOnlyDictionary<Guid, float[]> Pool(bool series) => series ? _series : _movies;

        public double? Rank(Guid source, Guid target, bool brief)
        {
            if (source == target && !brief) return null;
            var inSeries = _series.ContainsKey(target);
            var pool = inSeries ? _series : _movies;
            if (pool.Count < MinimumPool || !pool.ContainsKey(target)) return null;
            Dictionary<Guid, double> ranks;
            lock (_ranks)
            {
                if (!_ranks.TryGetValue((source, brief, inSeries), out ranks!))
                {
                    var from = brief ? Briefs.GetValueOrDefault(source) : Films.GetValueOrDefault(source);
                    if (from is null) return null;
                    // Closest first. The score falls with the log of the place, so the top is spread out
                    // (closest 1, 10th about 0.7, 100th about 0.4 in a library of 2,000) instead of
                    // dozens of films all near the top score.
                    var ordered = pool.Where(f => f.Key != source)
                        .Select(f => (Id: f.Key, Cos: Dot(from, f.Value)))
                        .OrderByDescending(x => x.Cos).ToList();
                    ranks = new Dictionary<Guid, double>(ordered.Count);
                    for (var i = 0; i < ordered.Count; i++)
                        ranks[ordered[i].Id] = Spread(i, ordered.Count);
                    _ranks[(source, brief, inSeries)] = ranks;
                }
            }
            return ranks.TryGetValue(target, out var r) ? r : null;
        }

        /// <summary>
        /// Every film as a point on a flat map: the two directions in which the films' meanings
        /// differ most (principal components). Close points mean similar things; the map
        /// flattens a lot, so the closest-films list is the exact answer.
        /// </summary>
        /// <param name="kind">"movie" (default): the films; "series": the series; "all": both on one map.
        /// Only the map is joined: scores always rank a film among films and a series among series.</param>
        public Dictionary<Guid, (double X, double Y)> Map(string? kind = null)
        {
            kind = kind is MovieMetadata.SeriesKind or "all" ? kind : MovieMetadata.MovieKind;
            lock (_ranks)
            {
                if (_maps.TryGetValue(kind, out var known)) return known;
                var pool = kind == "all" ? Films : kind == MovieMetadata.SeriesKind ? _series : _movies;
                var ids = pool.Keys.ToList();
                if (ids.Count < 3) return _maps[kind] = new();
                var dim = pool[ids[0]].Length;
                var rows = ids.Select(id => pool[id]).Where(v => v.Length == dim).ToList();
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
                return _maps[kind] = ids.Where(id => pool[id].Length == dim).ToDictionary(id => id, id => (Along(pool[id], first), Along(pool[id], second)));
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

    /// <summary>All models' matrices at one moment; scores are averaged over the models that know both.</summary>
    public sealed class Snapshot
    {
        public static readonly Snapshot Empty = new(-1, new List<Part>());
        public long Version { get; }
        /// <summary>Most films first.</summary>
        public IReadOnlyList<Part> Parts { get; }

        public Snapshot(long version, List<Part> parts)
        {
            Version = version;
            Parts = parts;
        }

        /// <summary>One model only (tests, and the single-model case).</summary>
        public Snapshot(string model, long version, Dictionary<Guid, float[]> films, Dictionary<Guid, float[]> briefs)
            : this(version, new List<Part> { new(model, films, briefs) }) { }

        /// <summary>The model with the most films.</summary>
        public string Model => Parts.FirstOrDefault()?.Model ?? "";
        public bool Any => Parts.Any(p => p.Any);
        public bool Has(Guid film) => Parts.Any(p => (p.IsSeries(film) ? p.AnySeries : p.Any) && p.Films.ContainsKey(film));
        public Part? For(string? model) => Parts.FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase)) ?? Parts.FirstOrDefault();

        /// <summary>How close a film is to another, 0..1 (1 = the closest of all); null when unknown.</summary>
        public double? Score(Guid source, Guid target) => Average(p => p.Rank(source, target, brief: false));

        /// <summary>How close a film is to what this person wants, 0..1; null when unknown.</summary>
        public double? BriefScore(Guid userId, Guid target) => Average(p => p.Rank(userId, target, brief: true));

        /// <summary>Each model's own score, for showing how they agree.</summary>
        public Dictionary<string, double> ScoreBy(Guid source, Guid target) =>
            Parts.Select(p => (p.Model, Score: p.Rank(source, target, brief: false)))
                .Where(x => x.Score is not null).ToDictionary(x => x.Model, x => x.Score!.Value);

        private double? Average(Func<Part, double?> score)
        {
            double sum = 0; var n = 0;
            foreach (var p in Parts) if (score(p) is { } v) { sum += v; n++; }
            return n == 0 ? null : sum / n;
        }

        public Dictionary<Guid, (double X, double Y)> Map(string? model = null, string? kind = null) => For(model)?.Map(kind) ?? new();
    }

    /// <summary>
    /// The score of the title at this place (0 = closest) among count titles: 1 for the closest, 0 for
    /// the farthest, falling with the log of the place. Checked against TMDB's "viewers also liked",
    /// film series and real Letterboxd ratings (2026-11): with 70% meaning it ranked people's
    /// favourites highest; the earlier score (0.7 at most, flat at the top) held meaning back.
    /// </summary>
    public static double Spread(int place, int count) =>
        count <= 1 ? 1 : 1 - Math.Log(place + 1) / Math.Log(count);

    /// <summary>Tags and meaning together: weight 0 = tags only, 1 = meaning only.</summary>
    public static double Blend(double tags, double? meaning, double weight) =>
        meaning is { } m && weight > 0 ? tags * (1 - weight) + m * weight : tags;
}
