using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

/// <summary>The order of the steps: look up, describe, check (with one more describe after an objection), re-check.</summary>
[Collection("ServerSettings")]
public class PipelineTests
{
    /// <summary>
    /// An AI that answers both prompts: describing tags every title "Heist" (or "Crime Drama"
    /// for titles named "Changes"), the check calls "Heist" wrong for every title it sees.
    /// </summary>
    private sealed class FakeAi : HttpMessageHandler
    {
        public readonly List<string> Prompts = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return Json("""{"data":[{"id":"test/model:free"}]}""");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            lock (Prompts) Prompts.Add(prompt);
            var refs = Regex.Matches(prompt, "\"Ref\":\"(m\\d+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
            string answer;
            if (prompt.StartsWith("You review movie tags"))
                answer = JsonSerializer.Serialize(new { problems = refs.Select(r => new { Ref = r, Wrong = new[] { "Heist" }, Better = new[] { "Crime Drama" }, Why = "No robbery." }) });
            else
            {
                // titles called "Changes" take the check's advice when described again
                var titles = Regex.Matches(prompt, "\"Ref\":\"(m\\d+)\"[^}]*?\"Title\":\"([^\"]+)\"").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
                answer = JsonSerializer.Serialize(new
                {
                    movies = refs.Select(r => new
                    {
                        Ref = r, Known = "yes",
                        Subcategories = new[] { titles.GetValueOrDefault(r, "").StartsWith("Changes") && prompt.Contains("Second opinion:") ? "Crime Drama" : "Heist" },
                        Moods = new[] { "tense", "gritty" },
                    }),
                });
            }
            return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } }));
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record Parts(Database Database, MovieStore Store, MovieClassifier Classifier, SecondOpinion Check, Rescan Rescan, FakeAi Ai);

    private static Parts Build(string dir)
    {
        var database = new Database(dir);
        var ai = new FakeAi();
        var http = new Factory(ai);
        var store = new MovieStore(database);
        var history = new MovieHistory(database, NullLogger<MovieHistory>.Instance);
        var web = new WebLookup(http, NullLogger<WebLookup>.Instance, new DataDirectory(dir));
        var factory = new AIProviderFactory(http, NullLoggerFactory.Instance);
        var classifier = new MovieClassifier(store, factory, NullLogger<MovieClassifier>.Instance, web, history);
        var check = new SecondOpinion(database, store, factory, classifier, NullLogger<SecondOpinion>.Instance, history);
        var rescan = new Rescan(database, store, classifier, factory, web, NullLogger<Rescan>.Instance);
        return new(database, store, classifier, check, rescan, ai);
    }

    private static async Task Settings(Action<ServerSettings> change, Func<Task> test)
    {
        var s = ServerSettings.Current;
        var before = s.Clone();
        try
        {
            (s.AIProvider, s.ApiKey, s.FreeOnly, s.CheckEnabled, s.CheckModel) = (AIProviderType.OpenRouter, "key", true, true, "test/model:free");
            (s.ClassificationModelsAuto, s.CheckModelsAuto, s.LookupModelsAuto) = (false, false, true);
            s.ClassificationModels = new() { "test/model:free" };
            s.CheckModels = new() { "test/model:free" };
            (s.RescanMonths, s.RescanPassStarted, s.WebLookupAuto) = (0, null, false);
            OpenRouterModels.Reset(); Providers.ResetRefused();
            change(s);
            await test();
        }
        finally
        {
            foreach (var prop in typeof(ServerSettings).GetProperties().Where(p => p.CanWrite && p.CanRead))
                prop.SetValue(s, prop.GetValue(before));
            OpenRouterModels.Reset();
        }
    }

    [Fact]
    public async Task LookedUpTitlesAreCheckedAndAnObjectionGetsOneMoreDescribe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-pipeline-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            await Settings(_ => { }, async () =>
            {
                var p = Build(dir);
                Guid keeps = Guid.NewGuid(), changes = Guid.NewGuid();
                await using (var db = p.Database.Open())
                {
                    db.Movies.AddRange(
                        new MovieMetadata { ItemId = keeps, Title = "Keeps", IsClassified = true, WebNotes = "A film about a family.", Subcategories = "[\"Heist\"]", Moods = "[\"tense\"]" },
                        new MovieMetadata { ItemId = changes, Title = "Changes", IsClassified = true, WebNotes = "A crime story.", Subcategories = "[\"Heist\"]", Moods = "[\"tense\"]" });
                    await db.SaveChangesAsync();
                }
                await p.Check.CheckPendingAsync(default);

                await using var check = p.Database.Open();
                var kept = await check.Movies.SingleAsync(m => m.ItemId == keeps);
                var changed = await check.Movies.SingleAsync(m => m.ItemId == changes);
                // both were checked although they have web text, and both described once more
                Assert.True(kept.Checked && changed.Checked);
                Assert.Contains(p.Ai.Prompts, x => x.StartsWith("You review movie tags") && x.Contains("A film about a family."));
                Assert.Single(p.Ai.Prompts, x => !x.StartsWith("You review movie tags")); // one describe call for both
                // the one that kept the wrong tag goes to Issues; the one that changed it doesn't
                Assert.Contains("Second opinion", kept.Issue);
                Assert.Equal(new[] { "Crime Drama" }, changed.SubcategorySet.Items);
                Assert.True(changed.Issue is null || !changed.Issue.Contains("Second opinion"));
                // and no endless loop: a second round finds nothing to check
                p.Ai.Prompts.Clear();
                await p.Check.CheckPendingAsync(default);
                Assert.Empty(p.Ai.Prompts);
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task ANewTitlesFirstRecheckComesAfterItsDays()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-firstrecheck-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // the normal schedule is off: only the first re-check of new titles runs
            await Settings(s => s.RescanMonths = 0, async () =>
            {
                var p = Build(dir);
                Guid due = Guid.NewGuid(), later = Guid.NewGuid(), old = Guid.NewGuid();
                await using (var db = p.Database.Open())
                {
                    db.Movies.AddRange(
                        new MovieMetadata { ItemId = due, Title = "Due", IsClassified = true, Subcategories = "[\"Heist\"]", FirstRecheckDue = DateTime.UtcNow.AddDays(-1) },
                        new MovieMetadata { ItemId = later, Title = "Later", IsClassified = true, Subcategories = "[\"Heist\"]", FirstRecheckDue = DateTime.UtcNow.AddDays(10) },
                        new MovieMetadata { ItemId = old, Title = "Old", IsClassified = true, Subcategories = "[\"Heist\"]" });
                    await db.SaveChangesAsync();
                }
                await p.Rescan.RunAsync(default);

                await using var check = p.Database.Open();
                var d = await check.Movies.SingleAsync(m => m.ItemId == due);
                Assert.NotNull(d.Rescanned);            // re-checked
                Assert.Null(d.FirstRecheckDue);         // and from now on on the normal schedule
                Assert.Null((await check.Movies.SingleAsync(m => m.ItemId == later)).Rescanned); // not yet
                Assert.Null((await check.Movies.SingleAsync(m => m.ItemId == old)).Rescanned);   // schedule off
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task DescribingANewTitleSchedulesItsFirstRecheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-describe-new-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            await Settings(s => s.FirstRecheckDays = 30, async () =>
            {
                var p = Build(dir);
                var id = Guid.NewGuid();
                await using (var db = p.Database.Open())
                {
                    db.Movies.Add(new MovieMetadata { ItemId = id, Title = "Wicker", ReleaseYear = 2026, Plot = "A fisherwoman orders a husband made of wicker." });
                    await db.SaveChangesAsync();
                }
                await p.Classifier.ClassifyPendingMoviesAsync(default);
                await using var check = p.Database.Open();
                var m = await check.Movies.SingleAsync(x => x.ItemId == id);
                Assert.True(m.IsClassified);
                Assert.InRange(m.FirstRecheckDue!.Value, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));
                // looked up before it was described (Wikipedia is on by default; the fake web finds nothing here)
                Assert.True(WebLookup.Configured);
                Assert.NotNull(m.WebNotes);
                var steps = await check.History.Where(h => h.ItemId == id).OrderBy(h => h.Id).Select(h => h.Step).ToListAsync();
                Assert.Equal(new[] { "web", "describe" }, steps.Take(2));
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
