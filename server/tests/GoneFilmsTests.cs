using Microsoft.EntityFrameworkCore;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Pickyfin.Server;
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
            Assert.Equal((2, 0), await store.MarkGoneAsync(new HashSet<Guid> { keep }, MovieMetadata.MovieKind));
            Assert.Equal(new[] { keep }, (await store.GetAllMoviesAsync()).Select(m => m.ItemId));
            Assert.Equal(3, (await store.GetAllIncludingGoneAsync()).Count);
            Assert.Equal(2, (await store.GetGoneAsync()).Count);

            // the extra comes back in Jellyfin: visible again, tags kept
            Assert.Equal((0, 1), await store.MarkGoneAsync(new HashSet<Guid> { keep, extra }, MovieMetadata.MovieKind));
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

    [Fact]
    public async Task AFilmWithANewFileKeepsItsTagsAndMeanings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pickyfin-replaced-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var database = new Database(dir);
            var store = new MovieStore(database);
            var old = Guid.NewGuid(); var fresh = Guid.NewGuid(); var other = Guid.NewGuid(); var otherNew = Guid.NewGuid();
            await using (var db = database.Open())
            {
                db.Movies.AddRange(
                    new MovieMetadata { ItemId = old, Title = "Heat", TmdbId = 949, IsClassified = true, Subcategories = "Heist", Libraries = "old-lib" },
                    new MovieMetadata { ItemId = other, Title = "Alien", ImdbId = "tt0078748", IsClassified = true });
                db.Embeddings.Add(new Embedding { ItemId = old, Kind = "film", Model = "m", Hash = "h", Vector = new byte[8], At = DateTime.UtcNow });
                db.History.Add(new HistoryEntry { ItemId = old, Step = "you", At = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            // the files were swapped: Jellyfin lists both films under new ids
            await store.SaveMoviesAsync(new[]
            {
                new MovieMetadata { ItemId = fresh, Title = "Heat", TmdbId = 949, Libraries = "new-lib" },
                new MovieMetadata { ItemId = otherNew, Title = "Alien", ImdbId = "TT0078748", IsClassified = true, Subcategories = "Space horror" },
            });
            await store.MarkGoneAsync(new HashSet<Guid> { fresh, otherNew }, MovieMetadata.MovieKind);

            var (moved, removed) = await store.ResolveReplacedAsync(MovieMetadata.MovieKind);
            Assert.Equal(new[] { "Heat" }, moved);      // the new one had nothing yet: the old record moved over
            Assert.Equal(new[] { "Alien" }, removed);   // the new one already had its own tags: the leftover went
            Assert.Empty(await store.GetGoneAsync());
            await using var check = database.Open();
            var heat = await check.Movies.SingleAsync(m => m.ItemId == fresh);
            Assert.True(heat.IsClassified);
            Assert.Equal("Heist", heat.Subcategories);
            Assert.Equal("new-lib", heat.Libraries);
            Assert.True(await check.Embeddings.AnyAsync(e => e.ItemId == fresh));
            Assert.True(await check.History.AnyAsync(h => h.ItemId == fresh));
            Assert.Equal(2, await check.Movies.IgnoreQueryFilters().CountAsync());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
