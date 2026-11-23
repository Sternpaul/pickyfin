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
public class ProvidersTests
{
    private static ServerSettings Keys() => new()
    {
        ApiKey = "or", NimApiKey = "nv", CloudflareAccountId = "acc1", CloudflareApiToken = "cf", GeminiApiKey = "ge", JinaApiKey = "ji", MistralApiKey = "mi", VoyageApiKey = "vo",
    };

    [Fact]
    public void EachSourceHasItsOwnAddressAndKey()
    {
        var s = Keys();
        Assert.Equal("https://api.cloudflare.com/client/v4/accounts/acc1/ai/v1/embeddings", Providers.EmbedUrl(s, "cf:@cf/baai/bge-m3"));
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/embeddings", Providers.EmbedUrl(s, "gemini:gemini-embedding-001"));
        Assert.Equal("https://api.jina.ai/v1/embeddings", Providers.EmbedUrl(s, "jina:jina-embeddings-v5-text-small"));
        Assert.Equal("https://api.mistral.ai/v1/embeddings", Providers.EmbedUrl(s, "mistral:mistral-embed"));
        Assert.Equal("https://openrouter.ai/api/v1/embeddings", Providers.EmbedUrl(s, "nvidia/nemotron-3.5-embed-8b:free"));
        Assert.Equal("https://api.voyageai.com/v1/embeddings", Providers.EmbedUrl(s, "voyage:voyage-4-large"));
        Assert.Equal("vo", Providers.Key(s, "voyage:voyage-4"));
        Assert.Equal(new[] { "cf", "ge", "ji", "mi", "nv", "or" },
            new[] { "cf:x", "gemini:x", "jina:x", "mistral:x", "nim:x", "x/y" }.Select(m => Providers.Key(s, m)));
        Assert.Equal("@cf/baai/bge-m3", Providers.ApiModel("cf:@cf/baai/bge-m3"));
        Assert.Equal("bge-m3 (Cloudflare)", Meaning.Short("cf:@cf/baai/bge-m3"));
        Assert.Equal("gemini-embedding-001 (Gemini)", Meaning.Short("gemini:gemini-embedding-001"));
        // Cloudflare needs its account id as well as the token
        Assert.False(Providers.HasKey(new ServerSettings { CloudflareApiToken = "cf" }, "cf:@cf/baai/bge-m3"));
        Assert.True(Providers.AnyEmbeddingKey(new ServerSettings { GeminiApiKey = "ge" }));
        Assert.False(Providers.AnyKey(new ServerSettings { GeminiApiKey = "ge" }));
    }

    [Fact]
    public void JinaGetsItsTaskAndTheOthersThePlainRequest()
    {
        using var jina = JsonDocument.Parse(Meaning.Body("jina:jina-embeddings-v5-text-small", "brief", new() { "slow sci-fi" }));
        Assert.Equal("jina-embeddings-v5-text-small", jina.RootElement.GetProperty("model").GetString());
        Assert.Equal("retrieval.query", jina.RootElement.GetProperty("task").GetString());
        using var plain = JsonDocument.Parse(Meaning.Body("jina:jina-embeddings-v5-text-small", "film", new() { "Heat" }, variant: 1));
        Assert.False(plain.RootElement.TryGetProperty("task", out _));
        using var voyage = JsonDocument.Parse(Meaning.Body("voyage:voyage-4-large", "film", new() { "Heat" }));
        Assert.Equal("document", voyage.RootElement.GetProperty("input_type").GetString());
        Assert.True(voyage.RootElement.GetProperty("truncation").GetBoolean());
        using var cf = JsonDocument.Parse(Meaning.Body("cf:@cf/baai/bge-m3", "film", new() { "Heat" }));
        Assert.Equal("@cf/baai/bge-m3", cf.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, cf.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void EachSourcesModelListIsReadForItsEmbeddingModels()
    {
        Assert.Equal(new[] { "cf:@cf/baai/bge-m3", "cf:@cf/qwen/qwen3-embedding-0.6b" },
            Providers.ParseList(Providers.Cloudflare, """{"result":[{"name":"@cf/qwen/qwen3-embedding-0.6b"},{"name":"@cf/baai/bge-m3"}],"success":true}"""));
        Assert.Equal(new[] { "gemini:gemini-embedding-001" },
            Providers.ParseList(Providers.Gemini, """{"models":[{"name":"models/gemini-embedding-001","supportedGenerationMethods":["embedContent"]},{"name":"models/gemini-3-flash","supportedGenerationMethods":["generateContent"]}]}"""));
        Assert.Equal(new[] { "mistral:codestral-embed", "mistral:mistral-embed" },
            Providers.ParseList(Providers.Mistral, """{"data":[{"id":"mistral-embed"},{"id":"mistral-large-latest"},{"id":"codestral-embed"}]}"""));
    }

    /// <summary>Answers for Cloudflare, Gemini and Mistral; records who was asked with which key.</summary>
    private sealed class ThreeSources : HttpMessageHandler
    {
        public readonly List<(string Host, string Model, string? Key, int Texts)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var n = doc.RootElement.GetProperty("input").GetArrayLength();
            lock (Calls) Calls.Add((request.RequestUri!.Host, doc.RootElement.GetProperty("model").GetString()!, request.Headers.Authorization?.Parameter, n));
            var data = Enumerable.Range(0, n).Select(i => new { index = i, embedding = new[] { 1f, i, n } });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task AnOrderOfThreeSourcesBuildsThreeMatrices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-sources-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = ServerSettings.Current;
        var before = (s.MeaningOn, s.AIProvider, s.ApiKey, s.NimApiKey, s.CloudflareAccountId, s.CloudflareApiToken, s.GeminiApiKey, s.MistralApiKey, s.EmbeddingModels.ToList(), s.EmbeddingModelsAuto);
        try
        {
            var database = new Database(dir);
            await using (var db = database.Open())
            {
                for (var i = 0; i < 20; i++) db.Movies.Add(new MovieMetadata { ItemId = Guid.NewGuid(), Title = $"Film {i}", IsClassified = true });
                await db.SaveChangesAsync();
            }
            // no OpenRouter key at all: the other sources are enough
            (s.MeaningOn, s.AIProvider, s.ApiKey, s.NimApiKey, s.CloudflareAccountId, s.CloudflareApiToken, s.GeminiApiKey, s.MistralApiKey, s.EmbeddingModelsAuto) =
                (true, AIProviderType.OpenRouter, "", "", "acc1", "cf-token", "gemini-key", "mistral-key", false);
            s.VoyageApiKey = "voyage-key";
            s.EmbeddingModels = new List<string> { "cf:@cf/baai/bge-m3", "gemini:gemini-embedding-001", "mistral:mistral-embed", "voyage:voyage-4-lite" };
            Meaning.ResetPause(); Providers.ResetRefused();
            var fake = new ThreeSources();
            await new Meaning(new Factory(fake), database, NullLogger<Meaning>.Instance).RunAsync(default);

            await using var check = database.Open();
            foreach (var model in s.EmbeddingModels.Where(m => !m.StartsWith("voyage:")))
                Assert.Equal(20, await check.Embeddings.CountAsync(e => e.Model == model));
            // Voyage without a card allows 3 requests a minute: 3 requests of 5 films this round, the rest next round
            Assert.Equal(15, await check.Embeddings.CountAsync(e => e.Model == "voyage:voyage-4-lite"));
            Assert.Equal(new[] { 5, 5, 5 }, fake.Calls.Where(c => c.Host == "api.voyageai.com").Select(c => c.Texts));
            Assert.All(fake.Calls.Where(c => c.Host == "api.voyageai.com"), c => Assert.Equal("voyage-key", c.Key));
            Assert.All(fake.Calls.Where(c => c.Host == "api.cloudflare.com"), c => Assert.Equal(("@cf/baai/bge-m3", "cf-token"), (c.Model, c.Key)));
            Assert.All(fake.Calls.Where(c => c.Host == "generativelanguage.googleapis.com"), c => Assert.Equal(("gemini-embedding-001", "gemini-key"), (c.Model, c.Key)));
            // Mistral gets smaller batches
            Assert.All(fake.Calls.Where(c => c.Host == "api.mistral.ai"), c => Assert.True(c.Texts <= 8));
            Assert.Equal(3, fake.Calls.Where(c => c.Host == "api.mistral.ai").Count());
        }
        finally
        {
            (s.MeaningOn, s.AIProvider, s.ApiKey, s.NimApiKey, s.CloudflareAccountId, s.CloudflareApiToken, s.GeminiApiKey, s.MistralApiKey, var models, s.EmbeddingModelsAuto) = before;
            s.EmbeddingModels = models;
            s.VoyageApiKey = "";
            Providers.ResetRefused();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
