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
            m.Title,
            OriginalTitle = m.OriginalTitle,
            Year = m.ReleaseYear,
            Genres = m.GenreList.Count > 0 ? m.GenreList : null,
            Countries = string.IsNullOrWhiteSpace(m.Countries) ? null : m.Countries,
            m.Director,
            Cast = string.IsNullOrWhiteSpace(m.Cast) ? null : m.Cast,
            Keywords = m.KeywordSet.Items.Count > 0 ? m.KeywordSet.Items.Take(15) : null,
            Plot = Cut(m.Plot, plotChars),
            FromTheWeb = string.IsNullOrWhiteSpace(m.WebNotes) ? null : Cut(m.WebNotes, 2500),
            // A flagged film being decided again: both sides of the argument
            EarlierTags = withReview && m.Issue is not null && m.IsClassified ? new { Subcategories = m.SubcategorySet.Items, Moods = m.MoodSet.Items } : null,
            Concerns = withReview && m.Issue is not null ? m.Issue.Split('\n') : null,
        };

        private static readonly JsonSerializerOptions Compact = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        public static string Build(IList<MovieMetadata> movies)
        {
            var moviesJson = JsonSerializer.Serialize(movies.Select((m, i) => Facts(m, i, withReview: true)), Compact);
            return $@"You classify movies for a recommendation engine. Use what you know about each film together with the facts given (genres, keywords, plot, and text from the web when present). The facts are reliable; don't contradict them.

Some movies were tagged before and questioned. They carry ""EarlierTags"" (what an AI said before) and ""Concerns"" (objections from a reviewing AI or an automatic genre check). Both are arguments, not facts, and either side can be wrong. Weigh them against the facts and the web text, keep earlier tags that fit, replace the ones that don't, and make your own decision.

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

Example output:
{{""movies"": [{{""Ref"": ""m1"", ""Known"": ""yes"", ""Subcategories"": [""Cyberpunk"", ""AI & Robots""], ""Moods"": [""cerebral"", ""thrilling""], ""Themes"": [""simulated reality"", ""rebellion"", ""identity""], ""NarrativeStyle"": ""hero-journey"", ""Accessibility"": ""mainstream"", ""Intensity"": ""high"", ""CriticalAcclaimScore"": 8}}]}}

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
            return $@"You review movie tags written by another AI for a recommendation engine. For each movie you get facts and the tags it was given.

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
