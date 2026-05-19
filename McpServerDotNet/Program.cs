using McpServerDotNet;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);

// CRITICAL: all logs must go to stderr
// stdout is reserved exclusively for MCP protocol messages
// mixing them corrupts the JSON-RPC stream
builder.Logging.AddConsole(opts =>
    opts.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<ITimeService, TimeService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

await builder.Build().RunAsync();


// ─────────────────────────────────────────────
//  TOOL DEFINITIONS
//  Each class marked [McpServerToolType] is
//  discovered automatically by WithToolsFromAssembly
// ─────────────────────────────────────────────

[McpServerToolType]
public static class MathTools
{
    [McpServerTool, Description("Adds two integers and returns the result")]
    public static int Add(
        [Description("The first number")] int a,
        [Description("The second number")] int b)
        => a + b;

    [McpServerTool, Description("Multiplies two integers and returns the result")]
    public static int Multiply(
        [Description("The first number")] int a,
        [Description("The second number")] int b)
        => a * b;
}

[McpServerToolType]
public static class TextTools
{
    [McpServerTool, Description("Reverses a string and returns it")]
    public static string Reverse(
        [Description("The string to reverse")] string input)
        => new string(input.Reverse().ToArray());

    [McpServerTool, Description("Counts the number of words in a string")]
    public static int CountWords(
        [Description("The text to count words in")] string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}



[McpServerToolType]
public class TimeTools  // ← remove static
{
    private readonly ITimeService _timeService;
    private readonly ILogger<TimeTools> _logger;

    // DI constructor — the SDK resolves this automatically
    public TimeTools(ITimeService timeService, ILogger<TimeTools> logger)
    {
        _timeService = timeService;
        _logger = logger;
    }

    [McpServerTool, Description("Returns the current UTC time and local timezone")]
    public string GetCurrentTime()
    {
        return $"UTC: {_timeService.GetCurrentTime():o}\n" +
               $"Timezone: {_timeService.GetTimezone()}";
    }

    [McpServerTool, Description("Formats the current time using a custom format string")]
    public string FormatTime(
        [Description("A .NET date format string, e.g. 'yyyy-MM-dd'")] string format)
        => _timeService.GetFormattedTime(format);

    [McpServerTool, Description("Returns server uptime info and logs the request")]
    public string GetServerInfo()
    {
        _logger.LogInformation("GetServerInfo called at {Time}", _timeService.GetCurrentTime());

        return $"Server time: {_timeService.GetCurrentTime():o}\n" +
               $"Runtime:     .NET {Environment.Version}\n" +
               $"OS:          {Environment.OSVersion}";
    }
}

// ─────────────────────────────────────────────
//  RESOURCES
//  Direct resources have fixed URIs and appear
//  in resources/list immediately.
//  Template resources use {parameters} and only
//  appear in resources/templates/list.
// ─────────────────────────────────────────────

[McpServerResourceType]
public static class ServerResources
{
    // Direct resource — fixed URI, always listed
    [McpServerResource(
        UriTemplate = "info://server/status",
        Name = "Server Status",
        MimeType = "application/json")]
    [Description("Returns the current server status and build info")]
    public static string GetStatus()
    {
        var status = new
        {
            server = "McpServerDotNet",
            status = "running",
            time = DateTime.UtcNow.ToString("o"),
            dotnet = Environment.Version.ToString()
        };
        return System.Text.Json.JsonSerializer.Serialize(status,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    // Template resource — parameterized, appears in templates/list
    [McpServerResource(
        UriTemplate = "math://times-table/{number}",
        Name = "Times Table",
        MimeType = "text/plain")]
    [Description("Returns the times table for a given number")]
    public static string GetTimesTable(int number)
    {
        var lines = Enumerable.Range(1, 10)
            .Select(i => $"{number} x {i,2} = {number * i}");
        return string.Join("\n", lines);
    }
}

// ─────────────────────────────────────────────
//  PROMPTS
//  Reusable message templates the client can
//  fetch and inject into LLM conversations.
//  Return ChatMessage for simple content,
//  IEnumerable<ChatMessage> for multi-turn.
// ─────────────────────────────────────────────

[McpServerPromptType]
public static class ServerPrompts
{
    // Simple prompt — no arguments
    [McpServerPrompt, Description("A prompt that asks the LLM to introduce itself clearly")]
    public static ChatMessage Introduce()
        => new(ChatRole.User,
            "Please introduce yourself. State what you are, what you can help with, " +
            "and what your limitations are. Be concise and honest.");

    // Prompt with arguments — multi-turn
    [McpServerPrompt, Description("Generates a code review conversation for a given snippet")]
    public static IEnumerable<ChatMessage> ReviewCode(
        [Description("The programming language of the snippet")] string language,
        [Description("The code to review")] string code)
        =>
        [
            new(ChatRole.User,
                $"Please review this {language} code and identify any issues:\n\n" +
                $"```{language}\n{code}\n```"),
            new(ChatRole.Assistant,
                $"I'll review this {language} code for correctness, " +
                $"style, and potential improvements.")
        ];
}