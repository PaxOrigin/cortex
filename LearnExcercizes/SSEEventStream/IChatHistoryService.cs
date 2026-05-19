using Microsoft.Extensions.AI;
using OllamaSharp;

public interface IChatHistoryService
{
    public IEnumerable<ChatMessage> GetHistory();
    public void AddMessage(ChatMessage message);
    public void AddMessages(IEnumerable<ChatMessage> updates);
}