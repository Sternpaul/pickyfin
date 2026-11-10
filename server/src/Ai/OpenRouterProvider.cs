using System;
using System.Collections.Generic;
using System.Linq;
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
        // Free models come and go; any no longer listed by OpenRouter are skipped (see OpenRouterModels).
        private static readonly string[] FreeFallbackModels =
        {
            "google/gemma-4-31b-it:free",
            "nvidia/nemotron-3.5-super-vl:free",
            "nvidia/nemotron-3-ultra-550b-a55b:free",
            "google/gemma-4-26b-a4b-it:free",
            "dots-studio/dots-3-note-preview:free",
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

        /// <summary>Any JSON-answering prompt on the given model (the second opinion uses this).</summary>
        public async Task<string> CompleteJsonAsync(string prompt, string model, CancellationToken cancellationToken = default) =>
            await CallApiWithFallbackAsync(BuildFallbackChain(model), prompt, true, cancellationToken);

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

        // Tries each model in the chain in order.
        // - 429 (busy): two quick tries, then the next model. If every model was only busy,
        //   wait a minute and go round again (three rounds at most).
        // - 404 (model gone): skipped, and left out for the next six hours.
        // - 400 (request rejected): tried once more without JSON mode (some models don't support
        //   it); if it's still rejected, stop. It's our request, another model won't fix that.
        // - 401/403: the key is wrong, stop.
        private async Task<string> CallApiWithFallbackAsync(List<string> models, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Config.ApiKey))
                throw new InvalidOperationException("OpenRouter API Key is missing.");

            var live = string.IsNullOrWhiteSpace(Config.CustomEndpoint) ? await OpenRouterModels.GetAsync(_httpClient, _logger, cancellationToken) : null;
            if (live is not null)
            {
                foreach (var gone in models.Where(m => !live.ContainsKey(m) && m.Equals(models[0], StringComparison.OrdinalIgnoreCase)))
                    _logger.LogWarning("OpenRouter: '{Model}' isn't offered anymore; pick another model on the AI page. Using fallbacks.", gone);
                models = models.Where(m => live.ContainsKey(m)).ToList();
            }
            models = models.Where(m => !OpenRouterModels.IsGone(m)).ToList();
            if (models.Count == 0)
                throw new InvalidOperationException("None of the models is offered by OpenRouter right now; pick another model on the AI page.");

            var lastError = "No models available.";
            for (var round = 1; round <= 3; round++)
            {
                var onlyBusy = true;
                for (var i = 0; i < models.Count; i++)
                {
                    var model = models[i];
                    var json = forceJson && (live is null || !live.TryGetValue(model, out var supportsJson) || supportsJson);
                    var result = await TryCallModelAsync(model, prompt, json, cancellationToken);
                    if (result.Success)
                    {
                        if (i > 0 || round > 1)
                            _logger.LogWarning("OpenRouter: answered by fallback model '{Model}'.", model);
                        return result.Content;
                    }
                    if (result.AbortChain)
                        throw new InvalidOperationException($"OpenRouter stopped: {result.Error}");
                    lastError = $"{model}: {result.Error}";
                    if (result.Gone) OpenRouterModels.MarkGone(model);
                    else if (!result.Busy) onlyBusy = false;
                    _logger.LogWarning("OpenRouter: model '{Model}' failed ({Error}); trying the next one.", model, result.Error);
                }
                if (!onlyBusy || round == 3) break;
                _logger.LogWarning("OpenRouter: every free model is busy right now; trying again in a minute (round {Round} of 3).", round + 1);
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            }
            throw new InvalidOperationException($"OpenRouter request failed on all models. Last error: {lastError}");
        }

        private async Task<OpenRouterCallResult> TryCallModelAsync(string model, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            var url = string.IsNullOrWhiteSpace(Config.CustomEndpoint)
                ? "https://openrouter.ai/api/v1/chat/completions"
                : Config.CustomEndpoint;

            const int maxAttempts = 3;
            var busyTries = 0;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
                request.Headers.Add("HTTP-Referer", "https://github.com/Sternpaul/pickyfin");
                request.Headers.Add("X-Title", "Pickyfin");

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
                    var what = ex is TaskCanceledException ? $"no answer within {(int)_httpClient.Timeout.TotalMinutes} minutes" : ex.Message;
                    _logger.LogWarning("OpenRouter: {Model} attempt {Attempt}/{Max} failed: {Error}", model, attempt, maxAttempts, what);
                    if (attempt == maxAttempts || ex is TaskCanceledException) return new OpenRouterCallResult { Error = what };
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
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
                    catch (Exception ex) when (ex is KeyNotFoundException or JsonException or InvalidOperationException)
                    {
                        attemptResponse.Dispose();
                        return new OpenRouterCallResult { Error = $"unexpected response format: {ex.Message}" };
                    }
                }

                var errorBody = await attemptResponse.Content.ReadAsStringAsync(cancellationToken);
                var status = (int)attemptResponse.StatusCode;
                if (errorBody.Length > 300) errorBody = errorBody[..300] + "…";

                if (status == 401 || status == 403)
                {
                    attemptResponse.Dispose();
                    return new OpenRouterCallResult { AbortChain = true, Error = $"the API key was refused (HTTP {status}): {errorBody}" };
                }

                if (status == 429)
                {
                    var delay = Math.Min(30, GetRetryDelay(attemptResponse, attempt, maxAttempts));
                    attemptResponse.Dispose();
                    if (++busyTries >= 2) return new OpenRouterCallResult { Busy = true, Error = "busy (429)" };
                    _logger.LogInformation("OpenRouter model '{Model}' is busy (429); trying again in {Delay}s.", model, delay);
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                    attempt--; // busy waits don't use up the network retries
                    continue;
                }

                attemptResponse.Dispose();
                if (status == 404)
                    return new OpenRouterCallResult { Gone = true, Error = $"not offered anymore (HTTP 404): {errorBody}" };

                if (status == 400)
                {
                    if (forceJson)
                    {
                        _logger.LogInformation("OpenRouter: '{Model}' rejected the request (400); trying once more without JSON mode.", model);
                        forceJson = false;
                        attempt--;
                        continue;
                    }
                    return new OpenRouterCallResult { AbortChain = true, Error = $"'{model}' rejected the request (HTTP 400): {errorBody}" };
                }

                // 5xx and anything else: the model's provider is having trouble, try the next model
                return new OpenRouterCallResult { Error = $"HTTP {status}: {errorBody}" };
            }

            return new OpenRouterCallResult { Error = "exhausted retries" };
        }

        private class OpenRouterCallResult
        {
            public bool Success;
            public string Content = string.Empty;
            public bool AbortChain;
            public bool Busy;
            public bool Gone;
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
