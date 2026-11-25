using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>What the admin chose for a manual run.</summary>
/// <param name="Scope">"all" (every library that's on), "library" (the libraries in Libraries) or "selected" (the titles picked on the Movies and Series pages).</param>
/// <param name="Redo">True: every chosen title goes through the steps again; false: only what's missing. Hand edits are never overwritten.</param>
/// <param name="Limit">Dollars; the run stops before going past it. Null: no limit.</param>
public sealed record ManualRunRequest(string Scope, List<Guid>? Libraries, List<Guid>? Ids, bool Redo,
    string? Describe, string? Check, string? Lookup, string? Meaning, double? Limit);

/// <summary>
/// A one-time run over chosen titles and steps with the models the admin picked, any source,
/// paid or free: apart from "Free only" and the model orders. It estimates first, counts what
/// each call costs, and stops before going past the spending limit.
/// </summary>
public sealed class ManualRun
{
    public sealed class StepState
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Model { get; init; } = "";
        public int Total { get; set; }
        public int Done { get; set; }
        public string? Note { get; set; }
    }

    public sealed class RunState
    {
        public bool Running { get; set; }
        public DateTime Started { get; init; }
        public DateTime? Finished { get; set; }
        public int Titles { get; init; }
        public bool Redo { get; init; }
        public double? Limit { get; init; }
        public double Spent { get; set; }
        /// <summary>Some model's price is unknown: what it used isn't in Spent.</summary>
        public bool Unpriced { get; set; }
        public string? Current { get; set; }
        /// <summary>Why it ended before the end (stopped by hand, limit, an error); null when it finished.</summary>
        public string? Ended { get; set; }
        public List<StepState> Steps { get; init; } = new();
    }

    public const string Key = "manual";
    public const string Title = "Manual run";
    private const int Batch = MovieClassifier.BatchSize, LookupBatch = 5;

    private readonly Database _database;
    private readonly MovieClassifier _classifier;
    private readonly SecondOpinion _check;
    private readonly Meaning _meaning;
    private readonly WebLookup _web;
    private readonly IHttpClientFactory _http;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _logger;
    private CancellationTokenSource? _stop;
    private readonly object _gate = new();

    public ManualRun(Database database, MovieClassifier classifier, SecondOpinion check, Meaning meaning, WebLookup web, IHttpClientFactory http, ILoggerFactory logs)
    {
        _database = database;
        _classifier = classifier;
        _check = check;
        _meaning = meaning;
        _web = web;
        _http = http;
        _logs = logs;
        _logger = logs.CreateLogger("Manual run");
    }

    /// <summary>The last run since the server started (null: none yet).</summary>
    public RunState? State { get; private set; }

    private static readonly (string Key, string Name)[] StepNames =
        { ("describe", "1 · Describe"), ("check", "2 · Check"), ("lookup", "3 · Look up"), ("meaning", "Meaning") };

    private static string? ModelFor(ManualRunRequest r, string step) => step switch
    {
        "describe" => r.Describe, "check" => r.Check, "lookup" => r.Lookup, _ => r.Meaning,
    } is { Length: > 0 } m ? m.Trim() : null;

    private static bool HandEdited(MovieMetadata m) => m.ManuallyEdited || m.Verified;

    /// <summary>The chosen titles.</summary>
    private async Task<List<MovieMetadata>> TitlesAsync(ManualRunRequest r, CancellationToken ct)
    {
        await using var db = _database.Open();
        var all = await db.Movies.AsNoTracking().ToListAsync(ct);
        var s = ServerSettings.Current;
        return r.Scope switch
        {
            "library" => all.Where(m => (m.Libraries ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(id => Guid.TryParse(id, out var g) && (r.Libraries ?? new()).Contains(g))).ToList(),
            "selected" => all.Where(m => (r.Ids ?? new()).Contains(m.ItemId)).ToList(),
            _ => all.Where(m => !s.IsLibraryExcluded(m.Libraries)).ToList(),
        };
    }

    /// <summary>Which of the titles a step would touch. "afterDescribe": describing runs first, so count those it will describe as described.</summary>
    private static List<MovieMetadata> For(string step, List<MovieMetadata> titles, bool redo, bool afterDescribe) => step switch
    {
        "describe" => titles.Where(m => !HandEdited(m) && (redo || !m.IsClassified)).ToList(),
        // unlike the automatic step 2, a manual check takes looked-up titles too: the admin chose to check them (the web text goes along)
        "check" => titles.Where(m => !HandEdited(m) && (m.IsClassified || afterDescribe) && (redo || !m.Checked)).ToList(),
        "lookup" => titles.Where(m => !HandEdited(m) && (m.IsClassified || afterDescribe) && (redo || m.WebNotes is null)).ToList(),
        _ => titles.Where(m => m.IsClassified || afterDescribe).ToList(),
    };

    /// <param name="Why">Why chosen titles are left out of this step (hand edits, already done, looked up), for the panel.</param>
    public sealed record StepEstimate(string Step, string Name, string Model, string Source, int Titles, long InTokens, long OutTokens, double? Cost, string? Note, string? Why = null);
    public sealed record Estimate(int Titles, List<StepEstimate> Steps, double Known, bool Unpriced, int WebSearches, string? Problem);

    /// <summary>The chosen titles a step leaves out, and why, in words; null when it takes them all.</summary>
    private static string? WhyLeftOut(string step, List<MovieMetadata> titles, bool redo, bool afterDescribe, int taken)
    {
        if (taken == titles.Count) return null;
        var reasons = new List<string>();
        var hand = titles.Count(HandEdited);
        if (hand > 0) reasons.Add($"{hand:N0} edited or checked by you");
        var rest = titles.Where(m => !HandEdited(m)).ToList();
        if (step != "describe" && !afterDescribe && rest.Count(m => !m.IsClassified) is > 0 and var undescribed) reasons.Add($"{undescribed:N0} not described yet");
        rest = rest.Where(m => m.IsClassified || afterDescribe).ToList();
        if (!redo)
        {
            var done = step switch { "describe" => rest.Count(m => m.IsClassified), "check" => rest.Count(m => m.Checked), "lookup" => rest.Count(m => m.WebNotes is not null), _ => 0 };
            if (done > 0) reasons.Add($"{done:N0} already done (\"Everything again\" runs them anyway)");
        }
        return reasons.Count == 0 ? null : "Left out: " + string.Join("; ", reasons) + ".";
    }

    /// <summary>What a run would do and cost, before it starts. Tokens are estimated from the real prompts (about three characters each).</summary>
    public async Task<Estimate> EstimateAsync(ManualRunRequest r, CancellationToken ct)
    {
        var titles = await TitlesAsync(r, ct);
        var s = ServerSettings.Current;
        var steps = new List<StepEstimate>();
        var describes = ModelFor(r, "describe") is not null;
        var searches = 0;
        foreach (var (key, name) in StepNames)
        {
            if (ModelFor(r, key) is not { } model) continue;
            var list = For(key, titles, r.Redo, afterDescribe: describes && key != "describe");
            long input = 0, output = 0;
            foreach (var batch in list.Chunk(Batch))
            {
                switch (key)
                {
                    case "describe": input += ClassificationPrompt.Build(batch).Length / 3; output += batch.Length * 220; break;
                    case "check": input += ClassificationPrompt.BuildCheck(batch).Length / 3; output += batch.Length * 60; break;
                    case "lookup": input += ClassificationPrompt.Build(batch).Length / 3 + batch.Length * 1500; output += batch.Length * 220; break;
                    default: input += batch.Sum(m => Meaning.Sendable(Meaning.Document(m)).Length / 3 + 1); break;
                }
            }
            if (key == "lookup") searches = list.Count;
            var price = await Prices.ForAsync(model, _http, _logger, ct);
            steps.Add(new(key, name, model, Providers.Name(model), list.Count, input, output,
                price is null ? null : Prices.Cost(price, (int)Math.Min(int.MaxValue, input), (int)Math.Min(int.MaxValue, output)),
                price?.Note ?? (price is null ? "price unknown: the spending limit can't count this model" : null),
                WhyLeftOut(key, titles, r.Redo, describes && key != "describe", list.Count)));
        }
        string? problem = r.Scope == "library" && (r.Libraries is null || r.Libraries.Count == 0) ? "Tick at least one library."
            : titles.Count == 0 ? "No titles chosen." : steps.Count == 0 ? "No step chosen." : null;
        foreach (var step in steps)
            if (!Providers.HasKey(s, step.Model)) problem ??= $"{step.Name}: no {step.Source} key is set.";
        if (steps.Any(x => x.Step is "lookup") && !WebLookup.Configured) problem ??= "3 · Look up needs a web search engine (AI page, step 3).";
        if (steps.Any(x => x.Step is not "meaning") && s.AIProvider != AIProviderType.OpenRouter) problem ??= "Describing, checking and looking up in a manual run need OpenRouter as the provider.";
        return new(titles.Count, steps, steps.Sum(x => x.Cost ?? 0), steps.Any(x => x.Cost is null), searches, problem);
    }

    /// <summary>Starts a run in the background; null when it can't start (with why).</summary>
    public async Task<string?> StartAsync(ManualRunRequest r, CancellationToken ct)
    {
        lock (_gate) if (State is { Running: true }) return "A manual run is already going.";
        var estimate = await EstimateAsync(r, ct);
        if (estimate.Problem is { } problem) return problem;
        var titles = await TitlesAsync(r, ct);
        var state = new RunState
        {
            Running = true, Started = DateTime.UtcNow, Titles = titles.Count, Redo = r.Redo, Limit = r.Limit,
            Unpriced = estimate.Unpriced,
            Steps = estimate.Steps.Select(x => new StepState { Key = x.Step, Name = x.Name, Model = x.Model, Total = x.Titles }).ToList(),
        };
        CancellationTokenSource stop;
        lock (_gate)
        {
            if (State is { Running: true }) return "A manual run is already going.";
            State = state;
            _stop = stop = new CancellationTokenSource();
        }
        _logger.LogInformation("Manual run started: {Titles} titles, {Steps}, {Mode}{Limit}", titles.Count,
            string.Join(", ", state.Steps.Select(x => $"{x.Name} with {x.Model}")), r.Redo ? "everything again" : "only what's missing",
            r.Limit is { } l ? $", limit ${l:0.##}" : ", no limit");
        Jobs.Running(Key, Title, "Starting");
        _ = Task.Run(() => RunAsync(r, state, titles.Select(m => m.ItemId).ToList(), stop.Token));
        return null;
    }

    public bool Stop()
    {
        lock (_gate)
        {
            if (State is not { Running: true }) return false;
            State.Ended ??= "Stopped by hand";
            _stop?.Cancel();
            return true;
        }
    }

    private async Task RunAsync(ManualRunRequest r, RunState state, List<Guid> ids, CancellationToken stop)
    {
        // every price once, before the first call
        var prices = new Dictionary<string, Prices.Price?>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in state.Steps) prices[step.Model] = await Prices.ForAsync(step.Model, _http, _logger, CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        // what the last call of each model really cost: estimates can be low, so the next call counts as at least that
        var lastCall = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Used(string model, int input, int output)
        {
            var price = prices.TryGetValue(model, out var p) ? p : null;
            if (price is null) { state.Unpriced = true; return; }
            var cost = Prices.Cost(price, input, output);
            lock (state) { state.Spent += cost; lastCall[model] = cost; }
            if (state.Limit is { } max && state.Spent >= max) { state.Ended ??= $"Spending limit of ${max:0.##} reached"; limit.Cancel(); }
        }
        // Would this go past the limit? Then stop before it (and cancel the run, so the call underneath stops too).
        bool Fits(string model, long input, long output)
        {
            if (state.Limit is not { } max || !prices.TryGetValue(model, out var price) || price is null) return true;
            var next = Prices.Cost(price, (int)Math.Min(int.MaxValue, input), (int)Math.Min(int.MaxValue, output));
            lock (state) if (lastCall.TryGetValue(model, out var last)) next = Math.Max(next, last);
            if (state.Spent + next <= max) return true;
            state.Ended ??= $"Stopped before going past the spending limit of ${max:0.##}";
            limit.Cancel();
            return false;
        }

        try
        {
            foreach (var step in state.Steps)
            {
                if (limit.IsCancellationRequested || state.Ended is not null) break;
                state.Current = step.Key;
                // what the step touches now (describing may have changed it)
                List<MovieMetadata> titles;
                await using (var db = _database.Open())
                    titles = (await db.Movies.AsNoTracking().Where(m => ids.Contains(m.ItemId)).ToListAsync(limit.Token));
                var list = For(step.Key, titles, r.Redo, afterDescribe: false);
                step.Total = list.Count;
                Jobs.Running(Key, Title, $"{step.Name} with {Meaning.Short(step.Model)}: 0 of {list.Count:N0}");
                using var scope = RunContext.Use(new RunContext { ChatModel = step.Model, Used = Used, Allow = (m, i, o) => Fits(m, i, o) });
                var provider = new OpenRouterProvider(_http.CreateClient(), _logs.CreateLogger<OpenRouterProvider>());
                if (step.Key == "meaning")
                {
                    var (done, stopped) = await _meaning.ManualAsync(step.Model, list.Select(m => m.ItemId).ToList(), r.Redo,
                        n => { step.Done = n; Jobs.Running(Key, Title, $"{step.Name} with {Meaning.Short(step.Model)}: {n:N0} of {list.Count:N0}"); }, limit.Token);
                    step.Done = done;
                    if (stopped is not null) step.Note = stopped;
                    continue;
                }
                var size = step.Key == "lookup" ? LookupBatch : Batch;
                foreach (var batch in list.Chunk(size).Select(b => b.ToList()))
                {
                    if (limit.IsCancellationRequested) break;
                    var input = step.Key == "check" ? ClassificationPrompt.BuildCheck(batch).Length / 3 : ClassificationPrompt.Build(batch).Length / 3 + (step.Key == "lookup" ? batch.Count * 1500 : 0);
                    if (!Fits(step.Model, input, batch.Count * 220)) break;
                    try
                    {
                        using (Activity.Begin($"Manual run · {step.Name}: titles {step.Done + 1}–{step.Done + batch.Count} of {list.Count}, waiting for {step.Model}"))
                        {
                            switch (step.Key)
                            {
                                case "describe": await _classifier.DescribeManualAsync(provider, batch, limit.Token); break;
                                case "check": await _check.CheckManualAsync(provider, batch, limit.Token); break;
                                case "lookup":
                                    if (_web.Blocked() is { } why) { step.Note = $"web lookups paused: {why}"; goto nextStep; }
                                    await _classifier.LookUpAndTagManyAsync(provider, batch, limit.Token);
                                    break;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        step.Note = ex.Message.Length > 200 ? ex.Message[..200] + "…" : ex.Message;
                        _logger.LogWarning("Manual run · {Step}: {Error}; going on with the next step", step.Name, ex.Message);
                        break;
                    }
                    step.Done += batch.Count;
                    Jobs.Running(Key, Title, $"{step.Name} with {Meaning.Short(step.Model)}: {step.Done:N0} of {list.Count:N0}");
                }
                nextStep:;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            state.Ended ??= $"Error: {ex.Message}";
            _logger.LogError("Manual run failed: {Error}", ex.Message);
        }
        finally
        {
            state.Running = false;
            state.Finished = DateTime.UtcNow;
            state.Current = null;
            var summary = string.Join(", ", state.Steps.Select(x => $"{x.Name} {x.Done:N0}/{x.Total:N0}"));
            var spent = $"${state.Spent:0.####}{(state.Unpriced ? " plus models without a known price" : "")}";
            _logger.LogInformation("Manual run {How}: {Summary}; spent about {Spent}", state.Ended is null ? "finished" : $"ended ({state.Ended})", summary, spent);
            Jobs.Idle(Key, Title, $"{(state.Ended is null ? "Finished" : state.Ended)} · {summary} · about {spent}", error: state.Ended is not null && state.Ended != "Stopped by hand");
        }
    }
}
