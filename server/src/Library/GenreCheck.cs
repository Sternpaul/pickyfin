using Pickyfin.Server.Data.Models;

namespace Pickyfin.Server.Library;

/// <summary>
/// A gentle sanity check of the AI's subcategories against the genres Jellyfin has
/// (from TMDB). It never changes tags; it only says what looks off, for the Issues page.
/// A subcategory is flagged only when the movie has genres and none of them fit at all.
/// </summary>
public static class GenreCheck
{
    // Jellyfin's genres come in the metadata language: English and German names, folded to one set
    private static readonly Dictionary<string, string> GenreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = "Action", ["Adventure"] = "Adventure", ["Abenteuer"] = "Adventure",
        ["Animation"] = "Animation", ["Comedy"] = "Comedy", ["Komödie"] = "Comedy", ["Komoedie"] = "Comedy",
        ["Crime"] = "Crime", ["Krimi"] = "Crime", ["Documentary"] = "Documentary", ["Dokumentarfilm"] = "Documentary", ["Dokumentation"] = "Documentary",
        ["Drama"] = "Drama", ["Family"] = "Family", ["Familie"] = "Family", ["Fantasy"] = "Fantasy",
        ["History"] = "History", ["Historie"] = "History", ["Horror"] = "Horror", ["Music"] = "Music", ["Musik"] = "Music", ["Musical"] = "Music",
        ["Mystery"] = "Mystery", ["Romance"] = "Romance", ["Liebesfilm"] = "Romance", ["Romantik"] = "Romance",
        ["Science Fiction"] = "Science Fiction", ["Sci-Fi"] = "Science Fiction", ["Science-Fiction"] = "Science Fiction",
        ["Thriller"] = "Thriller", ["War"] = "War", ["Kriegsfilm"] = "War", ["Krieg"] = "War", ["Western"] = "Western",
    };

    private static string[] G(params string[] genres) => genres;

    // Which genres a subcategory can live under. Subcategories missing here are never flagged
    // (thriller and drama labels fit too many genres to judge).
    private static readonly Dictionary<string, string[]> Fits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Supernatural Horror"] = G("Horror", "Thriller", "Mystery", "Fantasy"),
        ["Psychological Horror"] = G("Horror", "Thriller", "Mystery", "Drama"),
        ["Folk Horror"] = G("Horror", "Thriller", "Mystery"),
        ["Body Horror"] = G("Horror", "Science Fiction", "Thriller"),
        ["Cosmic Horror"] = G("Horror", "Science Fiction", "Mystery", "Fantasy"),
        ["Slasher"] = G("Horror", "Thriller"),
        ["Found Footage"] = G("Horror", "Thriller", "Science Fiction", "Mystery", "Documentary"),
        ["Zombie"] = G("Horror", "Action", "Comedy", "Science Fiction"),
        ["Vampire"] = G("Horror", "Fantasy", "Romance", "Comedy", "Drama"),
        ["Monster Movie"] = G("Horror", "Science Fiction", "Action", "Adventure", "Fantasy"),
        ["Horror Comedy"] = G("Horror", "Comedy"),
        ["Space Opera"] = G("Science Fiction", "Adventure", "Action", "Fantasy"),
        ["Hard Sci-Fi"] = G("Science Fiction"),
        ["Cyberpunk"] = G("Science Fiction", "Action", "Animation"),
        ["Time Travel"] = G("Science Fiction", "Fantasy", "Comedy", "Adventure"),
        ["Alien Invasion"] = G("Science Fiction", "Horror", "Action"),
        ["AI & Robots"] = G("Science Fiction", "Thriller", "Animation"),
        ["Superhero"] = G("Action", "Science Fiction", "Fantasy", "Adventure", "Animation"),
        ["Fantasy Epic"] = G("Fantasy", "Adventure"),
        ["Dark Fantasy"] = G("Fantasy", "Horror"),
        ["Fairy Tale"] = G("Fantasy", "Family", "Animation", "Romance"),
        ["Sword & Sorcery"] = G("Fantasy", "Adventure", "Action"),
        ["Martial Arts"] = G("Action", "Adventure", "Thriller", "Crime"),
        ["Swashbuckler"] = G("Adventure", "Action"),
        ["War"] = G("War", "History", "Drama", "Action"),
        ["Military Action"] = G("War", "Action"),
        ["Western"] = G("Western"),
        ["Romantic Comedy"] = G("Comedy", "Romance"),
        ["Dark Comedy"] = G("Comedy", "Crime", "Drama"),
        ["Satire"] = G("Comedy", "Drama"),
        ["Parody"] = G("Comedy"),
        ["Slapstick"] = G("Comedy", "Family", "Animation"),
        ["Buddy Comedy"] = G("Comedy", "Action", "Crime"),
        ["Teen Comedy"] = G("Comedy"),
        ["Comedy of Manners"] = G("Comedy", "Drama", "Romance"),
        ["Mockumentary"] = G("Comedy", "Documentary"),
        ["Musical"] = G("Music", "Animation", "Family"),
        ["Music Drama"] = G("Music", "Drama"),
        ["Animated Family"] = G("Animation"),
        ["Adult Animation"] = G("Animation"),
        ["Anime"] = G("Animation"),
        ["Documentary"] = G("Documentary"),
        ["Biopic"] = G("Drama", "History", "Music", "Documentary"),
        ["Historical Epic"] = G("History", "War", "Drama", "Adventure", "Action"),
        ["Romantic Drama"] = G("Romance", "Drama"),
    };

    private static readonly string[] HorrorTags = Fits.Where(f => f.Value[0] == "Horror").Select(f => f.Key).ToArray();

    public static List<string> KnownGenres(MovieMetadata movie) =>
        movie.GenreList.Select(g => GenreNames.GetValueOrDefault(g.Trim())).Where(g => g is not null).Select(g => g!).Distinct().ToList();

    /// <summary>Whether an issue line came from this check (so it can be cleared once a film is looked up).</summary>
    public static bool IsGenreLine(string line) =>
        line.Contains("doesn't fit the genres Jellyfin has") || line.StartsWith("Jellyfin says ") || line.StartsWith("Jellyfin lists it as");

    /// <summary>What looks off, in plain words; empty when nothing does.</summary>
    public static List<string> Problems(MovieMetadata movie)
    {
        var genres = KnownGenres(movie);
        var problems = new List<string>();
        if (genres.Count == 0 || !movie.IsClassified) return problems;
        var subs = movie.SubcategorySet.Items;
        var listed = string.Join(", ", genres);

        foreach (var sub in subs)
            if (Fits.TryGetValue(sub, out var fits) && !fits.Intersect(genres).Any())
                problems.Add($"\"{sub}\" doesn't fit the genres Jellyfin has ({listed})");

        if (genres.Contains("Documentary") && !subs.Contains("Documentary", StringComparer.OrdinalIgnoreCase) && !subs.Contains("Mockumentary", StringComparer.OrdinalIgnoreCase))
            problems.Add("Jellyfin says documentary, but it isn't tagged as one");
        if (genres.Contains("Animation") && genres.Count <= 2 && !subs.Any(s => s is "Animated Family" or "Adult Animation" or "Anime"))
            problems.Add("Jellyfin says animation, but no animation subcategory");
        if (genres.FirstOrDefault() == "Horror" && !subs.Any(s => HorrorTags.Contains(s)) && !movie.MoodSet.Items.Any(m => m is "eerie" or "disturbing" or "dark" or "tense"))
            problems.Add("Jellyfin lists it as horror first, but nothing in the tags says so");
        return problems;
    }
}
