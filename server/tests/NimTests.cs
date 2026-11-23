using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Library;
using Xunit;

[Collection("ServerSettings")]
public class NimTests
{
    [Fact]
    public void ModelsAreRoutedByTheirPrefix()
    {
        var s = new ServerSettings { ApiKey = "or-key", NimApiKey = "nvapi-key" };
        Assert.Equal("https://integrate.api.nvidia.com/v1/chat/completions", Nim.ChatUrl(s, "nim:moonshotai/kimi-k3.1-pro"));
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", Nim.ChatUrl(s, "google/gemma-4-31b-it:free"));
        Assert.Equal("moonshotai/kimi-k3.1-pro", Nim.ApiModel("nim:moonshotai/kimi-k3.1-pro"));
        Assert.Equal("nvapi-key", Nim.Key(s, "nim:x/y"));
        Assert.Equal("or-key", Nim.Key(s, "x/y"));
        Assert.Equal("nemotron-3.5-embed-8b (NVIDIA)", Meaning.Short("nim:nvidia/nemotron-3.5-embed-8b"));
        Assert.Equal("nemotron-3.5-embed-8b", Meaning.Short("nvidia/nemotron-3.5-embed-8b:free"));
        Assert.True(new ServerSettings { NimApiKey = "k" }.HasAiKey);
    }

    [Fact]
    public void NvidiasListIsSplitIntoTalkingAndEmbeddingModels()
    {
        var list = Nim.Parse("""{"data":[{"id":"nvidia/nemotron-3.5-embed-8b"},{"id":"snowflake/arctic-embed-l"},{"id":"moonshotai/kimi-k3.1-pro"},{"id":"fastino/gliner2-pii"},{"id":"nvidia/nemotron-3.5-content-safety"},{"id":"nvidia/nvclip"}]}""");
        Assert.Equal(new[] { "nim:nvidia/nemotron-3.5-embed-8b", "nim:nvidia/nvclip", "nim:snowflake/arctic-embed-l" }, list.Where(Nim.IsEmbedding).OrderBy(x => x));
        Assert.Equal(new[] { "nim:moonshotai/kimi-k3.1-pro" }, list.Where(Nim.IsChat));
    }

    [Fact]
    public void NvidiaGetsWishesAsQuestionsAndFilmsAsPassages()
    {
        using var brief = JsonDocument.Parse(Meaning.Body("nim:nvidia/nemotron-3.5-embed-8b", "brief", new() { "slow sci-fi" }));
        Assert.Equal("nvidia/nemotron-3.5-embed-8b", brief.RootElement.GetProperty("model").GetString());
        Assert.Equal("query", brief.RootElement.GetProperty("input_type").GetString());
        Assert.Equal("END", brief.RootElement.GetProperty("truncate").GetString());
        using var film = JsonDocument.Parse(Meaning.Body("nim:nvidia/nemotron-3.5-embed-8b", "film", new() { "Heat (1995)." }));
        Assert.Equal("passage", film.RootElement.GetProperty("input_type").GetString());
        // OpenRouter's request stays as it was, so its stored vectors stay valid
        using var or = JsonDocument.Parse(Meaning.Body("nvidia/nemotron-3.5-embed-8b:free", "film", new() { "Heat (1995)." }));
        Assert.False(or.RootElement.TryGetProperty("input_type", out _));
    }

    private sealed class TwoProviders : HttpMessageHandler
    {
        public readonly List<(string Host, string Model, string? Auth)> Calls = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            if (request.Method == HttpMethod.Get)
                return Json(host.Contains("nvidia")
                    ? """{"data":[{"id":"moonshotai/kimi-k3.1-pro"}]}"""
                    : """{"data":[{"id":"google/gemma-4-31b-it:free","supported_parameters":["response_format"]}]}""");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var model = doc.RootElement.GetProperty("model").GetString()!;
            Calls.Add((host, model, request.Headers.Authorization?.Parameter));
            // OpenRouter refuses the key; NVIDIA answers
            if (host.Contains("openrouter")) return Json("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized);
            return Json("""{"choices":[{"message":{"content":"{\"ok\":true}"}}]}""");
        }
        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task AMixedOrderGoesOnWithNvidiaWhenOpenRouterRefusesTheKey()
    {
        var s = ServerSettings.Current;
        var before = (s.ApiKey, s.NimApiKey, s.ClassificationModels.ToList(), s.ClassificationModelsAuto, s.CustomEndpoint);
        try
        {
            (s.ApiKey, s.NimApiKey, s.ClassificationModelsAuto, s.CustomEndpoint) = ("or-key", "nvapi-key", false, "");
            s.ClassificationModels = new() { "google/gemma-4-31b-it:free", "nim:moonshotai/kimi-k3.1-pro" };
            Nim.ResetRefused();
            Nim.SetModels(null);
            var fake = new TwoProviders();
            var provider = new OpenRouterProvider(new HttpClient(fake), NullLogger<OpenRouterProvider>.Instance);
            var answer = await provider.ClassifyMoviesAsync(new());
            Assert.Contains("ok", answer);
            Assert.Equal(("openrouter.ai", "google/gemma-4-31b-it:free", "or-key"), fake.Calls[0]);
            Assert.Equal(("integrate.api.nvidia.com", "moonshotai/kimi-k3.1-pro", "nvapi-key"), fake.Calls[^1]);
            // OpenRouter's models now wait; NVIDIA's go straight on
            Assert.NotNull(Nim.RefusedWhy("google/gemma-4-31b-it:free"));
            Assert.Null(Nim.RefusedWhy("nim:moonshotai/kimi-k3.1-pro"));
        }
        finally
        {
            (s.ApiKey, s.NimApiKey, var models, s.ClassificationModelsAuto, s.CustomEndpoint) = before;
            s.ClassificationModels = models;
            Nim.ResetRefused();
            Nim.SetModels(null);
        }
    }
}
