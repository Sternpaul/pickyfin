using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pickyfin.Server.Data.Models;
using Microsoft.Extensions.Logging;

namespace Pickyfin.Server.Ai
{
    public class OpenRouterProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OpenRouterProvider> _logger;

        public string Name => "OpenRouter";

        public OpenRouterProvider(HttpClient httpClient, ILogger<OpenRouterProvider> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        private static ServerSettings Config => ServerSettings.Current;

        // Curated free-model fallback chain. OpenRouter's pooled free tier throttles
        // models independently and unpredictably (we watched Poolside + Gemma 4 both
        // 429 in one session while gpt-oss-20b stayed open). When the user's chosen
        // model is rate-limited, we transparently try the next one so a long
        // library-classification run isn't killed by a transient 429.
        private static readonly string[] FreeFallbackModels =
        {
            "nvidia/nemotron-3-super-120b-a12b:free",
            "openai/gpt-oss-20b:free",
            "google/gemma-4-31b-it:free",
            "poolside/laguna-s-2.1:free"
        };

        public async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var chain = BuildFallbackChain(Config.ClassificationModel);
                var response = await CallApiWithFallbackAsync(chain, "Ping. Reply with 'Pong' in JSON.", true, cancellationToken);
                return !string.IsNullOrWhiteSpace(response);
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> ClassifyMoviesAsync(List<MovieMetadata> movies, CancellationToken cancellationToken = default)
        {
            var prompt = ClassificationPrompt.Build(movies);
            var chain = BuildFallbackChain(Config.ClassificationModel);
            return await CallApiWithFallbackAsync(chain, prompt, true, cancellationToken);
        }

        public async Task<string> ChatAsync(string userQuery, string systemPrompt, CancellationToken cancellationToken = default)
        {
            var prompt = $"{systemPrompt}\n\nUser: {userQuery}";
            var chain = BuildFallbackChain(Config.ChatModel);
            return await CallApiWithFallbackAsync(chain, prompt, false, cancellationToken);
        }

        private static List<string> BuildFallbackChain(string primary)
        {
            var chain = new List<string>();
            if (!string.IsNullOrWhiteSpace(primary) && !chain.Contains(primary, StringComparer.OrdinalIgnoreCase))
                chain.Add(primary);
            foreach (var m in FreeFallbackModels)
                if (!chain.Contains(m, StringComparer.OrdinalIgnoreCase))
                    chain.Add(m);
            return chain;
        }

        // Tries each model in the chain in order. A model that is rate-limited (429)
        // or otherwise fails is skipped in favour of the next one. Auth errors (401/403)
        // apply to every model and abort the whole chain immediately.
        private async Task<string> CallApiWithFallbackAsync(List<string> models, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Config.ApiKey))
                throw new InvalidOperationException("OpenRouter API Key is missing.");

            var lastError = "No models available.";
            for (int i = 0; i < models.Count; i++)
            {
                var model = models[i];
                var result = await TryCallModelAsync(model, prompt, forceJson, cancellationToken);
                if (result.Success)
                {
                    if (i > 0)
                        _logger.LogWarning("OpenRouter: fell back to model '{Model}' after earlier failure(s).", model);
                    return result.Content;
                }
                if (result.AbortChain)
                    throw new InvalidOperationException($"OpenRouter request failed (auth/permission): {result.Error}");
                lastError = result.Error;
                _logger.LogWarning("OpenRouter: model '{Model}' failed ({Error}); trying next fallback.", model, result.Error);
            }
            throw new InvalidOperationException($"OpenRouter request failed on all models. Last error: {lastError}");
        }

        private async Task<OpenRouterCallResult> TryCallModelAsync(string model, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            var url = string.IsNullOrWhiteSpace(Config.CustomEndpoint)
                ? "https://openrouter.ai/api/v1/chat/completions"
                : Config.CustomEndpoint;

            const int maxAttempts = 5;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
                request.Headers.Add("HTTP-Referer", "https://github.com/jellyfin/jellyfin");
                request.Headers.Add("X-Title", "Jellyfin AI Recommender");

                var requestBody = new
                {
                    model = model,
                    messages = new[]
                    {
                        new { role = "user", content = prompt }
                    },
                    response_format = forceJson ? new { type = "json_object" } : null
                };

                var jsonBody = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                HttpResponseMessage attemptResponse;
                try
                {
                    attemptResponse = await _httpClient.SendAsync(request, cancellationToken);
                }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("OpenRouter: {Model} attempt {Attempt}/{Max} failed: {Error}", model, attempt, maxAttempts,
                        ex is TaskCanceledException ? "no answer within 10 minutes" : ex.Message);
                    if (attempt == maxAttempts) return new OpenRouterCallResult { Success = false, Error = $"network: {ex.Message}" };
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt) * 2)), cancellationToken);
                    continue;
                }

                if (attemptResponse.IsSuccessStatusCode)
                {
                    try
                    {
                        var jsonResponse = await attemptResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                        var content = jsonResponse.GetProperty("choices")[0]
                            .GetProperty("message")
                            .GetProperty("content")
                            .GetString() ?? string.Empty;
                        attemptResponse.Dispose();
                        return new OpenRouterCallResult { Success = true, Content = content };
                    }
                    catch (KeyNotFoundException ex)
                    {
                        attemptResponse.Dispose();
                        return new OpenRouterCallResult { Success = false, Error = $"unexpected response format: {ex.Message}" };
                    }
                }

                var errorBody = await attemptResponse.Content.ReadAsStringAsync(cancellationToken);
                var status = (int)attemptResponse.StatusCode;
                if (errorBody.Length > 300) errorBody = errorBody[..300] + "…";

                // Auth/permission errors are not model-specific: abort the whole chain.
                if (status == 401 || status == 403)
                {
                    attemptResponse.Dispose();
                    return new OpenRouterCallResult { Success = false, AbortChain = true, Error = $"HTTP {status}: {errorBody}" };
                }

                if (status == 429)
                {
                    var delay = GetRetryDelay(attemptResponse, attempt, maxAttempts);
                    _logger.LogWarning("OpenRouter model '{Model}' rate limited (429). Attempt {Attempt}/{Max}. Waiting {Delay}s.", model, attempt, maxAttempts, delay);
                    attemptResponse.Dispose();
                    if (attempt == maxAttempts) return new OpenRouterCallResult { Success = false, Error = "rate limited (429) after retries" };
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                    continue;
                }

                // Any other error (4xx/5xx): model-specific, skip to the next fallback.
                attemptResponse.Dispose();
                return new OpenRouterCallResult { Success = false, Error = $"HTTP {status}: {errorBody}" };
            }

            return new OpenRouterCallResult { Success = false, Error = "exhausted retries" };
        }

        private class OpenRouterCallResult
        {
            public bool Success;
            public string Content = string.Empty;
            public bool AbortChain;
            public string Error = string.Empty;
        }

        private static int GetRetryDelay(HttpResponseMessage response, int attempt, int maxAttempts)
        {
            // Prefer the Retry-After header (delta seconds, or an http-date).
            if (response.Headers.RetryAfter?.Delta is { } delta)
                return (int)Math.Max(1, Math.Min(delta.TotalSeconds, 60));
            if (response.Headers.RetryAfter?.Date is { } date)
            {
                var secs = (date - DateTimeOffset.UtcNow).TotalSeconds;
                if (secs > 0) return (int)Math.Max(1, Math.Min(secs, 300));
            }
            // Fallback: exponential backoff, 5s..60s.
            return (int)Math.Max(5, Math.Min(60, Math.Pow(2, attempt) * 5));
        }
    }
}
