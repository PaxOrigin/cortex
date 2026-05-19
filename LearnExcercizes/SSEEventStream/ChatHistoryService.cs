using Microsoft.Extensions.AI;

public class ChatHistoryService : IChatHistoryService
{
    private readonly List<ChatMessage> _history =
    [
        new ChatMessage(ChatRole.System, "You are a helpful assistant.")
    ];

    public IEnumerable<ChatMessage> GetHistory() => _history;
    public void AddMessage(ChatMessage message) => _history.Add(message);
    public void AddMessages(IEnumerable<ChatMessage> updates) =>
        _history.AddRange(updates);
}