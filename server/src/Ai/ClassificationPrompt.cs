using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;

namespace Pickyfin.Server.Ai
{
    /// <summary>The one classification prompt every provider sends.</summary>
    public static class ClassificationPrompt
    {
        private static string Quoted(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"\"{v}\""));

        /// <summary>Short reference for a movie in a batch ("m1", "m2", …); long ids get garbled by models.</summary>
        public static string Ref(int index) => $"m{index + 1}";

        private static string Cut(string? text, int max) =>
            string.IsNullOrWhiteSpace(text) ? "" : text.Length <= max ? text : text[..max] + "…";

        /// <summary>Everything we know about a movie, compact, for a prompt.</summary>
        public static object Facts(MovieMetadata m, int index, int plotChars = 900, bool withReview = false) => new
        {
            Ref = Ref(index),
            // Films carry no type; a series says so, with what's known about its run
            Type = m.IsSeries ? "TV series" : null,
            m.Title,
            OriginalTitle = m.OriginalTitle,
            Year = m.IsSeries ? null : m.ReleaseYear,
            Aired = m.IsSeries ? Aired(m) : null,
            Seasons = m.IsSeries ? m.Seasons : null,
            Episodes = m.IsSeries ? m.Episodes : null,
            Network = m.IsSeries ? m.Network : null,
            Genres = m.GenreList.Count > 0 ? m.GenreList : null,
            Countries = string.IsNullOrWhiteSpace(m.Countries) ? null : m.Countries,
            // For a series the "director" field holds its creators (from TMDB)
            Director = m.IsSeries ? null : m.Director,
            CreatedBy = m.IsSeries ? m.Director : null,
            Cast = string.IsNullOrWhiteSpace(m.Cast) ? null : m.Cast,
            Keywords = m.KeywordSet.Items.Count > 0 ? m.KeywordSet.Items.Take(15) : null,
            Plot = Cut(m.Plot, plotChars),
            FromTheWeb = string.IsNullOrWhiteSpace(m.WebNotes) ? null : Cut(m.WebNotes, 10000),
            // Described again (flagged, or looked up on the web): the first opinion and what the review said
            EarlierTags = withReview && Reviewed(m) ? new { Subcategories = m.SubcategorySet.Items, Moods = m.MoodSet.Items } : null,
            Review = withReview && Reviewed(m) ? ReviewLines(m) : null,
            Recheck = withReview && m.Recheck ? "yes" : null,
        };

        /// <summary>"2008–2013", "2019–" (still running) or "2019".</summary>
        public static string? Aired(MovieMetadata m) =>
            m.ReleaseYear is not { } start ? null
            : string.Equals(m.Status, "Continuing", StringComparison.OrdinalIgnoreCase) ? $"{start}–"
            : m.EndYear is { } end && end != start ? $"{start}–{end}" : $"{start}";

        private static bool Reviewed(MovieMetadata m) =>
            m.IsClassified && (m.Issue is not null || !string.IsNullOrWhiteSpace(m.WebNotes) || m.Recheck);

        private static string[] ReviewLines(MovieMetadata m) =>
            m.Issue is not null ? m.Issue.Split('\n')
            : m.Checked ? new[] { "A second AI reviewed these tags and found nothing wrong" }
            : new[] { "Not reviewed by a second AI" };

        private static readonly JsonSerializerOptions Compact = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        public static string Build(IList<MovieMetadata> movies)
        {
            var moviesJson = JsonSerializer.Serialize(movies.Select((m, i) => Facts(m, i, withReview: true)), Compact);
            return $@"You classify movies and TV series for a recommendation engine. Entries with ""Type"": ""TV series"" are series: describe the series as a whole (all its seasons), not one episode. Use what you know about each film together with the facts given (genres, keywords, plot, and text from the web when present). The facts are reliable; don't contradict them.

Some movies are being described again. They carry ""EarlierTags"" (what an AI said the first time) and ""Review"" (what a second AI and an automatic genre check said about those tags: objections, or that they found nothing wrong). These are opinions, not facts, and any of them can be wrong. When ""FromTheWeb"" is there, it is the strongest evidence. Weigh everything together, keep earlier tags that fit, replace the ones that don't, and make your own decision.
Movies with ""Recheck"": ""yes"" are a routine re-check of films described before with all of this. Keep their earlier tags unless something is clearly wrong or the web text now clearly says otherwise; don't swap a fitting tag for another fitting one.

Return a JSON object with a ""movies"" array, one entry per input movie, with these fields:
- ""Ref"": copy the input Ref exactly (""m1"", ""m2"", …).
- ""Known"": how well you know this film: ""yes"" (you know it), ""partly"" (you recognise it but are unsure of details), or ""no"" (you don't know it and are going only by the facts given). Be honest; ""no"" is fine.
- ""Subcategories"": 1 to 3 values, most defining first, chosen ONLY from:
  {Quoted(ClassificationVocabulary.Subcategories)}
  Never use broad genres such as ""Action"", ""Drama"", ""Comedy"", ""Horror"" or ""Thriller"" on their own.
- ""Moods"": 2 to 4 values chosen ONLY from:
  {Quoted(ClassificationVocabulary.Moods)}
- ""Themes"": 3 to 5 short lowercase phrases (free text), e.g. ""grief"", ""class divide"", ""found family"".
- ""NarrativeStyle"": exactly one of {Quoted(ClassificationVocabulary.NarrativeStyles)}.
- ""Accessibility"": exactly one of {Quoted(ClassificationVocabulary.Accessibility)}.
- ""Intensity"": exactly one of {Quoted(ClassificationVocabulary.Intensity)}.
- ""CriticalAcclaimScore"": integer 1-10 for critical reception (5 = average, 9+ = canonical classic).
- ""CriticsScore"": integer 0-100, ONLY if ""FromTheWeb"" states one: the Rotten Tomatoes Tomatometer, else the Metacritic score. Otherwise null. Never guess.
- ""AudienceScore"": integer 0-100, ONLY if ""FromTheWeb"" states one: the Rotten Tomatoes Popcornmeter (audience score), else the IMDb rating × 10. Otherwise null.
- ""ScoreSource"": where those scores come from, e.g. ""Rotten Tomatoes"", or null.
- ""ContentNotes"": things some viewers avoid, ONLY when clearly present in the film (may be empty), chosen ONLY from: {Quoted(ClassificationVocabulary.ContentNotes)}
- ""BasedOn"": exactly one of {Quoted(ClassificationVocabulary.BasedOn)}.
- ""SettingEra"": when it's set, 1-3 words (e.g. ""1960s"", ""medieval"", ""near future""), or null.
- ""SettingPlace"": where it's set, 1-3 words (e.g. ""Paris"", ""rural Japan"", ""space""), or null.
- ""Pacing"": exactly one of {Quoted(ClassificationVocabulary.Pacing)}.
- ""Ending"": how the ending leaves the viewer, exactly one of {Quoted(ClassificationVocabulary.Endings)}.
- ""Occasions"": 1 or 2 situations it suits best, ONLY from: {Quoted(ClassificationVocabulary.Occasions)} (""alone"" = needs full attention or is too dark to share).
- ""Format"": ONLY for TV series, exactly one of {Quoted(ClassificationVocabulary.Formats)} (the most specific that fits: a crime series solving a case per episode is ""procedural"", a closed story in one or two seasons ""miniseries"", a story told over many seasons ""ongoing""); leave it out for films. For a series, ""Ending"" is how the series ends so far (""open"" if it's still running), and ""BasedOn"" is what the series is based on.

Example output:
{{""movies"": [{{""Ref"": ""m1"", ""Known"": ""yes"", ""Subcategories"": [""Cyberpunk"", ""AI & Robots""], ""Moods"": [""cerebral"", ""thrilling""], ""Themes"": [""simulated reality"", ""rebellion"", ""identity""], ""NarrativeStyle"": ""hero-journey"", ""Accessibility"": ""mainstream"", ""Intensity"": ""high"", ""CriticalAcclaimScore"": 8, ""CriticsScore"": 83, ""AudienceScore"": 85, ""ScoreSource"": ""Rotten Tomatoes"", ""ContentNotes"": [""graphic violence""], ""BasedOn"": ""original"", ""SettingEra"": ""2199"", ""SettingPlace"": ""simulated city"", ""Pacing"": ""fast"", ""Ending"": ""open"", ""Occasions"": [""with friends"", ""date night""]}}]}}

Output ONLY the JSON object, no markdown. Include every input movie.
Input:
{moviesJson}";
        }

        /// <summary>The second opinion: a different model looks at existing tags and names only what's wrong.</summary>
        public static string BuildCheck(IList<MovieMetadata> movies)
        {
            var items = movies.Select((m, i) => new
            {
                Facts = Facts(m, i, plotChars: 350),
                Tagged = new { Subcategories = m.SubcategorySet.Items, Moods = m.MoodSet.Items },
            });
            var json = JsonSerializer.Serialize(items, Compact);
            return $@"You review movie tags written by another AI for a recommendation engine. For each movie you get facts and the tags it was given. Entries with ""Type"": ""TV series"" are series, tagged as a whole; judge them the same way.

Subcategories come from this fixed list: {Quoted(ClassificationVocabulary.Subcategories)}
Moods come from this fixed list: {Quoted(ClassificationVocabulary.Moods)}

Report ONLY movies whose tags are clearly wrong or misleading, e.g. a subcategory that doesn't describe the film, or a mood that is the opposite of the film. Small matters of taste are fine; leave those out. If you don't know a film, judge by the facts and only report obvious contradictions.

Return a JSON object: {{""problems"": [{{""Ref"": ""m3"", ""Wrong"": [""Heist""], ""Better"": [""Crime Drama""], ""Why"": ""one short sentence""}}]}}
""Wrong"" lists the tags that don't fit; ""Better"" suggests replacements from the lists above (may be empty). If every movie is fine, return {{""problems"": []}}.

Output ONLY the JSON object, no markdown.
Movies:
{json}";
        }
    }
}
