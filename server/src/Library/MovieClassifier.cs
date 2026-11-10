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
        private readonly WebLookup _web;

        public const int BatchSize = 20;

        public MovieClassifier(
            MovieStore movieStore,
            AIProviderFactory aiProviderFactory,
            ILogger<MovieClassifier> logger,
            WebLookup web)
        {
            _web = web;
            _movieStore = movieStore;
            _aiProviderFactory = aiProviderFactory;
            _logger = logger;
        }

        /// <summary>Tags the given movies right away (from the movie browser). True if all got tags.</summary>
        public async Task<bool> TagNowAsync(List<MovieMetadata> movies, CancellationToken cancellationToken = default)
        {
            var provider = _aiProviderFactory.GetProvider();
            using (Activity.Begin($"Tagging {movies.Count} movie(s): waiting for {provider.Name}"))
                await TagBatchAsync(provider, movies, cancellationToken);
            return movies.All(m => m.IsClassified);
        }

        /// <summary>
        /// Looks the movie up on the web and tags it again with what was found. The admin
        /// asked for it, so a hand edit is handed back to the AI.
        /// </summary>
        public async Task<MovieMetadata> LookUpAndTagAsync(MovieMetadata movie, CancellationToken cancellationToken)
        {
            using (Activity.Begin($"Looking up {movie.Title} on the web"))
                movie.WebNotes = await _web.FindAsync(movie, cancellationToken) ?? "";
            movie.ManuallyEdited = false;
            var provider = _aiProviderFactory.GetProvider();
            using (Activity.Begin($"Tagging {movie.Title}: waiting for {provider.Name}"))
                await TagBatchAsync(provider, new List<MovieMetadata> { movie }, cancellationToken);
            return movie;
        }

        public async Task ClassifyPendingMoviesAsync(CancellationToken cancellationToken = default)
        {
            // Re-tagging movies classified under an older vocabulary costs one AI call
            // per batch, so it only happens when the admin opts in.
            var includeOutdated = ServerSettings.Current.ReclassifyOutdatedMovies;
            var unclassified = await _movieStore.GetMoviesNeedingClassificationAsync(includeOutdated, cancellationToken);
            if (!unclassified.Any())
            {
                _logger.LogInformation("All movies are tagged");
                return;
            }

            _logger.LogInformation("Tagging {Count} movies, {Batch} per AI call", unclassified.Count, BatchSize);
            var provider = _aiProviderFactory.GetProvider();
            TaggingEta.Start(unclassified.Count, BatchSize);
            try
            {
                for (int i = 0; i < unclassified.Count; i += BatchSize)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var batch = unclassified.Skip(i).Take(BatchSize).ToList();
                    _logger.LogInformation("Tagging movies {Start}–{End} of {Total} ({Model})", i + 1, i + batch.Count, unclassified.Count, ServerSettings.Current.ClassificationModel);
                    try
                    {
                        var started = DateTime.UtcNow;
                        TaggingEta.BatchStarted(unclassified.Count - i);
                        using (Activity.Begin($"Tagging movies {i + 1}–{i + batch.Count} of {unclassified.Count}: waiting for {provider.Name}"))
                            await TagBatchAsync(provider, batch, cancellationToken);
                        _logger.LogInformation("{Provider} answered in {Seconds}s", provider.Name, (int)(DateTime.UtcNow - started).TotalSeconds);
                        await LookUpFlaggedAsync(provider, batch, cancellationToken);
                        TaggingEta.BatchDone();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError("Tagging stopped: {Error}. It continues at the next library sync.", ex.Message);
                        break;
                    }
                }
            }
            finally { TaggingEta.Stop(); }
            _logger.LogInformation("Tagging finished");
        }

        /// <summary>
        /// Films the AI didn't know or that look off get one web lookup and are tagged again
        /// with what was found (when an Exa key is set and the budget allows).
        /// </summary>
        public async Task LookUpFlaggedAsync(IAIProvider provider, List<MovieMetadata> movies, CancellationToken cancellationToken)
        {
            if (!ServerSettings.Current.WebLookupAuto) return;
            var flagged = movies.Where(m => m.Issue is not null && m.WebNotes is null && !m.ManuallyEdited && !m.Verified).ToList();
            await LookUpAndTagManyAsync(provider, flagged, cancellationToken);
        }

        /// <summary>Looks each movie up, then tags the ones with findings again in one AI call. Returns how many were looked up.</summary>
        public async Task<int> LookUpAndTagManyAsync(IAIProvider provider, List<MovieMetadata> movies, CancellationToken cancellationToken)
        {
            var withNotes = new List<MovieMetadata>();
            var looked = 0;
            foreach (var movie in movies)
            {
                if (_web.Blocked() is { } why) { _logger.LogInformation("Web lookups paused: {Why}", why); break; }
                try
                {
                    using (Activity.Begin($"Looking up {movie.Title} on the web"))
                        movie.WebNotes = await _web.FindAsync(movie, cancellationToken) ?? "";
                    looked++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Web lookup failed for {Title}: {Error}", movie.Title, ex.Message);
                    break;
                }
                if (movie.WebNotes.Length > 0) withNotes.Add(movie);
                else await _movieStore.SaveMoviesAsync(new[] { movie }, cancellationToken);
            }
            if (withNotes.Count == 0) return looked;
            _logger.LogInformation("Tagging {Count} looked-up movie(s) again", withNotes.Count);
            using (Activity.Begin($"Tagging {withNotes.Count} looked-up movie(s) again: waiting for {provider.Name}"))
                await TagBatchAsync(provider, withNotes, cancellationToken);
            return looked;
        }

        /// <summary>One AI call for a batch: apply the tags, note what looks off, save.</summary>
        private async Task TagBatchAsync(IAIProvider provider, List<MovieMetadata> batch, CancellationToken cancellationToken)
        {
            var json = await provider.ClassifyMoviesAsync(batch, cancellationToken);
            var known = ProcessClassificationResult(json, batch);
            foreach (var movie in batch.Where(known.ContainsKey))
            {
                movie.Checked = false;
                movie.Verified = false;
                movie.Issue = Issues.Describe(movie, known[movie]);
            }
            var missing = batch.Count(m => !known.ContainsKey(m));
            if (missing > 0) _logger.LogWarning("The AI left out {Count} of {Total} movie(s); they're tried again next time", missing, batch.Count);
            await _movieStore.SaveMoviesAsync(batch.Where(known.ContainsKey), cancellationToken);
        }

        /// <summary>
        /// Applies the AI's answer. Each answer must name its movie by Ref (or id, or exact
        /// title); answers that can't be matched are dropped rather than guessed, so tags
        /// never land on the wrong movie. Returns the movies that got an answer and how well
        /// the AI said it knew them.
        /// </summary>
        internal Dictionary<MovieMetadata, string> ProcessClassificationResult(string? jsonResponse, List<MovieMetadata> batch)
        {
            var answered = new Dictionary<MovieMetadata, string>();
            if (string.IsNullOrEmpty(jsonResponse))
            {
                _logger.LogWarning("The AI returned an empty answer");
                return answered;
            }
            JsonElement moviesArray;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(ExtractJson(jsonResponse));
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("The AI's answer wasn't valid JSON ({Error}); the batch is tried again next time", ex.Message);
                return answered;
            }
            using var _ = document;
            if (document.RootElement.ValueKind == JsonValueKind.Array) moviesArray = document.RootElement;
            else if (TryGetPropertyIgnoreCase(document.RootElement, "movies", out var prop) && prop.ValueKind == JsonValueKind.Array) moviesArray = prop;
            else
            {
                _logger.LogWarning("Could not find the movies list in the AI's answer");
                return answered;
            }

            foreach (var element in moviesArray.EnumerateArray())
            {
                var movie = Match(element, batch);
                if (movie is null || answered.ContainsKey(movie)) continue;
                ApplyClassification(
                    movie,
                    GetStringArray(element, "Subcategories"),
                    GetStringArray(element, "Moods"),
                    GetStringArray(element, "Themes"),
                    GetStringProperty(element, "NarrativeStyle"),
                    GetStringProperty(element, "Accessibility"),
                    GetStringProperty(element, "Intensity"),
                    GetIntProperty(element, "CriticalAcclaimScore"));
                var knownRaw = (GetStringProperty(element, "Known") ?? "yes").Trim().ToLowerInvariant();
                answered[movie] = knownRaw is "no" or "partly" ? knownRaw : "yes";
            }
            return answered;
        }

        internal static string ExtractJson(string text)
        {
            var trimmed = text.Trim();
            // Sometimes the LLM wraps it in ```json ... ```
            var fence = System.Text.RegularExpressions.Regex.Match(trimmed, @"```(?:json)?\s*([\s\S]*?)\s*```");
            if (fence.Success) return fence.Groups[1].Value.Trim();
            var start = trimmed.IndexOfAny(new[] { '[', '{' });
            var end = trimmed.LastIndexOfAny(new[] { ']', '}' });
            return start >= 0 && end > start ? trimmed.Substring(start, end - start + 1) : trimmed;
        }

        internal static MovieMetadata? Match(JsonElement element, List<MovieMetadata> batch)
        {
            if (TryGetPropertyIgnoreCase(element, "Ref", out var refProp))
            {
                var r = (refProp.ValueKind == JsonValueKind.String ? refProp.GetString() : refProp.ToString())?.Trim().ToLowerInvariant().TrimStart('m');
                if (int.TryParse(r, out var n) && n >= 1 && n <= batch.Count) return batch[n - 1];
            }
            if (TryGetPropertyIgnoreCase(element, "ItemId", out var idProp) || TryGetPropertyIgnoreCase(element, "id", out idProp))
                if (Guid.TryParse(idProp.ToString(), out var itemId) && batch.FirstOrDefault(m => m.ItemId == itemId) is { } byId) return byId;
            if (TryGetPropertyIgnoreCase(element, "Title", out var titleProp) && titleProp.ValueKind == JsonValueKind.String)
            {
                var title = BrowserApi.Fold(titleProp.GetString());
                var same = batch.Where(m => BrowserApi.Fold(m.Title) == title).ToList();
                if (same.Count == 1) return same[0];
            }
            return null;
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
