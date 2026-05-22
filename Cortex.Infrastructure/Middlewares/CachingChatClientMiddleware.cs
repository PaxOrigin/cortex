using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Cortex.Infrastructure;

public sealed class CachingChatClientMiddleware : DelegatingChatClient
{
    private readonly ILogger<CachingChatClientMiddleware> _logger;
    private readonly Dictionary<string, ChatResponse> _cache = new();

    public CachingChatClientMiddleware(IChatClient innerClient, ILogger<CachingChatClientMiddleware> logger)
        : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

        if (_cache.TryGetValue(cacheKey, out var cachedResponse))
        {
            _logger.LogInformation("Cache HIT. Key={Key}", cacheKey);
            return cachedResponse;
        }

        _logger.LogInformation("Cache MISS. Key={Key}", cacheKey);
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        _cache[cacheKey] = response;
        _logger.LogInformation("Cache STORE. Key={Key} Length={Length}", cacheKey, response.Text?.Length ?? 0);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cacheKey = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

        if (_cache.TryGetValue(cacheKey, out var cachedResponse))
        {
            _logger.LogInformation("Cache HIT. Key={Key}", cacheKey);
            yield return new ChatResponseUpdate(ChatRole.Assistant, cachedResponse.Text);
            yield break;
        }

        _logger.LogInformation("Cache MISS. Key={Key}", cacheKey);
        var responseBuilder = new StringBuilder();
        var hadToolCalls = false;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (update.Contents?.OfType<FunctionCallContent>().Any() == true)
                hadToolCalls = true;

            responseBuilder.Append(update.Text ?? string.Empty);
            yield return update;
        }

        if (!hadToolCalls && responseBuilder.Length > 0)
        {
            var finalResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseBuilder.ToString()));
            _cache[cacheKey] = finalResponse;
            _logger.LogInformation("Cache STORE. Key={Key} Length={Length}", cacheKey, responseBuilder.Length);
        }
    }
}