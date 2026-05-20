using System.Text;
using Cortex.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Spectre.Console;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File("logs/cortex-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton(ChatClientFactory.Create(context.Configuration));
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var chatClient = host.Services.GetRequiredService<IChatClient>();
List<ChatMessage> conversationHistory = new();
StringBuilder responseBuilder = new();
conversationHistory.Add(new ChatMessage(ChatRole.System, "You are an helfup assistant who helps the user remember, track and plan their activities."));
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
        await foreach (var update in chatClient.GetStreamingResponseAsync(conversationHistory))
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
    conversationHistory.Add(new ChatMessage(ChatRole.Assistant, responseBuilder.ToString()));
    responseBuilder.Clear();
    AnsiConsole.WriteLine();
}