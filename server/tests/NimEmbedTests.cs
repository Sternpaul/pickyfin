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
public class NimEmbedTests
{
    [Fact]
    public void OnlyAnAnswerThatSaysSoMakesAModelGone()
    {
        Assert.True(Meaning.LooksGone("HTTP 400: nvidia/x is not a valid model ID"));
        Assert.True(Meaning.LooksGone("HTTP 404: Function 'abc' not found for account 'xyz'"));
        Assert.False(Meaning.LooksGone("HTTP 400: Input length 900 exceeds maximum allowed token size 512 for this model"));
        Assert.False(Meaning.LooksGone("HTTP 400: input_type is not supported by this model"));
    }

    /// <summary>Like NVCLIP: refuses input_type and truncate (422), answers without them.</summary>
    private sealed class NoExtraFields : HttpMessageHandler
    {
        public int Refused, Answered;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("input_type", out _))
            {
                Refused++;
                return new(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("""{"detail":"Extra inputs are not permitted: input_type"}""", Encoding.UTF8, "application/json") };
            }
            Answered++;
            var n = doc.RootElement.GetProperty("input").GetArrayLength();
            var data = Enumerable.Range(0, n).Select(i => new { index = i, embedding = new[] { 1f, i, 2 } });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task AModelThatRefusesTheExtraFieldsGetsFewer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-nimembed-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = ServerSettings.Current;
        var before = (s.MeaningOn, s.AIProvider, s.ApiKey, s.NimApiKey, s.EmbeddingModels.ToList(), s.EmbeddingModelsAuto);
        try
        {
            var database = new Database(dir);
            await using (var db = database.Open())
            {
                for (var i = 0; i < 40; i++) db.Movies.Add(new MovieMetadata { ItemId = Guid.NewGuid(), Title = $"Film {i}", IsClassified = true });
                await db.SaveChangesAsync();
            }
            (s.MeaningOn, s.AIProvider, s.ApiKey, s.NimApiKey, s.EmbeddingModelsAuto) = (true, AIProviderType.OpenRouter, "", "nvapi-test", false);
            s.EmbeddingModels = new List<string> { "nim:nvidia/nvclip-test" };
            Meaning.ResetPause(); Providers.ResetRefused();
            var fake = new NoExtraFields();
            await new Meaning(new Factory(fake), database, NullLogger<Meaning>.Instance).RunAsync(default);

            await using var check = database.Open();
            Assert.Equal(40, await check.Embeddings.CountAsync(e => e.Model == "nim:nvidia/nvclip-test"));
            Assert.False(OpenRouterModels.IsGone("nim:nvidia/nvclip-test"));
            Assert.Equal(2, fake.Refused); // both requests with input_type once; then the one that worked is remembered
        }
        finally
        {
            (s.MeaningOn, s.AIProvider, s.ApiKey, s.NimApiKey, var models, s.EmbeddingModelsAuto) = before;
            s.EmbeddingModels = models;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
