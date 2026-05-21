using System.Text;
using Cortex.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
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

List<ChatMessage> conversationHistory =
[
    new ChatMessage(ChatRole.System, """
    You are a personal notebook assistant.
    You MUST ALWAYS use the available tools to read and write data. NEVER answer from memory.
    Available tools:
    - add_entry(content, tags[]): saves a new entry
    - get_entries(): returns all entries  
    - search_entries(query): searches by text or tag
    For any question about tasks or notes: call get_entries or search_entries first.
    """)
];
var responseBuilder = new StringBuilder();

while (true)
{
    var prompt = AnsiConsole.Ask<string>("[green]>[/]");
    logger.LogInformation("User input: {Prompt}", prompt);

    if (prompt.Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (string.IsNullOrWhiteSpace(prompt))
        continue;

    conversationHistory.Add(new ChatMessage(ChatRole.User, prompt));
    try
    {
        await foreach (var update in chatClient.GetStreamingResponseAsync(
            conversationHistory, chatOptions))
        {
            responseBuilder.Append(update.Text ?? string.Empty);
            AnsiConsole.Write(update.Text ?? string.Empty);
        }
        conversationHistory.Add(new ChatMessage(ChatRole.Assistant, responseBuilder.ToString()));
        logger.LogDebug("Full response: {Response}", responseBuilder.ToString());
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error occurred while fetching streaming response");
        AnsiConsole.MarkupLine("[red]Error. Try Again.[/]");
    }
    finally
    {
        responseBuilder.Clear();
    }

    AnsiConsole.WriteLine();
}