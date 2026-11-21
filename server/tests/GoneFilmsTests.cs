using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Xunit;

public class GoneFilmsTests
{
    [Fact]
    public async Task FilmsJellyfinDropsAreHiddenUntilConfirmed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-gone-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var database = new Database(dir);
            var store = new MovieStore(database);
            var keep = Guid.NewGuid(); var dupe = Guid.NewGuid(); var extra = Guid.NewGuid();
            await using (var db = database.Open())
            {
                db.Movies.AddRange(new MovieMetadata { ItemId = keep, Title = "Heat", IsClassified = true },
                    new MovieMetadata { ItemId = dupe, Title = "Heat (copy)", IsClassified = true },
                    new MovieMetadata { ItemId = extra, Title = "Making of Heat", IsClassified = true });
                db.Embeddings.Add(new Embedding { ItemId = dupe, Kind = "film", Model = "m", Hash = "h", Vector = new byte[8], At = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            // Jellyfin now lists only Heat: the other two are hidden, not deleted
            Assert.Equal((2, 0), await store.MarkGoneAsync(new HashSet<Guid> { keep }));
            Assert.Equal(new[] { keep }, (await store.GetAllMoviesAsync()).Select(m => m.ItemId));
            Assert.Equal(3, (await store.GetAllIncludingGoneAsync()).Count);
            Assert.Equal(2, (await store.GetGoneAsync()).Count);

            // the extra comes back in Jellyfin: visible again, tags kept
            Assert.Equal((0, 1), await store.MarkGoneAsync(new HashSet<Guid> { keep, extra }));
            Assert.Contains((await store.GetAllMoviesAsync()), m => m.ItemId == extra && m.IsClassified);

            // the admin confirms: only the duplicate is still gone, and it goes with its meaning
            var removed = await store.DeleteGoneAsync(null);
            Assert.Equal(new[] { "Heat (copy)" }, removed);
            await using var check = database.Open();
            Assert.Equal(2, await check.Movies.IgnoreQueryFilters().CountAsync());
            Assert.False(await check.Embeddings.AnyAsync(e => e.ItemId == dupe));
            // a film that's still listed is never deleted, even when asked by id
            Assert.Empty(await store.DeleteGoneAsync(new[] { keep }));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
