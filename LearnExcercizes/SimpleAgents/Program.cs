using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Serilog;
using System.Text.Json;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

IChatClient ollamaClient = new ChatClientBuilder(
    new OllamaApiClient(new Uri("http://localhost:11434"), "qwen2.5"))
    .Build();

AIFunction getWeatherFunction = AIFunctionFactory.Create(
    () => "The weather is sunny and 22°C",
    "get_weather",
    "Get the current weather at the user's location");

AIFunction getTimeFunction = AIFunctionFactory.Create(
    () => $"The current time is {DateTime.Now:HH:mm:ss}",
    "get_time",
    "Get the current time at the user's location");

AIAgent agent = new ChatClientAgent(
    ollamaClient,
    instructions: "You are a helpful assistant specialized in weather and time. " +
                  "Always use the available tools before answering. " +
                  "Do not make up information.",
    tools: [getWeatherFunction, getTimeFunction]).AsBuilder()
    .Use(
        runFunc: new SecurityMiddleware().BlockSensibleRequestAsync,
        runStreamingFunc: null
    )
    .Use(runFunc: new LogAgentRunMiddleware().LogAgentRunAsync, runStreamingFunc: null)
    .Use(new LogFunctionCallsMiddleware().InvokeAsync)
    .Build();

AgentSession session = await agent.CreateSessionAsync();
Log.Information("Agent ready. Commands: save | load | quit");

while (true)
{
    Console.Write("\nYou: ");
    string input = Console.ReadLine() ?? string.Empty;

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        Log.Information("Exiting.");
        break;
    }

    if (input.Equals("save", StringComparison.OrdinalIgnoreCase))
    {
        var data = await agent.SerializeSessionAsync(session);
        await File.WriteAllTextAsync("session.json", data.ToString());
        Log.Information("Session saved to session.json");
        continue;
    }

    if (input.Equals("load", StringComparison.OrdinalIgnoreCase))
    {
        if (!File.Exists("session.json"))
        {
            Log.Warning("No session.json found.");
            continue;
        }
        var text = await File.ReadAllTextAsync("session.json");
        var json = JsonSerializer.Deserialize<JsonElement>(text);
        session = await agent.DeserializeSessionAsync(json);
        Log.Information("Session loaded from session.json");
        continue;
    }

    Log.Debug("Sending message to agent: {Input}", input);
    var response = await agent.RunAsync(input, session);
    Console.WriteLine($"\nAgent: {response.Text}");
}

Log.CloseAndFlush();