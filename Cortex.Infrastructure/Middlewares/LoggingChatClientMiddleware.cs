using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Cortex.Infrastructure;

public sealed class LoggingChatClientMiddleware : DelegatingChatClient
{
    private readonly ILogger<LoggingChatClientMiddleware> _logger;

    public LoggingChatClientMiddleware(IChatClient innerClient, ILogger<LoggingChatClientMiddleware> logger)
        : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("LLM request. LastUserMessage={Message}",
            messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text);

        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        _logger.LogInformation("LLM response. Length={Length}", response.Text?.Length ?? 0);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("LLM streaming request. LastUserMessage={Message}",
            messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text);

        var responseBuilder = new StringBuilder();

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            responseBuilder.Append(update.Text ?? string.Empty);
            yield return update;
        }

        _logger.LogInformation("LLM streaming complete. Length={Length}", responseBuilder.Length);
    }
}