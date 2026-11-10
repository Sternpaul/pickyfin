using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Pickyfin.Server.Ai
{
    public class AIProviderFactory
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;

        public AIProviderFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
        {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
        }

        private static ServerSettings Config => ServerSettings.Current;

        public IAIProvider GetProvider()
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            return Config.AIProvider switch
            {
                AIProviderType.GoogleAI => new GoogleAIProvider(httpClient, _loggerFactory.CreateLogger<GoogleAIProvider>()),
                AIProviderType.OpenRouter => new OpenRouterProvider(httpClient, _loggerFactory.CreateLogger<OpenRouterProvider>()),
                AIProviderType.OpenAI => new OpenAIProvider(httpClient, _loggerFactory.CreateLogger<OpenAIProvider>()),
                AIProviderType.Anthropic => new AnthropicProvider(httpClient, _loggerFactory.CreateLogger<AnthropicProvider>()),
                _ => throw new NotImplementedException($"AI Provider {Config.AIProvider} is not implemented.")
            };
        }
    }
}
