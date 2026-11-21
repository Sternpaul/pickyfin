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

public class MeaningRefusalTests
{
    [Fact]
    public void LinksAreLeftOutOfWhatIsSent()
    {
        var sent = Meaning.Sendable("Heat (1995). Poster at https://img.example.com/heat.jpg and www.example.com/x, data:image/png;base64,AAAA end.");
        Assert.DoesNotContain("http", sent);
        Assert.DoesNotContain("www.", sent);
        Assert.DoesNotContain("data:", sent);
        Assert.StartsWith("Heat (1995).", sent);
        Assert.Equal(2000, Meaning.Sendable(new string('a', 5000), 2000).Length);
    }

    private sealed class FakeOpenRouter : HttpMessageHandler
    {
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get) return Json("""{ "data": [] }""");
            Interlocked.Increment(ref Requests);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var input = doc.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToList();
            // this server refuses any request that holds the odd text
            if (input.Any(t => t.Contains("ODD")))
                return Json("""{ "error": { "code": 400, "message": "HTTP 400: image inputs require VLM serving to be enabled on this server" } }""", HttpStatusCode.BadRequest);
            var data = input.Select((t, i) => new { index = i, embedding = new[] { 1f, t.Length % 7, i } });
            return Json(JsonSerializer.Serialize(new { data }));
        }
        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task OneRefusedTextDoesNotBlockTheOthers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-meaning-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = ServerSettings.Current;
        var before = (s.MeaningOn, s.AIProvider, s.ApiKey, s.EmbeddingModels.ToList(), s.EmbeddingModelsAuto);
        try
        {
            var database = new Database(dir);
            var odd = Guid.NewGuid();
            await using (var db = database.Open())
            {
                for (var i = 0; i < 20; i++)
                    db.Movies.Add(new MovieMetadata { ItemId = i == 7 ? odd : Guid.NewGuid(), Title = i == 7 ? "The ODD One" : $"Film {i}", IsClassified = true, Plot = new string('x', i) });
                await db.SaveChangesAsync();
            }
            (s.MeaningOn, s.AIProvider, s.ApiKey, s.EmbeddingModelsAuto) = (true, AIProviderType.OpenRouter, "test-key", false);
            s.EmbeddingModels = new List<string> { "test/model-a:free" };
            Meaning.ResetPause();
            var fake = new FakeOpenRouter();
            await new Meaning(new Factory(fake), database, NullLogger<Meaning>.Instance).RunAsync(default);

            await using var check = database.Open();
            var stored = await check.Embeddings.Select(e => e.ItemId).ToListAsync();
            Assert.Equal(19, stored.Count);            // everything but the odd one
            Assert.DoesNotContain(odd, stored);
            Assert.Equal(1, Meaning.RefusedCount("test/model-a:free"));
            Assert.Null(Meaning.RestingWhy("test/model-a:free")); // the model keeps working
        }
        finally
        {
            (s.MeaningOn, s.AIProvider, s.ApiKey, s.EmbeddingModels, s.EmbeddingModelsAuto) = (before.Item1, before.Item2, before.Item3, before.Item4, before.Item5);
            Meaning.ResetPause();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
