using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

[Collection("ServerSettings")]
public class RetiredModelTests
{
    private sealed class NoHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    [Fact]
    public async Task ASwitchedOffModelKeepsItsFilmsButNotItsWishes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-retired-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var s = ServerSettings.Current;
        var before = (s.MeaningOn, s.AIProvider, s.ApiKey, s.EmbeddingModels.ToList(), s.EmbeddingModelsAuto);
        try
        {
            var database = new Database(dir);
            var films = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();
            var user = Guid.NewGuid();
            await using (var db = database.Open())
            {
                foreach (var (id, i) in films.Select((f, i) => (f, i)))
                {
                    db.Movies.Add(new MovieMetadata { ItemId = id, Title = $"Film {i}", IsClassified = true });
                    foreach (var model in new[] { "a/live:free", "b/retired:free" })
                        db.Embeddings.Add(new Embedding { ItemId = id, Kind = "film", Model = model, Hash = "h", Vector = Meaning.Pack(new[] { 1f, i, i % 5 }), At = DateTime.UtcNow });
                }
                foreach (var model in new[] { "a/live:free", "b/retired:free" })
                    db.Embeddings.Add(new Embedding { ItemId = user, Kind = "brief", Model = model, Hash = "h", Vector = Meaning.Pack(new[] { 1f, 3, 3 }), At = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            (s.MeaningOn, s.AIProvider, s.ApiKey, s.EmbeddingModelsAuto) = (true, AIProviderType.OpenRouter, "test-key", false);
            s.EmbeddingModels = new List<string> { "a/live:free", "b/retired:free" };
            var meaning = new Meaning(new NoHttp(), database, NullLogger<Meaning>.Instance);
            Assert.All((await meaning.SnapshotAsync(default)).Parts, p => Assert.True(p.Active));

            // switched off: its matrix stays, its stored wishes (no longer kept up to date) don't count
            s.EmbeddingModels = new List<string> { "a/live:free" };
            var snapshot = await meaning.SnapshotAsync(default);
            var retired = snapshot.For("b/retired:free")!;
            Assert.Equal("b/retired:free", retired.Model);
            Assert.False(retired.Active);
            Assert.Equal(30, retired.Films.Count);
            Assert.Empty(retired.Briefs);
            Assert.Equal(2, snapshot.ScoreBy(films[0], films[1]).Count);

            // switched on again: back in full
            s.EmbeddingModels = new List<string> { "a/live:free", "b/retired:free" };
            Assert.True((await meaning.SnapshotAsync(default)).For("b/retired:free")!.Active);
        }
        finally
        {
            (s.MeaningOn, s.AIProvider, s.ApiKey, var models, s.EmbeddingModelsAuto) = before;
            s.EmbeddingModels = models;
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
