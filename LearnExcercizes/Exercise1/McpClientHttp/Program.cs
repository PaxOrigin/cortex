using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

// MCP setup
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5100/mcp"),
    TransportMode = HttpTransportMode.AutoDetect
});
var mcpClient = await McpClient.CreateAsync(transport);
var tools = await mcpClient.ListToolsAsync();

// IChatClient via Ollama con middleware UseFunctionInvocation
IChatClient chatClient = new OllamaChatClient(
        new Uri("http://localhost:11434"),
        "qwen2.5")
    .AsBuilder()
    .UseFunctionInvocation()
    .Build();

// Chat loop
var history = new List<ChatMessage>();
while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) break;

    history.Add(new ChatMessage(ChatRole.User, input));

    var response = await chatClient.GetResponseAsync(
        history,
        new ChatOptions { Tools = [.. tools] });

    history.Add(new ChatMessage(ChatRole.Assistant, response.Text));
    Console.WriteLine($"Agent: {response.Text}");
}