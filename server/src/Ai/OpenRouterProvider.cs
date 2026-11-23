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
                var response = await CallApiWithFallbackAsync(() => OpenRouterModels.DescribeOrder(Config), "Ping. Reply with 'Pong' in JSON.", true, "test", cancellationToken);
                return !string.IsNullOrWhiteSpace(response);
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> ClassifyMoviesAsync(List<MovieMetadata> movies, CancellationToken cancellationToken = default) =>
            await CallApiWithFallbackAsync(() => OpenRouterModels.DescribeOrder(Config), ClassificationPrompt.Build(movies), true, "describe", cancellationToken);

        public async Task<string> ClassifyLookedUpAsync(List<MovieMetadata> movies, CancellationToken cancellationToken = default) =>
            await CallApiWithFallbackAsync(() => OpenRouterModels.LookupOrder(Config), ClassificationPrompt.Build(movies), true, "lookup", cancellationToken);

        /// <summary>Any JSON-answering prompt. The second opinion's model gets the second opinion's order.</summary>
        public async Task<string> CompleteJsonAsync(string prompt, string model, CancellationToken cancellationToken = default) =>
            await CallApiWithFallbackAsync(
                () => model.Equals(Config.CheckModel, StringComparison.OrdinalIgnoreCase) ? OpenRouterModels.CheckOrder(Config) : new List<string> { model },
                prompt, true, "check", cancellationToken);

        public async Task<string> ChatAsync(string userQuery, string systemPrompt, CancellationToken cancellationToken = default)
        {
            var prompt = $"{systemPrompt}\n\nUser: {userQuery}";
            // Picking: the chosen model first, then the describing order as fallbacks
            return await CallApiWithFallbackAsync(() => new[] { Config.ChatModel }.Concat(OpenRouterModels.DescribeOrder(Config))
                .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), prompt, false, "pick", cancellationToken);
        }


        public const int TriesPerModel = 3;

        // Goes down the model order set on the AI page. Each model gets three tries, then the next one:
        // - 429 (busy), 5xx, network trouble, an unreadable answer: wait a little, try again
        // - 400 (request rejected): the next try goes without JSON mode (some models don't support it)
        // - no answer within the time limit: next model right away (waiting again rarely helps)
        // - 404 (model gone): next model right away, and it's left out for six hours
        // - 401/403: the key is wrong; stop
        private async Task<string> CallApiWithFallbackAsync(Func<List<string>> order, string prompt, bool forceJson, string step, CancellationToken cancellationToken)
        {
            if (!Providers.AnyKey(Config))
                throw new InvalidOperationException("No API key is set: add an OpenRouter or NVIDIA key on the AI page.");

            var live = string.IsNullOrWhiteSpace(Config.CustomEndpoint) ? await OpenRouterModels.GetAsync(_httpClient, _logger, cancellationToken) : null;
            var models = order(); // after the live list, so an automatic order is current
            var nim = models.Any(Nim.Is) ? await Nim.GetAsync(_httpClient, _logger, cancellationToken) : null;
            // Models of a provider with no key are left out quietly: it's the admin's choice which keys to set
            var keyed = models.Where(m => Providers.HasKey(Config, m)).ToList();
            var usable = keyed.Where(m => (Nim.Is(m) ? nim is null || nim.Contains(m, StringComparer.OrdinalIgnoreCase) : !Providers.IsOpenRouter(m) || live is null || live.ContainsKey(m))
                && !OpenRouterModels.IsGone(m) && Providers.RefusedWhy(m) is null).ToList();
            foreach (var skipped in keyed.Except(usable))
                _logger.LogWarning("{Provider}: '{Model}' {Why}; skipped.", Providers.Name(skipped), skipped,
                    Providers.RefusedWhy(skipped) is { } why ? $"waits: {why}" : "isn't offered right now. Take it out of the model order on the AI page");
            if (usable.Count == 0)
                throw new InvalidOperationException(keyed.Count == 0
                    ? "No model in your order has a key: add the missing OpenRouter or NVIDIA key, or change the order on the AI page."
                    : models.Select(Providers.RefusedWhy).FirstOrDefault(w => w is not null) is { } refused ? $"Stopped: {refused}"
                    : "None of the models in your order is offered right now; change the order on the AI page.");

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
                        OpenRouterModels.LastAnswered[step] = (model, DateTime.UtcNow);
                        if (i > 0) _logger.LogWarning(Providers.Name(model) + ": answered by '{Model}' (number {Index} in your order).", model, i + 1);
                        return result.Content;
                    }
                    OpenRouterModels.Record(model, result.Error);
                    OpenRouterModels.StepFailures.AddOrUpdate($"{step}|{model}", 1, (_, n) => n + 1);
                    if (result.AbortChain)
                    {
                        // The key was refused: that provider's models wait an hour, the other provider's carry on
                        Providers.Refuse(model, $"{Providers.Name(model)} refused the key ({result.Error})");
                        var rest = usable.Skip(i + 1).Where(m => Providers.Name(m) != Providers.Name(model)).ToList();
                        if (rest.Count == 0) throw new InvalidOperationException($"{Providers.Name(model)} stopped: {result.Error}");
                        _logger.LogWarning("{Provider}: {Error}; its models wait an hour, '{Next}' takes over.", Providers.Name(model), result.Error, rest[0]);
                        usable = usable.Take(i + 1).Concat(rest).ToList();
                        continue;
                    }
                    lastError = $"{model}: {result.Error}";
                    if (result.Gone) OpenRouterModels.MarkGone(model);
                    _logger.LogWarning(Providers.Name(model) + ": '{Model}' failed ({Error}){Next}", model, result.Error,
                        i + 1 < usable.Count ? $"; trying '{usable[i + 1]}' next." : "; no more models in your order.");
                }
            }
            finally { Activity.Detail = null; }
            throw new InvalidOperationException($"AI: every model in your order failed. Last error: {lastError}");
        }

        private async Task<OpenRouterCallResult> TryCallModelAsync(string model, string prompt, bool forceJson, string position, CancellationToken cancellationToken)
        {
            var url = Providers.ChatUrl(Config, model);

            var error = "no answer";
            for (var attempt = 1; attempt <= TriesPerModel; attempt++)
            {
                Activity.Detail = $"model {position}: {model}, try {attempt} of {TriesPerModel}";
                if (attempt > 1)
                    _logger.LogInformation(Providers.Name(model) + ": '{Model}' try {Attempt} of {Max} ({Error})", model, attempt, TriesPerModel, error);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                Providers.Authorize(request, Config, model);
                var requestBody = new
                {
                    model = Providers.ApiModel(model),
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

                    if (status is 401 or 402 or 403)
                        return new OpenRouterCallResult { AbortChain = true, Error = $"the API key was refused (HTTP {status}): {errorBody}" };
                    if (Nim.Is(model) && Nim.SaysNotServed(status, errorBody)) Nim.MarkNotServed(model);
                    if (status is 404 or 410)
                        return new OpenRouterCallResult { Gone = true, Error = $"not offered anymore (HTTP {status})" };
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
