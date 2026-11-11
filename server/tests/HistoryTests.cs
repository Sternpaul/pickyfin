using Microsoft.Extensions.Logging.Abstractions;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

public class HistoryTests
{
    [Fact]
    public async Task StepsAreKeptInOrderAndTrimmed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-history-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var history = new MovieHistory(new Database(dir), NullLogger<MovieHistory>.Instance);
            var film = new MovieMetadata { ItemId = Guid.NewGuid(), Title = "Harakiri", IsClassified = true, Subcategories = "[\"Samurai\"]", Moods = "[\"tense\"]" };
            await history.AddAsync(MovieHistory.Entry(film, "describe", "gemma", null, "Knew the film: yes"), default);
            var before = MovieHistory.Tags(film);
            film.Subcategories = "[\"Samurai\",\"Historical Epic\"]";
            await history.AddAsync(MovieHistory.Entry(film, "redescribe", "nemotron", before, "Knew the film: yes"), default);

            var list = await history.ForAsync(film.ItemId, default);
            Assert.Equal(new[] { "describe", "redescribe" }, list.Select(h => h.Step));
            Assert.Contains("Historical Epic", list[1].After);
            Assert.DoesNotContain("Historical Epic", list[1].Before);

            for (var i = 0; i < 70; i++) await history.AddAsync(MovieHistory.Entry(film, "check", "x", null, "Found nothing wrong"), default);
            Assert.Equal(60, (await history.ForAsync(film.ItemId, default)).Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
