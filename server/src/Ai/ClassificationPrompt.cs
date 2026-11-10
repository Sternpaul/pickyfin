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

        public static string Build(IEnumerable<MovieMetadata> movies)
        {
            var moviesJson = JsonSerializer.Serialize(movies.Select(m => new { m.ItemId, m.Title, m.ReleaseYear, m.Plot }));
            return $@"You classify movies for a recommendation engine. Use what you know about each film, not just the plot text.

Return a JSON object with a ""movies"" array, one entry per input movie, with these fields:
- ""ItemId"": copy the input ItemId exactly.
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
{{""movies"": [{{""ItemId"": ""00000000-0000-0000-0000-000000000000"", ""Subcategories"": [""Cyberpunk"", ""AI & Robots""], ""Moods"": [""cerebral"", ""thrilling""], ""Themes"": [""simulated reality"", ""rebellion"", ""identity""], ""NarrativeStyle"": ""hero-journey"", ""Accessibility"": ""mainstream"", ""Intensity"": ""high"", ""CriticalAcclaimScore"": 8}}]}}

Output ONLY the JSON object, no markdown.
Input:
{moviesJson}";
        }
    }
}
