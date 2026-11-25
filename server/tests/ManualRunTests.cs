using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

[Collection("ServerSettings")]
public class ManualRunTests
{
    /// <summary>OpenRouter with one paid embedding model ($1 per million tokens) and one paid chat model.</summary>
    private sealed class PaidOpenRouter : HttpMessageHandler
    {
        public readonly List<string> Asked = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"data":[{"id":"acme/embed-paid","pricing":{"prompt":"0.000001","completion":"0"}},{"id":"acme/chat-paid","pricing":{"prompt":"0.000002","completion":"0.000004"},"supported_parameters":["response_format"]}]}""");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var model = doc.RootElement.GetProperty("model").GetString()!;
            lock (Asked) Asked.Add(model);
            if (doc.RootElement.TryGetProperty("messages", out _))
                return Json("""{"choices":[{"message":{"content":"{\"ok\":true}"}}],"usage":{"prompt_tokens":1000,"completion_tokens":500}}""");
            var n = doc.RootElement.GetProperty("input").GetArrayLength();
            var data = Enumerable.Range(0, n).Select(i => new { index = i, embedding = new[] { 1f, i, 3 } });
            // 100,000 tokens a request: $0.10 at $1 per million
            return Json(JsonSerializer.Serialize(new { data, usage = new { prompt_tokens = 100_000 } }));
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (ManualRun Run, Database Database, PaidOpenRouter Fake) Build(string dir)
    {
        var database = new Database(dir);
        var fake = new PaidOpenRouter();
        var http = new Factory(fake);
        var logs = NullLoggerFactory.Instance;
        var store = new MovieStore(database);
        var history = new MovieHistory(database, NullLogger<MovieHistory>.Instance);
        var web = new WebLookup(http, NullLogger<WebLookup>.Instance, new DataDirectory(dir));
        var ai = new AIProviderFactory(http, logs);
        var classifier = new MovieClassifier(store, ai, NullLogger<MovieClassifier>.Instance, web, history);
        var check = new SecondOpinion(database, store, ai, classifier, NullLogger<SecondOpinion>.Instance, history);
        var meaning = new Meaning(http, database, NullLogger<Meaning>.Instance);
        return (new ManualRun(database, classifier, check, meaning, web, http, logs), database, fake);
    }

    private static async Task<ManualRun.RunState> Finish(ManualRun run)
    {
        for (var i = 0; i < 200 && run.State is { Running: true }; i++) await Task.Delay(50);
        return run.State!;
    }

    [Fact]
    public async Task APaidModelRunsWithFreeOnlyOnAndStopsAtTheLimit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-manual-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = ServerSettings.Current;
        var before = (s.FreeOnly, s.MeaningOn, s.AIProvider, s.ApiKey, s.EmbeddingModels.ToList(), s.EmbeddingModelsAuto);
        try
        {
            OpenRouterModels.Reset(); Prices.Reset(); Pace.Reset(); Providers.ResetRefused();
            (s.FreeOnly, s.MeaningOn, s.AIProvider, s.ApiKey, s.EmbeddingModelsAuto) = (true, true, AIProviderType.OpenRouter, "or-key", false);
            s.EmbeddingModels = new List<string> { "nvidia/nemotron-3.5-embed-8b:free" };
            var (run, database, fake) = Build(dir);
            await using (var db = database.Open())
            {
                for (var i = 0; i < 40; i++) db.Movies.Add(new MovieMetadata { ItemId = Guid.NewGuid(), Title = $"Film {i}", IsClassified = true, Plot = "A plot." });
                await db.SaveChangesAsync();
            }
            var request = new ManualRunRequest("all", null, null, Redo: false, null, null, null, Meaning: "acme/embed-paid", Limit: 0.25);

            var estimate = await run.EstimateAsync(request, default);
            Assert.Null(estimate.Problem);
            Assert.Equal(40, estimate.Steps.Single().Titles);
            Assert.NotNull(estimate.Steps.Single().Cost); // the price is known from OpenRouter's list

            Assert.Null(await run.StartAsync(request, default));
            var state = await Finish(run);
            // each request "used" 100,000 tokens ($0.10): two fit under $0.25, then it stops before the third
            Assert.Contains("limit", state.Ended);
            Assert.InRange(state.Spent, 0.19, 0.25);
            Assert.All(fake.Asked, m => Assert.Equal("acme/embed-paid", m)); // the chosen model, though "Free only" is on
            await using var check = database.Open();
            Assert.Equal(32, await check.Embeddings.CountAsync(e => e.Model == "acme/embed-paid")); // 2 requests of 16
        }
        finally
        {
            (s.FreeOnly, s.MeaningOn, s.AIProvider, s.ApiKey, var models, s.EmbeddingModelsAuto) = before;
            s.EmbeddingModels = models;
            OpenRouterModels.Reset(); Prices.Reset(); Pace.Reset();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task TheChosenChatModelIsAskedAndItsTokensCounted()
    {
        var s = ServerSettings.Current;
        var before = (s.FreeOnly, s.ApiKey, s.ClassificationModels.ToList(), s.ClassificationModelsAuto, s.CustomEndpoint);
        try
        {
            OpenRouterModels.Reset(); Providers.ResetRefused();
            (s.FreeOnly, s.ApiKey, s.ClassificationModelsAuto, s.CustomEndpoint) = (true, "or-key", false, "");
            s.ClassificationModels = new() { "google/gemma-4-31b-it:free" };
            var fake = new PaidOpenRouter();
            var provider = new OpenRouterProvider(new HttpClient(fake), NullLogger<OpenRouterProvider>.Instance);
            var used = new List<(string, int, int)>();
            using (RunContext.Use(new RunContext { ChatModel = "acme/chat-paid", Used = (m, i, o) => used.Add((m, i, o)) }))
                await provider.ClassifyMoviesAsync(new());
            Assert.Equal(new[] { "acme/chat-paid" }, fake.Asked);
            Assert.Equal(new[] { ("acme/chat-paid", 1000, 500) }, used);
            // outside a manual run the paid model is skipped again
            fake.Asked.Clear();
            await provider.ClassifyMoviesAsync(new()).ContinueWith(_ => { });
            Assert.DoesNotContain("acme/chat-paid", fake.Asked);
        }
        finally
        {
            (s.FreeOnly, s.ApiKey, var models, s.ClassificationModelsAuto, s.CustomEndpoint) = before;
            s.ClassificationModels = models;
            OpenRouterModels.Reset();
        }
    }

    [Fact]
    public async Task SeveralLibrariesCanBeChosen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-manual-libs-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = ServerSettings.Current;
        var before = (s.AIProvider, s.ApiKey);
        try
        {
            OpenRouterModels.Reset(); Prices.Reset();
            (s.AIProvider, s.ApiKey) = (AIProviderType.OpenRouter, "or-key");
            var (run, database, _) = Build(dir);
            Guid films = Guid.NewGuid(), anime = Guid.NewGuid(), docs = Guid.NewGuid();
            await using (var db = database.Open())
            {
                db.Movies.AddRange(
                    new MovieMetadata { ItemId = Guid.NewGuid(), Title = "A", IsClassified = true, Libraries = $"{films}" },
                    new MovieMetadata { ItemId = Guid.NewGuid(), Title = "B", IsClassified = true, Libraries = $"{anime},{films}" },
                    new MovieMetadata { ItemId = Guid.NewGuid(), Title = "C", IsClassified = true, Libraries = $"{anime}" },
                    new MovieMetadata { ItemId = Guid.NewGuid(), Title = "D", IsClassified = true, Libraries = $"{docs}" });
                await db.SaveChangesAsync();
            }
            ManualRunRequest For(params Guid[] libraries) => new("library", libraries.ToList(), null, Redo: true, null, null, null, Meaning: "acme/embed-paid", Limit: null);
            Assert.Equal(2, (await run.EstimateAsync(For(films), default)).Titles);
            Assert.Equal(3, (await run.EstimateAsync(For(films, anime), default)).Titles); // B counted once
            Assert.Equal("Tick at least one library.", (await run.EstimateAsync(For(), default)).Problem);
        }
        finally
        {
            (s.AIProvider, s.ApiKey) = before;
            OpenRouterModels.Reset(); Prices.Reset();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void MistralIsPaidAndItsAliasesAreListedOnce()
    {
        Assert.False(Providers.IsFree(new ServerSettings { FreeOnly = true }, "mistral:mistral-embed"));
        Assert.True(Providers.IsFree(new ServerSettings { FreeOnly = false }, "mistral:mistral-embed"));
        Assert.Equal(new[] { "mistral:codestral-embed", "mistral:mistral-embed" },
            Providers.ParseList(Providers.Mistral, """{"data":[{"id":"mistral-embed-2312","aliases":["mistral-embed"]},{"id":"mistral-embed","aliases":["mistral-embed-2312"]},{"id":"codestral-embed-2505","aliases":["codestral-embed"]},{"id":"mistral-small-latest","capabilities":{"completion_chat":true}}]}"""));
        Assert.Equal(new[] { "mistral:mistral-small-latest" },
            Providers.ParseList(Providers.Mistral, """{"data":[{"id":"mistral-embed"},{"id":"mistral-small-latest","aliases":["mistral-small-2603"],"capabilities":{"completion_chat":true}},{"id":"mistral-small-2603","capabilities":{"completion_chat":true}}]}""", chat: true));
    }
}
