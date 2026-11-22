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

        public const string MovieKind = "movie", SeriesKind = "series";
        /// <summary>
        /// "movie" or "series". Series go through the same pipeline (tags, scores, meaning) but are
        /// never recommended (yet); they only tell us more about a person's taste.
        /// </summary>
        public string Kind { get; set; } = MovieKind;
        [NotMapped] public bool IsSeries => Kind == SeriesKind;

        // Series only: seasons and episodes in the library, the last year it aired, whether it's
        // still running ("Continuing" / "Ended"), where it aired, and what form it takes
        // (miniseries, ongoing, anthology, sitcom, procedural, docuseries).
        public int? Seasons { get; set; }
        public int? Episodes { get; set; }
        public int? EndYear { get; set; }
        public string? Status { get; set; }
        public string? Network { get; set; }
        public string? Format { get; set; }
        /// <summary>Series only: kinds of TV films don't have (sitcom, procedural…), JSON array. Compared between series only.</summary>
        public string? SeriesKinds { get => _seriesKinds; set { _seriesKinds = value; _seriesKindSet = null; } }
        private string? _seriesKinds;
        private TagSet? _seriesKindSet;
        [NotMapped] public TagSet SeriesKindSet => _seriesKindSet ??= TagSet.Parse(_seriesKinds);
        /// <summary>Series only: when someone switched on last finished an episode (watched series are described first).</summary>
        public DateTime? LastWatched { get; set; }

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
        /// <summary>Critics' score 0–100 from the web (Rotten Tomatoes Tomatometer, else Metacritic), when the text has one.</summary>
        public int? CriticsScore { get; set; }
        /// <summary>Audience score 0–100 from the web (Rotten Tomatoes Popcornmeter, else IMDb × 10).</summary>
        public int? AudienceScore { get; set; }
        public string? ScoreSource { get; set; }
        /// <summary>From OMDb: Rotten Tomatoes Tomatometer (0–100), Metacritic (0–100), IMDb rating (0–10). Only the OMDb job writes these.</summary>
        public int? RtScore { get; set; }
        public int? Metascore { get; set; }
        public double? ImdbRating { get; set; }
        /// <summary>When OMDb was last asked about this film (also when it had nothing).</summary>
        public DateTime? ScoresFetched { get; set; }
        /// <summary>JSON array of content notes (gore, animal harm, …), only when clearly present.</summary>
        public string? ContentNotes { get; set; }
        /// <summary>original, novel, true story, remake, …</summary>
        public string? BasedOn { get; set; }
        /// <summary>When and where it's set, a few words each ("1960s", "Paris").</summary>
        public string? SettingEra { get; set; }
        public string? SettingPlace { get; set; }
        /// <summary>Running time in minutes (from Jellyfin).</summary>
        public int? Minutes { get; set; }
        /// <summary>OMDb's awards line ("Won 3 Oscars. 45 wins & 112 nominations total"); "" = none. Only the OMDb job writes it.</summary>
        public string? Awards { get; set; }
        public int OscarWins { get; set; }
        public int OscarNominations { get; set; }
        /// <summary>TMDB: the series it belongs to, and the films TMDB's viewers liked alongside it. Only the TMDB job writes these.</summary>
        public int? CollectionId { get; set; }
        public string? CollectionName { get; set; }
        /// <summary>JSON array of TMDB ids TMDB recommends with this film.</summary>
        public string? TmdbRecs { get; set; }
        public DateTime? DetailsFetched { get; set; }
        /// <summary>slow, steady or fast.</summary>
        public string? Pacing { get; set; }
        /// <summary>How it leaves you: happy, bittersweet, open or bleak.</summary>
        public string? Ending { get; set; }
        /// <summary>JSON array: date night, family, with friends, alone.</summary>
        public string? Occasions { get; set; }
        /// <summary>
        /// From a letterboxd-collector service: Letterboxd's average (0.5–5 stars), how many people
        /// rated it, the film page, the rating spread (10 half-star counts, JSON) and a few of the
        /// most-liked reviews without spoilers (JSON). Only the Letterboxd job writes these.
        /// </summary>
        /// <summary>
        /// When Jellyfin stopped listing this film. Such films are hidden everywhere (a query
        /// filter) and only deleted when the admin confirms; they come back if Jellyfin lists them again.
        /// </summary>
        public DateTime? GoneSince { get; set; }
        public double? LbRating { get; set; }
        public int? LbCount { get; set; }
        public string? LbUrl { get; set; }
        public string? LbHistogram { get; set; }
        public string? LbReviews { get; set; }
        public DateTime? LbFetched { get; set; }
        [NotMapped] public List<Library.LetterboxdRatings.Review> LbReviewList =>
            string.IsNullOrEmpty(LbReviews) ? new() : JsonSerializer.Deserialize<List<Library.LetterboxdRatings.Review>>(LbReviews) ?? new();
        [NotMapped] public int[]? LbHistogramCounts => string.IsNullOrEmpty(LbHistogram) ? null : JsonSerializer.Deserialize<int[]>(LbHistogram);
        [NotMapped] public List<string> OccasionList => string.IsNullOrEmpty(Occasions) ? new() : JsonSerializer.Deserialize<List<string>>(Occasions) ?? new();
        private HashSet<int>? _recSet;
        private string? _recSource;
        [NotMapped] public HashSet<int> RecSet
        {
            get
            {
                if (_recSet is null || !ReferenceEquals(_recSource, TmdbRecs))
                {
                    _recSource = TmdbRecs;
                    try { _recSet = string.IsNullOrEmpty(TmdbRecs) ? new() : JsonSerializer.Deserialize<HashSet<int>>(TmdbRecs) ?? new(); }
                    catch (JsonException) { _recSet = new(); }
                }
                return _recSet;
            }
        }
        [NotMapped] public List<string> ContentNoteList => string.IsNullOrEmpty(ContentNotes) ? new() : JsonSerializer.Deserialize<List<string>>(ContentNotes) ?? new();
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
        /// <summary>The rating from real scores only (OMDb, the web text, Jellyfin), 0-10; null when none is known.</summary>
        [NotMapped]
        public double? RealRating
        {
            get
            {
                // OMDb's real scores first, then scores the web text stated, then Jellyfin's
                var community = ImdbRating is > 0 and <= 10 ? ImdbRating
                    : AudienceScore is > 0 and <= 100 ? AudienceScore / 10.0 : CommunityRating is > 0 and <= 10 ? CommunityRating : null;
                var critic = RtScore is > 0 and <= 100 ? RtScore / 10.0 : Metascore is > 0 and <= 100 ? Metascore / 10.0
                    : CriticsScore is > 0 and <= 100 ? CriticsScore / 10.0 : CriticRating is > 0 and <= 100 ? CriticRating / 10.0 : null;
                // Letterboxd counts most (it's the audience Pickyfin trusts most), once enough people rated it
                double? letterboxd = LbRating is > 0 and <= 5 ? LbRating * 2 : null;
                var lbWeight = LbCount switch { >= 1000 => 2.0, >= 100 => 1.0, _ => 0.5 };
                double sum = 0, weight = 0;
                if (critic is { } c) { sum += c; weight += 1; }
                if (community is { } co) { sum += co; weight += 1; }
                if (letterboxd is { } lb) { sum += lb * lbWeight; weight += lbWeight; }
                return weight > 0 ? sum / weight : null;
            }
        }

        /// <summary>Real scores when known, else the AI's estimate.</summary>
        [NotMapped]
        public double Acclaim => RealRating ?? CriticalAcclaimScore;
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
