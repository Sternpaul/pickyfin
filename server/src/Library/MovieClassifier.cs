using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Ai;
using Pickyfin.Server.Library;
using Microsoft.Extensions.Logging;

namespace Pickyfin.Server.Library
{
    public class MovieClassifier
    {
        private readonly MovieStore _movieStore;
        private readonly AIProviderFactory _aiProviderFactory;
        private readonly ILogger<MovieClassifier> _logger;

        private const int BatchSize = 20;

        public MovieClassifier(
            MovieStore movieStore,
            AIProviderFactory aiProviderFactory,
            ILogger<MovieClassifier> logger)
        {
            _movieStore = movieStore;
            _aiProviderFactory = aiProviderFactory;
            _logger = logger;
        }

        public async Task ClassifyPendingMoviesAsync(CancellationToken cancellationToken = default)
        {
            // Re-tagging movies classified under an older vocabulary costs one AI call
            // per batch, so it only happens when the admin opts in.
            var includeOutdated = ServerSettings.Current.ReclassifyOutdatedMovies;
            _logger.LogInformation("Checking for unclassified movies (including outdated tags: {IncludeOutdated})...", includeOutdated);
            
            var unclassified = await _movieStore.GetMoviesNeedingClassificationAsync(includeOutdated, cancellationToken);
            if (!unclassified.Any())
            {
                _logger.LogInformation("No movies require classification.");
                return;
            }

            _logger.LogInformation("Found {Count} unclassified movies. Starting batch classification...", unclassified.Count);
            
            var provider = _aiProviderFactory.GetProvider();
            
            // Process in batches
            for (int i = 0; i < unclassified.Count; i += BatchSize)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var batch = unclassified.Skip(i).Take(BatchSize).ToList();
                _logger.LogInformation("Classifying batch {Start} to {End} of {Total}...", i + 1, i + batch.Count, unclassified.Count);

                try
                {
                    var jsonResponse = await provider.ClassifyMoviesAsync(batch, cancellationToken);
                    _logger.LogDebug("Raw AI response (first 2000 chars): {Response}", jsonResponse?.Length > 2000 ? jsonResponse.Substring(0, 2000) : jsonResponse);
                    if (!string.IsNullOrEmpty(jsonResponse))
                        ProcessClassificationResult(jsonResponse, batch);
                    
                    // Save batch to DB
                    await _movieStore.SaveMoviesAsync(batch, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to classify batch. Aborting the current classification run to prevent rate-limit spam.");
                    break;
                }
            }
            
            _logger.LogInformation("Classification complete.");
        }

        private void ProcessClassificationResult(string? jsonResponse, List<MovieMetadata> batch)
        {
            if (string.IsNullOrEmpty(jsonResponse))
            {
                _logger.LogWarning("Classification returned an empty response; skipping batch.");
                return;
            }
            try
            {
                var jsonToParse = jsonResponse.Trim();
                
                // Sometimes the LLM wraps it in ```json ... ```
                var match = System.Text.RegularExpressions.Regex.Match(jsonToParse, @"```(?:json)?\s*([\s\S]*?)\s*```");
                if (match.Success)
                {
                    jsonToParse = match.Groups[1].Value.Trim();
                }
                else
                {
                    // Fallback to searching for the first [ or { and the last ] or }
                    int startIndex = jsonToParse.IndexOfAny(new[] { '[', '{' });
                    int endIndex = jsonToParse.LastIndexOfAny(new[] { ']', '}' });
                    if (startIndex >= 0 && endIndex > startIndex)
                    {
                        jsonToParse = jsonToParse.Substring(startIndex, endIndex - startIndex + 1);
                    }
                }

                // Different providers might wrap the response slightly differently based on their JSON mode.
                // We'll parse dynamically to find the array of movies.
                using var document = JsonDocument.Parse(jsonToParse);
                
                JsonElement moviesArray;
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    moviesArray = document.RootElement;
                }
                else if (document.RootElement.TryGetProperty("movies", out var propArray))
                {
                    moviesArray = propArray;
                }
                else
                {
                    _logger.LogWarning("Could not find movies array in JSON response.");
                    return;
                }

                int index = 0;
                foreach (var element in moviesArray.EnumerateArray())
                {
                    MovieMetadata? movie = null;

                    // 1. Try matching by ItemId or Id property
                    if (TryGetPropertyIgnoreCase(element, "ItemId", out var idProp) || TryGetPropertyIgnoreCase(element, "id", out idProp))
                    {
                        var idString = idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : idProp.ToString();
                        if (Guid.TryParse(idString, out var itemId))
                        {
                            movie = batch.FirstOrDefault(m => m.ItemId == itemId);
                        }
                    }

                    // 2. Fallback to Title matching if ItemId didn't match
                    if (movie == null && (TryGetPropertyIgnoreCase(element, "Title", out var titleProp) || TryGetPropertyIgnoreCase(element, "title", out titleProp)))
                    {
                        var titleStr = titleProp.GetString();
                        if (!string.IsNullOrWhiteSpace(titleStr))
                        {
                            movie = batch.FirstOrDefault(m => m.Title.Equals(titleStr, StringComparison.OrdinalIgnoreCase) || m.Title.StartsWith(titleStr, StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    // 3. Fallback to array index
                    if (movie == null && index < batch.Count)
                    {
                        movie = batch[index];
                    }

                    if (movie == null)
                    {
                        index++;
                        continue;
                    }

                    ApplyClassification(
                        movie,
                        GetStringArray(element, "Subcategories"),
                        GetStringArray(element, "Moods"),
                        GetStringArray(element, "Themes"),
                        GetStringProperty(element, "NarrativeStyle"),
                        GetStringProperty(element, "Accessibility"),
                        GetStringProperty(element, "Intensity"),
                        GetIntProperty(element, "CriticalAcclaimScore"));
                    index++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse JSON, attempting fallback text parsing. Error: {Msg}", ex.Message);
                
                var successCount = 0;
                var lines = jsonResponse.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\*\s*\*(?<title>[^*]+)\*:\s*(?<subcategories>[^.]+)\.\s*Moods:\s*(?<moods>[^.]+)\.\s*Themes:\s*(?<themes>[^.]+)\.\s*Style:\s*(?<style>[^.]+)\.\s*Accessibility:\s*(?<acc>[^.]+)\.\s*Intensity:\s*(?<int>[^.]+)\.\s*Score:\s*(?<score>\d+)");
                    if (match.Success)
                    {
                        var title = match.Groups["title"].Value.Trim();
                        var movie = batch.FirstOrDefault(m => m.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase) || m.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
                        if (movie != null)
                        {
                            static List<string> SplitList(string value) => value
                                .Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => s.Length > 0)
                                .ToList();
                            int.TryParse(match.Groups["score"].Value, out int scoreVal);
                            ApplyClassification(
                                movie,
                                SplitList(match.Groups["subcategories"].Value),
                                SplitList(match.Groups["moods"].Value),
                                SplitList(match.Groups["themes"].Value),
                                match.Groups["style"].Value.Trim(),
                                match.Groups["acc"].Value.Trim(),
                                match.Groups["int"].Value.Trim(),
                                scoreVal);
                            successCount++;
                        }
                    }
                }
                
                if (successCount == 0)
                {
                    _logger.LogError(ex, "Error parsing AI classification JSON and fallback failed: {Json}", jsonResponse);
                }
                else
                {
                    _logger.LogInformation("Successfully parsed {Count} movies using text fallback.", successCount);
                }
            }
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
            value = default;
            return false;
        }

        /// <summary>
        /// Stores one AI result on the movie, mapped onto the canonical vocabulary.
        /// Tags outside the vocabulary are dropped; if the model ignored the vocabulary
        /// entirely its raw subcategories are kept rather than losing the movie, and the
        /// row stays on the legacy version so an opt-in re-classify can fix it later.
        /// </summary>
        internal void ApplyClassification(
            MovieMetadata movie,
            IReadOnlyList<string> rawSubcategories,
            IReadOnlyList<string> rawMoods,
            IReadOnlyList<string> rawThemes,
            string? narrativeStyle,
            string? accessibility,
            string? intensity,
            int score)
        {
            var subcategories = ClassificationVocabulary.Canonicalize(rawSubcategories, ClassificationVocabulary.CanonicalSubcategory, keepUnknown: false);
            var onVocabulary = subcategories.Count > 0;
            if (!onVocabulary)
            {
                subcategories = ClassificationVocabulary.Canonicalize(rawSubcategories, ClassificationVocabulary.CanonicalSubcategory, keepUnknown: true);
                if (subcategories.Count > 0)
                    _logger.LogWarning(
                        "AI returned no known subcategories for '{Title}' ({Raw}); keeping them as legacy tags.",
                        movie.Title,
                        string.Join(", ", rawSubcategories));
            }

            movie.Subcategories = JsonSerializer.Serialize(subcategories.Take(3));
            movie.Moods = JsonSerializer.Serialize(
                ClassificationVocabulary.Canonicalize(rawMoods, ClassificationVocabulary.CanonicalMood, keepUnknown: false).Take(4));
            movie.Themes = JsonSerializer.Serialize(ClassificationVocabulary.NormalizeThemes(rawThemes));
            movie.NarrativeStyle = ClassificationVocabulary.CanonicalNarrativeStyle(narrativeStyle) ?? movie.NarrativeStyle;
            movie.Accessibility = ClassificationVocabulary.CanonicalAccessibility(accessibility) ?? movie.Accessibility;
            movie.Intensity = ClassificationVocabulary.CanonicalIntensity(intensity) ?? movie.Intensity;
            if (score is >= 1 and <= 10) movie.CriticalAcclaimScore = score;

            // v1.5.27: a movie that came back with no subcategories stays
            // unclassified so the next run re-sends it to the AI.
            movie.IsClassified = subcategories.Count > 0;
            movie.VocabularyVersion = onVocabulary ? ClassificationVocabulary.Version : 0;
            movie.LastUpdated = DateTime.UtcNow;
        }

        private static List<string> GetStringArray(JsonElement element, string propertyName)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Array)
                {
                    return prop.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .ToList();
                }
                else if (prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                    }
                }
            }
            return new List<string>();
        }

        private string? GetStringProperty(JsonElement element, string propertyName)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
                if (prop.ValueKind != JsonValueKind.Null && prop.ValueKind != JsonValueKind.Undefined) return prop.ToString();
            }
            return null;
        }

        private int GetIntProperty(JsonElement element, string propertyName, int defaultValue = 0)
        {
            if (TryGetPropertyIgnoreCase(element, propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int val)) return val;
                if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out int strVal)) return strVal;
            }
            return defaultValue;
        }
    }
}
