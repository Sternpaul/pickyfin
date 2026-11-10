using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pickyfin.Server.Data;
using Pickyfin.Server.Data.Models;
using Microsoft.Extensions.Logging;

namespace Pickyfin.Server.Library
{
    // v1.5.14: pulls TMDB keywords for each movie to sharpen similarity/taste matching.
    // Keywords are objective, curated tags (e.g. "serial killer", "neo-noir") — a far
    // more reliable overlap signal than the LLM's subjective "themes".
    //
    // Fetched at refresh time (not classification time) so existing libraries benefit
    // immediately without re-running Classify Library. Resolves the TMDB id from the
    // IMDB id (already stored) when ProviderIds didn't carry a TMDB id. Results are
    // cached in a JSON file so we don't re-hit TMDB on every refresh.
    public class TmdbKeywordService
    {
        private readonly HttpClient _httpClient;
        private readonly MovieStore _movieStore;
        private readonly ILogger<TmdbKeywordService> _logger;
        private readonly string _cacheDir;

        private const string FindUrl = "https://api.themoviedb.org/3/find/{0}?api_key={1}&external_source=imdb_id";
        private const string KeywordsUrl = "https://api.themoviedb.org/3/movie/{0}/keywords?api_key={1}";
        private readonly SemaphoreSlim _cacheLock = new(1, 1);

        public TmdbKeywordService(
            HttpClient httpClient,
            MovieStore movieStore,
            ILogger<TmdbKeywordService> logger,
            string cacheDir)
        {
            _httpClient = httpClient;
            _movieStore = movieStore;
            _logger = logger;
            _cacheDir = cacheDir;
        }

        public async Task EnrichKeywordsAsync(string apiKey, List<MovieMetadata> movies, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("TMDB keyword enrichment skipped: no TMDB API key configured.");
                return;
            }
            if (movies == null || movies.Count == 0) return;

            var cache = await LoadCacheAsync();
            var changed = new List<MovieMetadata>();
            var saved = 0;
            _logger.LogInformation("TMDB keywords: fetching for {Count} movies", movies.Count);

            foreach (var movie in movies)
            {
                // Per-movie budget so a single slow TMDB call can't stall the whole loop.
                using var perMovieCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                perMovieCts.CancelAfter(TimeSpan.FromSeconds(10));
                var ct = perMovieCts.Token;

                int? tmdbId = movie.TmdbId;
                if (tmdbId == null && !string.IsNullOrWhiteSpace(movie.ImdbId))
                {
                    tmdbId = await ResolveTmdbIdFromImdbAsync(apiKey, movie.ImdbId, ct);
                    if (tmdbId != null) movie.TmdbId = tmdbId;
                }
                if (tmdbId == null) continue;

                // Load cached keywords/popularity for this tmdb id (if any).
                cache.TryGetValue(tmdbId.Value, out var entry);
                entry ??= new TmdbCacheEntry();

                // Refresh keywords only when missing (popularity refresh is separate below).
                if (string.IsNullOrWhiteSpace(movie.Keywords) || movie.Keywords == "[]")
                {
                    var keywords = entry.Keywords
                        ?? await FetchKeywordsAsync(apiKey, tmdbId.Value, ct);
                    entry.Keywords = keywords ?? new List<string>();
                    movie.Keywords = JsonSerializer.Serialize(entry.Keywords);
                    movie.LastUpdated = DateTime.UtcNow;
                    changed.Add(movie);
                }

                // Backfill popularity whenever the movie (or cache) lacks it, so the
                // Hidden Gems fame penalty works even for films enriched before v1.5.21.
                if (movie.Popularity <= 0)
                {
                    if (entry.Popularity > 0)
                    {
                        movie.Popularity = entry.Popularity;
                        changed.Add(movie);
                    }
                    else
                    {
                        var popularity = await FetchPopularityAsync(apiKey, movie.ImdbId, tmdbId.Value, ct);
                        if (popularity.HasValue && popularity.Value > 0)
                        {
                            movie.Popularity = popularity.Value;
                            entry.Popularity = popularity.Value;
                            changed.Add(movie);
                        }
                    }
                }

                // Persist any cache updates.
                cache[tmdbId.Value] = entry;
                await SaveCacheAsync(cache);

                // Save as we go, so a restart doesn't lose the work
                if (changed.Count >= 100)
                {
                    await _movieStore.SaveMoviesAsync(changed.Distinct().ToList(), CancellationToken.None);
                    saved += changed.Distinct().Count();
                    changed.Clear();
                    _logger.LogInformation("TMDB keywords: {Saved} movies so far", saved);
                }

                await Task.Delay(50, CancellationToken.None); // be polite to TMDB
            }

            if (changed.Any())
                await _movieStore.SaveMoviesAsync(changed.Distinct().ToList(), CancellationToken.None);
            saved += changed.Distinct().Count();

            _logger.LogInformation("TMDB keywords: done, {Count} movies updated", saved);
        }

        private async Task<double?> FetchPopularityAsync(string apiKey, string? imdbId, int tmdbId, CancellationToken cancellationToken)
        {
            // Prefer the /find response (already fetched implicitly via id resolution):
            // re-use ResolveTmdbIdFromImdb which returns the hit's popularity. If we only
            // have the tmdb id, fall back to the movie detail endpoint's popularity.
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                try
                {
                    var url = string.Format(FindUrl, imdbId, apiKey);
                    var resp = await _httpClient.GetFromJsonAsync<TmdbFindResponse>(url, cancellationToken);
                    var hit = resp?.movie_results?.FirstOrDefault();
                    if (hit != null && hit.popularity > 0) return hit.popularity;
                }
                catch (Exception)
                {
                    // Keyword/popularity enrichment is best-effort; don't spew stack
                    // traces into the log for a transient TMDB hiccup.
                    _logger.LogDebug("TMDB find (imdb={Imdb}) popularity lookup failed.", imdbId);
                }
            }
            return null;
        }

        private async Task<int?> ResolveTmdbIdFromImdbAsync(string apiKey, string imdbId, CancellationToken cancellationToken)
        {
            try
            {
                var url = string.Format(FindUrl, imdbId, apiKey);
                var resp = await _httpClient.GetFromJsonAsync<TmdbFindResponse>(url, cancellationToken);
                var hit = resp?.movie_results?.FirstOrDefault();
                if (hit != null && hit.id > 0) return hit.id;
            }
            catch (Exception)
            {
                _logger.LogDebug("TMDB find (imdb={Imdb}) failed; skipping keyword resolve.", imdbId);
            }
            return null;
        }

        private async Task<List<string>?> FetchKeywordsAsync(string apiKey, int tmdbId, CancellationToken cancellationToken)
        {
            try
            {
                var url = string.Format(KeywordsUrl, tmdbId, apiKey);
                var resp = await _httpClient.GetFromJsonAsync<TmdbKeywordResponse>(url, cancellationToken);
                if (resp?.keywords == null) return null;
                return resp.keywords
                    .Select(k => k.name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!.Trim())
                    .ToList();
            }
            catch (Exception)
            {
                _logger.LogDebug("TMDB keyword fetch for movie {TmdbId} failed; skipping.", tmdbId);
                return null;
            }
        }

        private string CachePath => System.IO.Path.Combine(_cacheDir, "tmdb_keywords_cache.json");

        private async Task<Dictionary<int, TmdbCacheEntry>> LoadCacheAsync()
        {
            await _cacheLock.WaitAsync();
            try
            {
                if (System.IO.File.Exists(CachePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(CachePath);
                    if (!string.IsNullOrWhiteSpace(json))
                        return JsonSerializer.Deserialize<Dictionary<int, TmdbCacheEntry>>(json) ?? new();
                }
            }
            catch { /* corrupt cache — start fresh */ }
            finally { _cacheLock.Release(); }
            return new Dictionary<int, TmdbCacheEntry>();
        }

        private async Task SaveCacheAsync(Dictionary<int, TmdbCacheEntry> cache)
        {
            await _cacheLock.WaitAsync();
            try { await System.IO.File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(cache)); }
            catch { /* best-effort cache write */ }
            finally { _cacheLock.Release(); }
        }

        private class TmdbFindResponse
        {
            public List<TmdbMovieHit>? movie_results { get; set; }
        }
        private class TmdbMovieHit
        {
            public int id { get; set; }
            public double popularity { get; set; }
        }
        // v1.5.21: cache entry carries both keywords and the popularity (fame) score so we
        // persist the signal across refreshes without re-hitting TMDB every time.
        private class TmdbCacheEntry
        {
            public List<string>? Keywords { get; set; }
            public double Popularity { get; set; } = 0;
        }
        private class TmdbKeywordResponse
        {
            public int id { get; set; }
            public List<TmdbKeyword>? keywords { get; set; }
        }
        private class TmdbKeyword
        {
            public int id { get; set; }
            public string? name { get; set; }
        }
    }
}
