using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.Json;

namespace Pickyfin.Server.Data.Models
{
    public class MovieMetadata
    {
        public Guid ItemId { get; set; } // Jellyfin's internal BaseItem ID

        // Basic metadata
        public string Title { get; set; } = string.Empty;
        public int? ReleaseYear { get; set; }
        public string? ImdbId { get; set; }
        public int? TmdbId { get; set; }   // v1.5.14: TMDB movie id (ProviderIds["Tmdb"]) — used to fetch keywords
        // v1.5.21: TMDB "popularity" (a live 0..N score; blockbusters are in the tens/hundreds,
        // cult/obscure films are <1). Used to make "Hidden Gems" genuinely obscure. 0 = unknown
        // (no TMDB key configured or not yet enriched) → fame penalty is skipped.
        public double Popularity { get; set; } = 0;
        public string? Plot { get; set; }
        public string? Director { get; set; }
        public string? Cast { get; set; }
        /// <summary>Pickyfin: Jellyfin's genres (JSON array), original title and countries; facts for the AI and the genre check.</summary>
        public string? Genres { get; set; }
        public string? OriginalTitle { get; set; }
        public string? Countries { get; set; }
        /// <summary>Pickyfin: why these tags may be wrong ("Issues" page); null = nothing found.</summary>
        public string? Issue { get; set; }
        /// <summary>Pickyfin: a second model looked at the current tags (or the admin said they're fine).</summary>
        public bool Checked { get; set; }
        /// <summary>Pickyfin: what a web search found about the film, given to the AI when tagging again.</summary>
        public string? WebNotes { get; set; }
        /// <summary>Pickyfin: the admin looked at these tags and said they're right.</summary>
        public bool Verified { get; set; }
        /// <summary>When step 4 last re-checked this film (fresh lookup, described again). Null = not yet.</summary>
        public DateTime? Rescanned { get; set; }
        /// <summary>Queued to be described again (step 1 run again for the whole library).</summary>
        public bool RedoDescribe { get; set; }
        /// <summary>Set while a film is being re-checked: the prompt asks to change only clear mistakes.</summary>
        [NotMapped] public bool Recheck { get; set; }

        /// <summary>How far the tags can be trusted: "you", "web", "second", "ai" or "none".</summary>
        [NotMapped] public string Trust =>
            !IsClassified ? "none" : ManuallyEdited || Verified ? "you" : !string.IsNullOrEmpty(WebNotes) ? "web" : Checked && Issue is null ? "second" : "ai";

        [NotMapped] public List<string> GenreList => string.IsNullOrEmpty(Genres) ? new() : JsonSerializer.Deserialize<List<string>>(Genres) ?? new();

        // v2.0: Jellyfin's own ratings, synced on every index. CommunityRating is the
        // 0-10 audience score from the metadata provider; CriticRating is 0-100.
        public float? CommunityRating { get; set; }
        public float? CriticRating { get; set; }

        // AI Assigned Metadata
        public string? Subcategories { get => _subcategories; set { _subcategories = value; _subcategorySet = null; } } // JSON array of strings
        public string? Moods { get => _moods; set { _moods = value; _moodSet = null; } } // JSON array of strings
        public string? Themes { get => _themes; set { _themes = value; _themeSet = null; } } // JSON array of strings
        public string? NarrativeStyle { get; set; }
        public string? Accessibility { get; set; }
        public string? Intensity { get; set; }
        public int CriticalAcclaimScore { get; set; } = 0; // 1-10, AI estimate
        public string? Keywords { get => _keywords; set { _keywords = value; _keywordSet = null; } } // v1.5.14: JSON array of TMDB keyword strings (precision signal)
        public bool IsClassified { get; set; } = false;
        // v2.0: ClassificationVocabulary.Version the tags were produced under; 0 = free-form legacy tags.
        public int VocabularyVersion { get; set; } = 0;
        /// <summary>Pickyfin: tags were set by hand on the admin page; the AI never overwrites them.</summary>
        public bool ManuallyEdited { get; set; }
        /// <summary>Pickyfin: ids of the Jellyfin libraries this movie is in, comma separated.</summary>
        public string? Libraries { get; set; }
        public DateTime DateAdded { get; set; }
        public DateTime LastUpdated { get; set; }

        private string? _subcategories, _moods, _themes, _keywords;
        private TagSet? _subcategorySet, _moodSet, _themeSet, _keywordSet;

        // Parsed once per row instead of on every similarity comparison. The JSON
        // setters invalidate the cache, so reclassification is picked up immediately.
        [NotMapped] public TagSet SubcategorySet => _subcategorySet ??= TagSet.Parse(_subcategories);
        [NotMapped] public TagSet MoodSet => _moodSet ??= TagSet.Parse(_moods);
        [NotMapped] public TagSet ThemeSet => _themeSet ??= TagSet.Parse(_themes);
        [NotMapped] public TagSet KeywordSet => _keywordSet ??= TagSet.Parse(_keywords);

        /// <summary>
        /// Critical reception on a 0-10 scale. Prefers Jellyfin's real ratings (critic and
        /// community averaged when both exist) and falls back to the AI's estimate.
        /// </summary>
        [NotMapped]
        public double Acclaim
        {
            get
            {
                var community = CommunityRating is > 0 and <= 10 ? CommunityRating : null;
                var critic = CriticRating is > 0 and <= 100 ? CriticRating / 10.0 : null;
                if (community.HasValue && critic.HasValue) return (community.Value + critic.Value) / 2.0;
                if (community.HasValue) return community.Value;
                if (critic.HasValue) return critic.Value;
                return CriticalAcclaimScore;
            }
        }
    }

    /// <summary>An ordered, case-insensitive tag list parsed from a JSON string array.</summary>
    public sealed class TagSet
    {
        public static readonly TagSet Empty = new(Array.Empty<string>());

        private readonly HashSet<string> _set;

        private TagSet(IReadOnlyList<string> items)
        {
            Items = items;
            _set = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Tags in stored order (the first subcategory is the most defining).</summary>
        public IReadOnlyList<string> Items { get; }
        public int Count => Items.Count;
        public bool Contains(string tag) => _set.Contains(tag);

        public static TagSet Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Empty;
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list == null || list.Count == 0) return Empty;
                var items = list
                    .Select(s => s?.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return items.Length == 0 ? Empty : new TagSet(items);
            }
            catch (JsonException)
            {
                return Empty;
            }
        }

        public double Jaccard(TagSet other)
        {
            if (Count == 0 || other.Count == 0) return 0.0;
            int intersection = Items.Count(other.Contains);
            int union = Count + other.Count - intersection;
            return union == 0 ? 0.0 : (double)intersection / union;
        }
    }
}
