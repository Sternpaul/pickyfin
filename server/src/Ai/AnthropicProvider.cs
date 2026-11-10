using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pickyfin.Server.Data.Models;
using Microsoft.Extensions.Logging;

namespace Pickyfin.Server.Ai
{
    public class AnthropicProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AnthropicProvider> _logger;

        public string Name => "Anthropic Claude";

        public AnthropicProvider(HttpClient httpClient, ILogger<AnthropicProvider> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        private static ServerSettings Config => ServerSettings.Current;

        public async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await CallApiAsync(Config.ClassificationModel, "Ping. Reply with 'Pong' in JSON.", true, cancellationToken);
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
            return await CallApiAsync(Config.ClassificationModel, prompt, true, cancellationToken);
        }

        /// <summary>Any JSON-answering prompt on the given model (the second opinion uses this).</summary>
        public async Task<string> CompleteJsonAsync(string prompt, string model, CancellationToken cancellationToken = default) =>
            await CallApiAsync(model, prompt, true, cancellationToken);

        public async Task<string> ChatAsync(string userQuery, string systemPrompt, CancellationToken cancellationToken = default)
        {
            var prompt = $"{systemPrompt}\n\nUser: {userQuery}";
            return await CallApiAsync(Config.ChatModel, prompt, false, cancellationToken);
        }

        private async Task<string> CallApiAsync(string model, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Config.ApiKey))
                throw new InvalidOperationException("Anthropic API Key is missing.");

            var url = string.IsNullOrWhiteSpace(Config.CustomEndpoint)
                ? "https://api.anthropic.com/v1/messages"
                : Config.CustomEndpoint;

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-api-key", Config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            if (forceJson)
            {
                prompt += "\n\nRespond ONLY with valid JSON. Do not include markdown formatting or explanations.";
            }

            var requestBody = new
            {
                model = model,
                max_tokens = 4096,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var jsonBody = JsonSerializer.Serialize(requestBody);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Anthropic API failed with status {Status}: {ErrorBody}", response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            
            try
            {
                return jsonResponse.GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogError(ex, "Unexpected response format from Anthropic. Raw JSON: {Json}", jsonResponse.GetRawText());
                throw new Exception("Unexpected response format from Anthropic.");
            }
        }
    }
}
