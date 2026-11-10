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
using Pickyfin.Server.Library;
using Microsoft.Extensions.Logging;

namespace Pickyfin.Server.Ai
{
    public class OpenAIProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OpenAIProvider> _logger;

        public string Name => "OpenAI";

        public OpenAIProvider(HttpClient httpClient, ILogger<OpenAIProvider> logger)
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
                throw new InvalidOperationException("OpenAI API Key is missing.");

            var url = string.IsNullOrWhiteSpace(Config.CustomEndpoint)
                ? "https://api.openai.com/v1/chat/completions"
                : Config.CustomEndpoint;

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                response_format = forceJson ? new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "movie_classification",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                movies = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            ItemId = new { type = "string" },
                                            Subcategories = new { type = "array", items = new { type = "string", @enum = ClassificationVocabulary.Subcategories } },
                                            Moods = new { type = "array", items = new { type = "string", @enum = ClassificationVocabulary.Moods } },
                                            Themes = new { type = "array", items = new { type = "string" } },
                                            NarrativeStyle = new { type = "string", @enum = ClassificationVocabulary.NarrativeStyles },
                                            Accessibility = new { type = "string", @enum = ClassificationVocabulary.Accessibility },
                                            Intensity = new { type = "string", @enum = ClassificationVocabulary.Intensity },
                                            CriticalAcclaimScore = new { type = "integer" }
                                        },
                                        required = new[] { "ItemId", "Subcategories", "Moods", "Themes", "NarrativeStyle", "Accessibility", "Intensity", "CriticalAcclaimScore" },
                                        additionalProperties = false
                                    }
                                }
                            },
                            required = new[] { "movies" },
                            additionalProperties = false
                        }
                    }
                } : null
            };

            var jsonBody = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("OpenAI API failed with status {Status}: {ErrorBody}", response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            
            try
            {
                return jsonResponse.GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty;
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogError(ex, "Unexpected response format from OpenAI. Raw JSON: {Json}", jsonResponse.GetRawText());
                throw new Exception("Unexpected response format from OpenAI.");
            }
        }
    }
}
