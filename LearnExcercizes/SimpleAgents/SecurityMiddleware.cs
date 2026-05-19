using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Serilog;

public sealed class SecurityMiddleware
{
    public async Task<AgentResponse> BlockSensibleRequestAsync
    (
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent inner,
        CancellationToken ct
    )
    {
        if (messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text.Contains("password", StringComparison.OrdinalIgnoreCase) ?? false)
        {
            Log.Warning("Blocked a request containing sensitive information.");
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "Sorry, I cannot process this request."));
        }
        return await inner.RunAsync(messages, session, options, ct);
    }
}