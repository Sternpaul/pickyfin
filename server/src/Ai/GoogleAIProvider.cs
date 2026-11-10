using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pickyfin.Server.Data.Models;
using Pickyfin.Server.Library;
using Microsoft.Extensions.Logging;

namespace Pickyfin.Server.Ai
{
    public class GoogleAIProvider : IAIProvider
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<GoogleAIProvider> _logger;

        public string Name => "Google AI";

        public GoogleAIProvider(HttpClient httpClient, ILogger<GoogleAIProvider> logger)
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
            await CallApiAsync(model, prompt, false, cancellationToken);

        public async Task<string> ChatAsync(string userQuery, string systemPrompt, CancellationToken cancellationToken = default)
        {
            var prompt = $"{systemPrompt}\n\nUser: {userQuery}";
            return await CallApiAsync(Config.ChatModel, prompt, false, cancellationToken);
        }

        private async Task<string> CallApiAsync(string model, string prompt, bool forceJson, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Config.ApiKey))
                throw new InvalidOperationException("Google AI API Key is missing.");

            var url = string.IsNullOrWhiteSpace(Config.CustomEndpoint)
                ? $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Config.ApiKey}"
                : $"{Config.CustomEndpoint}/models/{model}:generateContent?key={Config.ApiKey}";

            object generationConfig;
            if (forceJson)
            {
                generationConfig = new 
                { 
                    temperature = 0.2, 
                    responseMimeType = "application/json",
                    responseSchema = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            movies = new
                            {
                                type = "ARRAY",
                                items = new
                                {
                                    type = "OBJECT",
                                    properties = new
                                    {
                                        Ref = new { type = "STRING" },
                                        Known = new { type = "STRING", @enum = new[] { "yes", "partly", "no" } },
                                        Subcategories = new { type = "ARRAY", items = new { type = "STRING", @enum = ClassificationVocabulary.Subcategories } },
                                        Moods = new { type = "ARRAY", items = new { type = "STRING", @enum = ClassificationVocabulary.Moods } },
                                        Themes = new { type = "ARRAY", items = new { type = "STRING" } },
                                        NarrativeStyle = new { type = "STRING", @enum = ClassificationVocabulary.NarrativeStyles },
                                        Accessibility = new { type = "STRING", @enum = ClassificationVocabulary.Accessibility },
                                        Intensity = new { type = "STRING", @enum = ClassificationVocabulary.Intensity },
                                        CriticalAcclaimScore = new { type = "INTEGER" }
                                    },
                                    required = new[] { "Ref", "Known", "Subcategories", "Moods", "Themes", "NarrativeStyle", "Accessibility", "Intensity", "CriticalAcclaimScore" }
                                }
                            }
                        },
                        required = new[] { "movies" }
                    }
                };
            }
            else
            {
                generationConfig = new { temperature = 0.2 };
            }

            var requestBody = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                },
                generationConfig = generationConfig
            };

            var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Google AI API failed with status {Status}: {ErrorBody}", response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();
            }

            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

            try
            {
                var sb = new System.Text.StringBuilder();
                var partsElement = jsonResponse
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts");

                // Gemma / Gemini "thinking" models can emit a `thought` part (internal
                // chain-of-thought, flagged with "thought": true) BEFORE the actual answer.
                // Reading parts[0].text blindly returns that reasoning instead of the
                // user-facing text (or the valid JSON we asked for). Skip thought parts and
                // concatenate only the real answer parts.
                foreach (var part in partsElement.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                    if (!part.TryGetProperty("text", out var text)) continue;
                    sb.Append(text.GetString());
                }

                return sb.ToString();
            }
            catch (KeyNotFoundException ex)
            {
                _logger.LogError(ex, "Unexpected response format from Google AI. Raw JSON: {Json}", jsonResponse.GetRawText());
                throw new Exception("Unexpected response format from Google AI.");
            }
        }
    }
}
