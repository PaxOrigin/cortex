using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Cortex.Infrastructure;

public sealed class AgentCachingSession
{
    private readonly Dictionary<string, string> _cache = new();
    private readonly ILogger<AgentCachingSession> _logger;
    private readonly InMemoryChatHistoryProvider _historyProvider;

    public AgentCachingSession(ILogger<AgentCachingSession> logger, InMemoryChatHistoryProvider historyProvider)
    {
        _logger = logger;
        _historyProvider = historyProvider;
    }

    private bool TurnHadToolCalls(AgentSession? session, int messagesBeforeTurn)
    {
        if (session is null) return false;
        var messages = _historyProvider.GetMessages(session);
        return messages
            .Skip(messagesBeforeTurn)
            .Any(m => m.Contents?.OfType<FunctionCallContent>().Any() == true);
    }

    public async Task<AgentResponse> InvokeAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var cacheKey = messages.LastOrDefault()?.Text ?? string.Empty;

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogInformation("AgentCache HIT. Key={Key}", cacheKey);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, cached));
        }

        _logger.LogInformation("AgentCache MISS. Key={Key}", cacheKey);
        var messagesBeforeTurn = _historyProvider.GetMessages(session).Count;
        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);

        if (!TurnHadToolCalls(session, messagesBeforeTurn) && !string.IsNullOrWhiteSpace(response.Text))
        {
            _cache[cacheKey] = response.Text;
            _logger.LogInformation("AgentCache STORE. Key={Key} Length={Length}",
                cacheKey, response.Text.Length);
        }
        else
        {
            _logger.LogInformation("AgentCache SKIP — tool calls detected. Key={Key}", cacheKey);
        }

        return response;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> InvokeStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cacheKey = messages.LastOrDefault()?.Text ?? string.Empty;

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogInformation("AgentCache HIT. Key={Key}", cacheKey);
            yield return new AgentResponseUpdate(ChatRole.Assistant, cached);
            yield break;
        }

        _logger.LogInformation("AgentCache MISS. Key={Key}", cacheKey);
        var messagesBeforeTurn = _historyProvider.GetMessages(session).Count;
        var responseBuilder = new StringBuilder();

        await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
        {
            responseBuilder.Append(update.Text ?? string.Empty);
            yield return update;
        }

        if (!TurnHadToolCalls(session, messagesBeforeTurn) && responseBuilder.Length > 0)
        {
            _cache[cacheKey] = responseBuilder.ToString();
            _logger.LogInformation("AgentCache STORE. Key={Key} Length={Length}",
                cacheKey, responseBuilder.Length);
        }
        else
        {
            _logger.LogInformation("AgentCache SKIP — tool calls detected. Key={Key}", cacheKey);
        }
    }
}