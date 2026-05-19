using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OllamaSharp;

public sealed class AddSystemPrompt(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? chatOptions = null,
    CancellationToken cancellationToken = default)
    {
        var effectiveMessages = messages.Any(p => p.Role == ChatRole.System)
            ? messages
            : messages.Prepend(new ChatMessage(ChatRole.System, "You are a helpful assistant. You always start your sentences with [Bip Bop]."));

        return await base.GetResponseAsync(effectiveMessages, chatOptions, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!messages.Any(p => p.Role == ChatRole.System))
        {
            messages = messages.Append(new ChatMessage(ChatRole.System, "You are a helpful assistant. You always start your stentences with [Bip Bop]."));
        }

        await foreach (var response in base.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
        {
            yield return response;
        }
    }
}