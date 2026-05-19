using Microsoft.Extensions.AI;
using OllamaSharp;

const string OLLAMA_URI = "http://localhost:11434";
const string MODEL_NAME = "QWEN2.5";

using IChatClient client = new OllamaApiClient(OLLAMA_URI, MODEL_NAME);

List<ChatMessage> messages = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "You are a helpful assistant, you will answer question the user ask you."),
};
string response = string.Empty;
messages.Add(new ChatMessage(ChatRole.User, "I am Giorgio, What is the capital of France?"));
Console.WriteLine("[User]: I am Giorgio, What is the capital of France?");
await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages))
{
    Console.Write(update.Text);
    response += update.Text;
}
Console.WriteLine();

messages.Add(new ChatMessage(ChatRole.Assistant, response));
response = string.Empty;
messages.Add(new ChatMessage(ChatRole.User, "What is the capital of Germany?"));
Console.WriteLine("[User]: What is the capital of Germany?");
await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages))
{
    Console.Write(update.Text);
    response += update.Text;
}
Console.WriteLine();

messages.Add(new ChatMessage(ChatRole.Assistant, response));
response = string.Empty;
messages.Add(new ChatMessage(ChatRole.User, "What is the capital of France, could you remind me?"));
Console.WriteLine("[User]: What is the capital of France, could you remind me?");
await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages))
{
    Console.Write(update.Text);
    response += update.Text;
}
messages.Add(new ChatMessage(ChatRole.Assistant, response));
response = string.Empty;
Console.WriteLine();
Console.WriteLine("Done.");
// === AI Tools ===
response = string.Empty;
AIFunction greetFunction = AIFunctionFactory
    .Create(GreetYourself, "greet_yourself", "This function allows you to greet yourself.");

List<AITool> tools = new List<AITool>
{
    greetFunction
};

using IChatClient cllientWithTools = new ChatClientBuilder(new OllamaApiClient(OLLAMA_URI, MODEL_NAME))
    .UseFunctionInvocation()
    .Build();

tools.Add(AIFunctionFactory.Create(new Func<string>(() => DateTime.UtcNow.ToString("o")), "get_current_time_utc", "This tool returns the current time in ISO 8601 format."));
ChatOptions options = new ChatOptions
{
    Tools = tools
};

List<ChatMessage> messagesWithTools = new List<ChatMessage>
{
    new ChatMessage(ChatRole.System, "You are a helpful assistant, you will answer question the user ask you."),
    new ChatMessage(ChatRole.User, "Hello, I am Alessandro, Greet yourself and tell me the current time in UTC."),
};
Console.WriteLine("[User]: Hello, I am Alessandro, Greet yourself and tell me the current time in UTC.");

await foreach (ChatResponseUpdate update in cllientWithTools.GetStreamingResponseAsync(messagesWithTools, options))
{
    Console.Write(update.Text);
    response += update.Text;
}
Console.WriteLine();
response = string.Empty;
/// <summary>
/// A function to greet the user.
/// <param name="input">The input string.</param>
/// <returns>The greeting message.</returns>
string GreetYourself(string input)
{
    return $"Hello {input}, I am your AI assistant!";
}