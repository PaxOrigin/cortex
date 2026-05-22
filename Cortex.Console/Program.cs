using System.Text;
using System.Text.Json;
using Cortex.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Spectre.Console;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File("logs/cortex-.log", rollingInterval: RollingInterval.Hour)
    .CreateLogger();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton(sp => ChatClientFactory.Create(
    context.Configuration,
    sp.GetRequiredService<ILoggerFactory>()));
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var chatClient = host.Services.GetRequiredService<IChatClient>();

// MCP client — await using garantisce dispose del processo figlio
var (mcpClient, tools) = await CortexMcpClient.CreateAsync(logger);
await using var _ = mcpClient;
logger.LogInformation("MCP ready. {Count} tools loaded.", tools.Count);

var chatOptions = new ChatOptions { Tools = [.. tools] };

// Used in the past with AiChatClient, agents allow to handle through the framework using
List<ChatMessage> conversationHistory =
[
    new ChatMessage(ChatRole.System, """
    You are a personal notebook assistant. You manage a notebook of tagged entries.
    
    RULES:
    - To add data: ALWAYS call add_entry. Never confirm an addition without calling it first.
    - To read data: ALWAYS call get_entries or search_entries. Never answer from memory.
    - Never invent, assume, or hallucinate entries, tags, ids, or timestamps.
    - If the user asks about data and the tool returns nothing, say "No entries found" — do not guess.
    - If the request is ambiguous, ask the user to clarify before calling any tool.
    
    Available tools:
    - add_entry(content, tags[]): saves a new entry
    - get_entries(): returns all entries
    - search_entries(query): searches by text or tag (strip @ from tags)
    """)
];
var responseBuilder = new StringBuilder();

var historyProvider = new InMemoryChatHistoryProvider();

var agentCache = new AgentCachingSession(
    host.Services.GetRequiredService<ILogger<AgentCachingSession>>(),
    historyProvider);
AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    ChatHistoryProvider = historyProvider,
    ChatOptions = new ChatOptions
    {
        Instructions = """
    You are a personal notebook assistant. You manage a notebook of tagged entries.
    
    RULES:
    - To add data: ALWAYS call add_entry. Never confirm an addition without calling it first.
    - To read data: ALWAYS call get_entries or search_entries. Never answer from memory.
    - Never invent, assume, or hallucinate entries, tags, ids, or timestamps.
    - If the user asks about data and the tool returns nothing, say "No entries found" — do not guess.
    - If the request is ambiguous, ask the user to clarify before calling any tool.
    
    Available tools:
    - add_entry(content, tags[]): saves a new entry
    - get_entries(): returns all entries
    - search_entries(query): searches by text or tag (strip @ from tags)
    """,
        Tools = [.. tools]
    }
});

agent = agent
    .AsBuilder()
    .Use(runFunc: agentCache.InvokeAsync, runStreamingFunc: agentCache.InvokeStreamingAsync)
    .Build();

var agentSession = await agent.CreateSessionAsync();

if (File.Exists("session.json"))
{
    var json = await File.ReadAllTextAsync("session.json");
    if (!string.IsNullOrWhiteSpace(json))
    {
        var jsonElement = JsonSerializer.Deserialize<JsonElement>(json);
        agentSession = await agent.DeserializeSessionAsync(jsonElement);
        logger.LogInformation("Session restored from file.");
    }
    else
    {
        logger.LogInformation("Session file is empty. Starting new session.");
    }
}

while (true)
{
    var prompt = AnsiConsole.Ask<string>("[blue]>[/]");
    logger.LogInformation("User input: {Prompt}", prompt);

    if (prompt.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (string.IsNullOrWhiteSpace(prompt))
        continue;

    try
    {
        await foreach (var update in agent.RunStreamingAsync(prompt, agentSession))
        {
            responseBuilder.Append(update.Text ?? string.Empty);
            AnsiConsole.Write(update.Text ?? string.Empty);
        }

        logger.LogDebug("Full response: {Response}", responseBuilder.ToString());
        var sessionElement = await agent.SerializeSessionAsync(agentSession);
        await File.WriteAllTextAsync("session.json", JsonSerializer.Serialize(sessionElement));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error occurred while fetching streaming response");
        AnsiConsole.MarkupLine("[red]Error. Try Again.[/]");
    }
    finally
    {
        AnsiConsole.WriteLine();
        responseBuilder.Clear();
    }
}


// Codice con AiChatClient, senza agenti
// while (true)
// {
//     var prompt = AnsiConsole.Ask<string>("[green]>[/]");
//     logger.LogInformation("User input: {Prompt}", prompt);

//     if (prompt.Equals("exit", StringComparison.OrdinalIgnoreCase))
//         break;

//     if (string.IsNullOrWhiteSpace(prompt))
//         continue;

//     conversationHistory.Add(new ChatMessage(ChatRole.User, prompt));
//     try
//     {
//         await foreach (var update in chatClient.GetStreamingResponseAsync(
//             conversationHistory, chatOptions))
//         {
//             responseBuilder.Append(update.Text ?? string.Empty);
//             AnsiConsole.Write(update.Text ?? string.Empty);
//         }
//         conversationHistory.Add(new ChatMessage(ChatRole.Assistant, responseBuilder.ToString()));
//         logger.LogDebug("Full response: {Response}", responseBuilder.ToString());
//     }
//     catch (Exception ex)
//     {
//         logger.LogError(ex, "Error occurred while fetching streaming response");
//         AnsiConsole.MarkupLine("[red]Error. Try Again.[/]");
//     }
//     finally
//     {
//         responseBuilder.Clear();
//     }

//     AnsiConsole.WriteLine();
// }