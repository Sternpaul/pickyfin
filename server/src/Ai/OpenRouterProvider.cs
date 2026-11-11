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

        public async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await CallApiWithFallbackAsync(() => OpenRouterModels.DescribeOrder(Config), "Ping. Reply with 'Pong' in JSON.", true, cancellationToken);
                return !string.IsNullOrWhiteSpace(response);
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> ClassifyMoviesAsync(List<MovieMetadata> movies, CancellationToken cancellationToken = default) =>
            await CallApiWithFallbackAsync(() => OpenRouterModels.DescribeOrder(Config), ClassificationPrompt.Build(movies), true, cancellationToken);

        /// <summary>Any JSON-answering prompt. The second opinion's model gets the second opinion's order.</summary>
        public async Task<string> CompleteJsonAsync(string prompt, string model, CancellationToken cancellationToken = default) =>
            await CallApiWithFallbackAsync(
                () => model.Equals(Config.CheckModel, StringComparison.OrdinalIgnoreCase) ? OpenRouterModels.CheckOrder(Config) : new List<string> { model },
                prompt, true, cancellationToken);

        public async Task<string> ChatAsync(string userQuery, string systemPrompt, CancellationToken cancellationToken = default)
        {
            var prompt = $"{systemPrompt}\n\nUser: {userQuery}";
            // Picking: the chosen model first, then the describing order as fallbacks
            return await CallApiWithFallbackAsync(() => new[] { Config.ChatModel }.Concat(OpenRouterModels.DescribeOrder(Config))
                .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), prompt, false, cancellationToken);
        }


        public const int TriesPerModel = 3;

        // Goes down the model order set on the AI page. Each model gets three tries, then the next one:
        // - 429 (busy), 5xx, network trouble, an unreadable answer: wait a little, try again
        // - 400 (request rejected): the next try goes without JSON mode (some models don't support it)
        // - no answer within the time limit: next model right away (waiting again rarely helps)
        // - 404 (model gone): next model right away, and it's left out for six hours
        // - 401/403: the key is wrong; stop
        private async Task<string> CallApiWithFallbackAsync(Func<List<string>> order, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Config.ApiKey))
                throw new InvalidOperationException("OpenRouter API Key is missing.");

            var live = string.IsNullOrWhiteSpace(Config.CustomEndpoint) ? await OpenRouterModels.GetAsync(_httpClient, _logger, cancellationToken) : null;
            var models = order(); // after the live list, so an automatic order is current
            var usable = models.Where(m => (live is null || live.ContainsKey(m)) && !OpenRouterModels.IsGone(m)).ToList();
            foreach (var skipped in models.Except(usable))
                _logger.LogWarning("OpenRouter: '{Model}' isn't offered right now; skipped. Take it out of the model order on the AI page.", skipped);
            if (usable.Count == 0)
                throw new InvalidOperationException("None of the models in your order is offered by OpenRouter right now; change the order on the AI page.");

            var lastError = "No models available.";
            try
            {
                for (var i = 0; i < usable.Count; i++)
                {
                    var model = usable[i];
                    var json = forceJson && (live is null || !live.TryGetValue(model, out var supportsJson) || supportsJson);
                    var result = await TryCallModelAsync(model, prompt, json, $"{i + 1} of {usable.Count}", cancellationToken);
                    if (result.Success)
                    {
                        OpenRouterModels.Record(model, null);
                        if (i > 0) _logger.LogWarning("OpenRouter: answered by '{Model}' (number {Index} in your order).", model, i + 1);
                        return result.Content;
                    }
                    OpenRouterModels.Record(model, result.Error);
                    if (result.AbortChain)
                        throw new InvalidOperationException($"OpenRouter stopped: {result.Error}");
                    lastError = $"{model}: {result.Error}";
                    if (result.Gone) OpenRouterModels.MarkGone(model);
                    _logger.LogWarning("OpenRouter: '{Model}' failed ({Error}){Next}", model, result.Error,
                        i + 1 < usable.Count ? $"; trying '{usable[i + 1]}' next." : "; no more models in your order.");
                }
            }
            finally { Activity.Detail = null; }
            throw new InvalidOperationException($"OpenRouter: every model in your order failed. Last error: {lastError}");
        }

        private async Task<OpenRouterCallResult> TryCallModelAsync(string model, string prompt, bool forceJson, string position, CancellationToken cancellationToken)
        {
            var url = string.IsNullOrWhiteSpace(Config.CustomEndpoint)
                ? "https://openrouter.ai/api/v1/chat/completions"
                : Config.CustomEndpoint;

            var error = "no answer";
            for (var attempt = 1; attempt <= TriesPerModel; attempt++)
            {
                Activity.Detail = $"model {position}: {model}, try {attempt} of {TriesPerModel}";
                if (attempt > 1)
                    _logger.LogInformation("OpenRouter: '{Model}' try {Attempt} of {Max} ({Error})", model, attempt, TriesPerModel, error);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
                request.Headers.Add("HTTP-Referer", "https://github.com/Sternpaul/pickyfin");
                request.Headers.Add("X-Title", "Pickyfin");
                var requestBody = new
                {
                    model,
                    messages = new[] { new { role = "user", content = prompt } },
                    response_format = forceJson ? new { type = "json_object" } : null
                };
                request.Content = new StringContent(
                    JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                    Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, cancellationToken);
                }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
                {
                    if (ex is TaskCanceledException)
                        return new OpenRouterCallResult { Error = $"no answer within {(int)_httpClient.Timeout.TotalMinutes} minutes" };
                    error = $"network: {ex.Message}";
                    if (attempt < TriesPerModel) await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
                    continue;
                }

                using (response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                            var content = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                            if (content.Trim().Length > 0) return new OpenRouterCallResult { Success = true, Content = content };
                            error = "empty answer";
                        }
                        catch (Exception ex) when (ex is KeyNotFoundException or JsonException or InvalidOperationException or IndexOutOfRangeException)
                        {
                            error = $"unreadable answer: {ex.Message}";
                        }
                        if (attempt < TriesPerModel) await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }

                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    var status = (int)response.StatusCode;
                    if (errorBody.Length > 300) errorBody = errorBody[..300] + "…";
                    error = $"HTTP {status}: {errorBody}";

                    if (status is 401 or 403)
                        return new OpenRouterCallResult { AbortChain = true, Error = $"the API key was refused (HTTP {status}): {errorBody}" };
                    if (status == 404)
                        return new OpenRouterCallResult { Gone = true, Error = $"not offered anymore (HTTP 404)" };
                    if (status == 400)
                    {
                        error = $"rejected the request (HTTP 400): {errorBody}";
                        forceJson = false; // the usual cause; the prompt asks for JSON anyway
                        if (attempt < TriesPerModel) await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }
                    if (status == 429)
                    {
                        error = "busy (429)";
                        if (attempt < TriesPerModel) await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, GetRetryDelay(response, attempt, TriesPerModel))), cancellationToken);
                        continue;
                    }
                    // 5xx and anything else: the model's provider is having trouble
                    if (attempt < TriesPerModel) await Task.Delay(TimeSpan.FromSeconds(10 * attempt), cancellationToken);
                }
            }
            return new OpenRouterCallResult { Error = $"{error} ({TriesPerModel} tries)" };
        }

        private class OpenRouterCallResult
        {
            public bool Success;
            public string Content = string.Empty;
            public bool AbortChain;
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
