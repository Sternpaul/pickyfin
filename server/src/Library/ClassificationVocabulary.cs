using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pickyfin.Server.Library
{
    /// <summary>
    /// The closed tag vocabulary the AI classifies into. Similarity, taste profiles and
    /// playlist names all compare tags as exact strings, so free-form LLM output
    /// ("Sci-Fi" vs "Science Fiction" vs "Space Opera") silently splits one taste into
    /// several. Every tag is mapped onto these canonical names before it is stored.
    /// </summary>
    public static class ClassificationVocabulary
    {
        /// <summary>Bump when the vocabulary changes enough that old classifications should be redone.</summary>
        public const int Version = 2;

        public static readonly IReadOnlyList<string> Subcategories = new[]
        {
            // Thriller / crime / mystery
            "Psychological Thriller", "Political Thriller", "Conspiracy Thriller", "Techno-Thriller",
            "Erotic Thriller", "Action Thriller", "Spy", "Heist", "Neo-Noir", "Film Noir",
            "Crime Drama", "Gangster", "Detective Mystery", "Whodunit", "Serial Killer",
            "Courtroom Drama", "Revenge", "Survival",
            // Horror
            "Supernatural Horror", "Psychological Horror", "Folk Horror", "Body Horror",
            "Cosmic Horror", "Slasher", "Found Footage", "Zombie", "Vampire", "Monster Movie",
            "Horror Comedy",
            // Sci-fi / fantasy
            "Space Opera", "Hard Sci-Fi", "Cyberpunk", "Dystopian", "Post-Apocalyptic",
            "Time Travel", "Alien Invasion", "AI & Robots", "Superhero", "Fantasy Epic",
            "Dark Fantasy", "Fairy Tale", "Sword & Sorcery",
            // Action / adventure
            "Martial Arts", "Disaster", "Adventure", "Swashbuckler", "War", "Military Action",
            "Western", "Neo-Western",
            // Drama
            "Coming-of-Age", "Family Drama", "Romantic Drama", "Melodrama", "Period Drama",
            "Historical Epic", "Biopic", "Sports Drama", "Music Drama", "Social Drama",
            "Character Study", "Medical Drama",
            // Comedy
            "Romantic Comedy", "Dark Comedy", "Satire", "Parody", "Slapstick", "Buddy Comedy",
            "Teen Comedy", "Comedy of Manners", "Mockumentary",
            // Form / other
            "Musical", "Road Movie", "Animated Family", "Adult Animation", "Anime",
            "Documentary", "Arthouse", "Surreal", "Experimental", "Christmas",
        };

        public static readonly IReadOnlyList<string> Moods = new[]
        {
            "tense", "dark", "bleak", "eerie", "disturbing", "gritty", "intense", "thrilling",
            "suspenseful", "cerebral", "contemplative", "melancholic", "emotional", "romantic",
            "heartwarming", "uplifting", "hopeful", "inspiring", "feel-good", "funny", "quirky",
            "whimsical", "playful", "nostalgic", "epic", "atmospheric", "dreamlike", "cozy",
        };

        public static readonly IReadOnlyList<string> NarrativeStyles = new[]
        {
            "linear", "nonlinear", "hero-journey", "ensemble", "character-study", "anthology",
            "mystery-box", "slow-burn", "episodic", "real-time", "unreliable-narrator",
            "documentary-style",
        };

        public static readonly IReadOnlyList<string> Accessibility = new[] { "mainstream", "accessible", "niche", "challenging" };

        public static readonly IReadOnlyList<string> Intensity = new[] { "low", "medium", "high" };

        // Common LLM variants that key normalization alone cannot fold. Keys are
        // normalized with Key(); values must be canonical names.
        private static readonly Dictionary<string, string> SubcategoryAliases = BuildAliases(new Dictionary<string, string>
        {
            ["espionage"] = "Spy", ["spy thriller"] = "Spy", ["spy film"] = "Spy",
            ["heist thriller"] = "Heist", ["caper"] = "Heist",
            ["noir"] = "Film Noir", ["neonoir thriller"] = "Neo-Noir",
            ["black comedy"] = "Dark Comedy", ["romcom"] = "Romantic Comedy",
            ["biography"] = "Biopic", ["biographical"] = "Biopic", ["biographical drama"] = "Biopic",
            ["creature feature"] = "Monster Movie", ["kaiju"] = "Monster Movie",
            ["space adventure"] = "Space Opera", ["space western"] = "Space Opera",
            ["dystopia"] = "Dystopian", ["dystopian scifi"] = "Dystopian",
            ["postapocalypse"] = "Post-Apocalyptic", ["apocalyptic"] = "Post-Apocalyptic",
            ["whodunnit"] = "Whodunit", ["murder mystery"] = "Whodunit",
            ["detective"] = "Detective Mystery", ["mystery"] = "Detective Mystery", ["police procedural"] = "Detective Mystery",
            ["legal drama"] = "Courtroom Drama", ["legal thriller"] = "Courtroom Drama",
            ["mob"] = "Gangster", ["mafia"] = "Gangster", ["gangster film"] = "Gangster",
            ["war drama"] = "War", ["war film"] = "War", ["war epic"] = "War",
            ["historical drama"] = "Period Drama", ["costume drama"] = "Period Drama",
            ["epic"] = "Historical Epic", ["historical"] = "Historical Epic",
            ["teen drama"] = "Coming-of-Age", ["coming of age drama"] = "Coming-of-Age",
            ["comic book"] = "Superhero", ["superhero action"] = "Superhero",
            ["high fantasy"] = "Fantasy Epic", ["epic fantasy"] = "Fantasy Epic",
            ["sword and sorcery"] = "Sword & Sorcery",
            ["artificial intelligence"] = "AI & Robots", ["robots"] = "AI & Robots", ["ai"] = "AI & Robots",
            ["haunted house"] = "Supernatural Horror", ["ghost story"] = "Supernatural Horror", ["possession"] = "Supernatural Horror",
            ["lovecraftian"] = "Cosmic Horror", ["kung fu"] = "Martial Arts", ["wuxia"] = "Martial Arts",
            ["spaghetti western"] = "Western", ["revisionist western"] = "Neo-Western",
            ["family animation"] = "Animated Family", ["kids animation"] = "Animated Family",
            ["documentary film"] = "Documentary", ["docudrama"] = "Documentary",
            ["art house"] = "Arthouse", ["surrealist"] = "Surreal", ["surrealism"] = "Surreal",
            ["holiday"] = "Christmas", ["christmas movie"] = "Christmas",
            ["romance"] = "Romantic Drama", ["romantic"] = "Romantic Drama",
            ["survival thriller"] = "Survival", ["revenge thriller"] = "Revenge",
            ["erotic"] = "Erotic Thriller", ["sports"] = "Sports Drama", ["sport"] = "Sports Drama",
            ["music"] = "Music Drama", ["musical drama"] = "Music Drama",
            ["political drama"] = "Political Thriller", ["conspiracy"] = "Conspiracy Thriller",
            ["techno thriller"] = "Techno-Thriller", ["cyber thriller"] = "Techno-Thriller",
            ["disaster film"] = "Disaster", ["horror comedy"] = "Horror Comedy", ["comedy horror"] = "Horror Comedy",
            ["social commentary"] = "Social Drama", ["social realism"] = "Social Drama",
            ["character driven drama"] = "Character Study", ["hospital drama"] = "Medical Drama",
        });

        private static readonly Dictionary<string, string> MoodAliases = BuildAliases(new Dictionary<string, string>
        {
            ["suspense"] = "suspenseful", ["thoughtprovoking"] = "cerebral", ["intellectual"] = "cerebral",
            ["sad"] = "melancholic", ["melancholy"] = "melancholic", ["moving"] = "emotional", ["touching"] = "heartwarming",
            ["humorous"] = "funny", ["comedic"] = "funny", ["hilarious"] = "funny", ["lighthearted"] = "playful",
            ["light"] = "playful", ["scary"] = "eerie", ["creepy"] = "eerie", ["unsettling"] = "disturbing",
            ["grim"] = "bleak", ["exciting"] = "thrilling", ["actionpacked"] = "thrilling", ["adrenaline"] = "thrilling",
            ["reflective"] = "contemplative", ["meditative"] = "contemplative", ["philosophical"] = "cerebral",
            ["feelgood"] = "feel-good", ["optimistic"] = "hopeful", ["charming"] = "whimsical", ["magical"] = "whimsical",
            ["surreal"] = "dreamlike", ["hypnotic"] = "dreamlike", ["grand"] = "epic", ["sweeping"] = "epic",
            ["moody"] = "atmospheric", ["brooding"] = "dark", ["violent"] = "intense", ["raw"] = "gritty",
            ["warm"] = "cozy", ["comforting"] = "cozy", ["sentimental"] = "nostalgic", ["passionate"] = "romantic",
        });

        private static readonly Dictionary<string, string> NarrativeAliases = BuildAliases(new Dictionary<string, string>
        {
            ["heros journey"] = "hero-journey", ["heroes journey"] = "hero-journey", ["chronological"] = "linear",
            ["non linear"] = "nonlinear", ["fragmented"] = "nonlinear", ["flashback"] = "nonlinear",
            ["ensemble cast"] = "ensemble", ["multistrand"] = "ensemble", ["character driven"] = "character-study",
            ["slowburn"] = "slow-burn", ["vignettes"] = "anthology", ["puzzle"] = "mystery-box",
            ["twist"] = "mystery-box", ["mockumentary"] = "documentary-style", ["found footage"] = "documentary-style",
        });

        private static readonly Dictionary<string, string> AccessibilityAliases = BuildAliases(new Dictionary<string, string>
        {
            ["popular"] = "mainstream", ["blockbuster"] = "mainstream", ["broad"] = "mainstream",
            ["moderate"] = "accessible", ["cult"] = "niche", ["indie"] = "niche",
            ["arthouse"] = "challenging", ["art house"] = "challenging", ["experimental"] = "challenging", ["demanding"] = "challenging",
        });

        private static readonly Dictionary<string, string> IntensityAliases = BuildAliases(new Dictionary<string, string>
        {
            ["mild"] = "low", ["gentle"] = "low", ["calm"] = "low", ["moderate"] = "medium", ["mid"] = "medium",
            ["extreme"] = "high", ["very high"] = "high", ["intense"] = "high",
        });

        private static readonly Dictionary<string, string> SubcategoryIndex = BuildIndex(Subcategories, SubcategoryAliases);
        private static readonly Dictionary<string, string> MoodIndex = BuildIndex(Moods, MoodAliases);
        private static readonly Dictionary<string, string> NarrativeIndex = BuildIndex(NarrativeStyles, NarrativeAliases);
        private static readonly Dictionary<string, string> AccessibilityIndex = BuildIndex(Accessibility, AccessibilityAliases);
        private static readonly Dictionary<string, string> IntensityIndex = BuildIndex(Intensity, IntensityAliases);

        public static string? CanonicalSubcategory(string? raw) => Lookup(SubcategoryIndex, raw);
        public static string? CanonicalMood(string? raw) => Lookup(MoodIndex, raw);
        public static string? CanonicalNarrativeStyle(string? raw) => Lookup(NarrativeIndex, raw);
        public static string? CanonicalAccessibility(string? raw) => Lookup(AccessibilityIndex, raw);
        public static string? CanonicalIntensity(string? raw) => Lookup(IntensityIndex, raw);

        /// <summary>
        /// Maps raw tags onto canonical ones, dropping duplicates. Unknown tags are
        /// dropped when <paramref name="keepUnknown"/> is false (new AI output) and kept
        /// verbatim when true (legacy rows, so an upgrade never empties a movie).
        /// </summary>
        public static List<string> Canonicalize(IEnumerable<string> raw, Func<string?, string?> canonical, bool keepUnknown)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in raw)
            {
                var trimmed = tag?.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var value = canonical(trimmed) ?? (keepUnknown ? trimmed : null);
                if (value != null && seen.Add(value)) result.Add(value);
            }
            return result;
        }

        /// <summary>Free-form themes stay open, but are folded to one lowercase spelling.</summary>
        public static List<string> NormalizeThemes(IEnumerable<string> raw) => raw
            .Select(t => t?.Trim().ToLowerInvariant())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .Take(6)
            .ToList();

        // Lowercase, strip everything except letters/digits/spaces, collapse spaces,
        // drop a trailing plural "s" and "film"/"movie" suffixes. "Sci-Fi Thrillers"
        // and "scifi thriller" therefore share one key.
        internal static string Key(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '_' || c == '/') sb.Append(' ');
                else if (c == '&') sb.Append(" and ");
            }
            var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 1 && (words[^1] == "film" || words[^1] == "films" || words[^1] == "movie" || words[^1] == "movies"))
                words.RemoveAt(words.Count - 1);
            for (int i = 0; i < words.Count; i++)
                if (words[i].Length > 3 && words[i].EndsWith('s') && !words[i].EndsWith("ss") && !words[i].EndsWith("us"))
                    words[i] = words[i][..^1];
            return string.Join(' ', words);
        }

        private static string? Lookup(Dictionary<string, string> index, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var key = Key(raw);
            if (index.TryGetValue(key, out var hit)) return hit;
            // "scifi" vs "sci fi": also try with spaces removed.
            return index.TryGetValue(key.Replace(" ", string.Empty), out hit) ? hit : null;
        }

        // Two spellings may fold to one key ("sport"/"sports"); that is fine as long as
        // they agree. A disagreement is a vocabulary bug and fails loudly.
        private static Dictionary<string, string> BuildAliases(Dictionary<string, string> aliases)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (alias, value) in aliases)
            {
                var key = Key(alias);
                if (result.TryGetValue(key, out var existing) && existing != value)
                    throw new InvalidOperationException($"Alias '{alias}' maps to both '{existing}' and '{value}'.");
                result[key] = value;
            }
            return result;
        }

        private static Dictionary<string, string> BuildIndex(IReadOnlyList<string> canonical, Dictionary<string, string> aliases)
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (alias, value) in aliases)
            {
                if (!canonical.Contains(value)) throw new InvalidOperationException($"Alias target '{value}' is not canonical.");
                index[alias] = value;
                index[alias.Replace(" ", string.Empty)] = value;
            }
            foreach (var value in canonical)
            {
                index[Key(value)] = value;
                index[Key(value).Replace(" ", string.Empty)] = value;
            }
            return index;
        }
    }
}
