using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OllamaSharp;

namespace Cortex.Infrastructure;

public static class ChatClientFactory
{
    private const string AnthropicModel = "claude-haiku-4-5";
    private const string OllamaModel = "qwen2.5";
    private const string DefaultOllamaHost = "http://localhost:11434";

    public static IChatClient Create(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<LoggingChatClientMiddleware>();
        var anthropicApiKey = configuration["ANTHROPIC_API_KEY"];

        if (!string.IsNullOrWhiteSpace(anthropicApiKey))
        {
            return new AnthropicClient { ApiKey = anthropicApiKey }
                .AsIChatClient(AnthropicModel)
                .AsBuilder()
                .UseFunctionInvocation()
                .Use(inner => new LoggingChatClientMiddleware(inner, logger))
                .Build();
        }

        var ollamaHost = configuration["OLLAMA_HOST"] ?? DefaultOllamaHost;
        return new ChatClientBuilder(new OllamaApiClient(ollamaHost, OllamaModel))
            .UseFunctionInvocation()
            .Use(inner => new LoggingChatClientMiddleware(inner, logger))
            .Build();
    }
}