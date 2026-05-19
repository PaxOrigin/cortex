using Microsoft.Extensions.AI;
using OllamaSharp;


using IChatClient client = new ChatClientBuilder(innerClient:
    new OllamaApiClient(
            "http://localhost:11434",
            "Qwen2.5"))
    .Use(inner => new AddSystemPrompt(inner: inner))
    .UseFunctionInvocation()
    .Build();

IEnumerable<ChatMessage> messages = new[]
{
    new ChatMessage(ChatRole.User, "What is the weather like today?")
};

var response = await client.GetResponseAsync(messages);
Console.WriteLine(response.Text ?? string.Empty);
messages.Append(new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty));
Console.WriteLine(string.Concat("-", new string('-', 50), "-"));
Console.WriteLine(string.Join(Environment.NewLine, string.Join(Environment.NewLine, messages.Select(p => $"{p.Role}: {p.Text}"))));
Console.WriteLine(string.Concat("-", new string('-', 50), "-"));
messages = new[]
{
    new ChatMessage(ChatRole.User, "What are you???")
};

var bugger = string.Empty;
await foreach (var update in client.GetStreamingResponseAsync(messages))
{
    Console.Write(update.Text);
    bugger += update.Text;
}

messages.Append(new ChatMessage(ChatRole.Assistant, bugger));
Console.WriteLine(string.Concat("-", new string('-', 50), "-"));
Console.WriteLine(string.Join(Environment.NewLine, string.Join(Environment.NewLine, messages.Select(p => $"{p.Role}: {p.Text}"))));
Console.WriteLine(string.Concat("-", new string('-', 50), "-"));
Console.WriteLine();