using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Serilog;

public class LogAgentRunMiddleware
{

    public async Task<AgentResponse> LogAgentRunAsync(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent inner, CancellationToken ct)
    {
        session!.TryGetInMemoryChatHistory(out var chatHistory);
        Log.Information("[Messages Number] {0}", chatHistory?.Count ?? 0);
        Log.Information("[User Prompt] {0}", messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text);
        var response = await inner.RunAsync(messages, session, options, ct);
        Log.Information("[Agent Run] {0}", response.Text);
        return response;
    }
}