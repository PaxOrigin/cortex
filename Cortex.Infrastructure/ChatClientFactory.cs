using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;

namespace Cortex.Infrastructure;

public static class ChatClientFactory
{
    private const string AnthropicModel = "claude-haiku-4-5";
    private const string OllamaModel = "qwen2.5";
    private const string DefaultOllamaHost = "http://localhost:11434";

    public static IChatClient Create(IConfiguration configuration)
    {
        var anthropicApiKey = configuration["ANTHROPIC_API_KEY"];

        if (!string.IsNullOrWhiteSpace(anthropicApiKey))
        {
            return new AnthropicClient { ApiKey = anthropicApiKey }
                .AsIChatClient(AnthropicModel)
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();
        }

        var ollamaHost = configuration["OLLAMA_HOST"] ?? DefaultOllamaHost;
        var ollamaClient = new ChatClientBuilder(new OllamaApiClient(ollamaHost, OllamaModel))
            .UseFunctionInvocation()
            .Build();
        return ollamaClient;
    }
}