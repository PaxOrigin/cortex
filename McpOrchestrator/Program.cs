using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OllamaSharp;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  LOGGING
// ─────────────────────────────────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Warning));

var logger = loggerFactory.CreateLogger("Orchestrator");

// ─────────────────────────────────────────────────────────────────────────────
//  SERVER REGISTRY
//  Describes every server the orchestrator knows about.
//  Adding a new server = adding one entry here. Nothing else changes.
// ─────────────────────────────────────────────────────────────────────────────

var serverDefinitions = new[]
{
    new ServerDefinition(
        Name:      "HttpServer",
        Transport: new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint      = new Uri("http://localhost:5100/mcp"),
            TransportMode = HttpTransportMode.AutoDetect
        })
    ),
    new ServerDefinition(
        Name:      "StdioServer",
        Transport: new StdioClientTransport(new StdioClientTransportOptions
        {
            Name      = "McpServerDotNet",
            Command   = "dotnet",
            Arguments = ["run", "--project",
                         "../McpServerDotNet/McpServerDotNet.csproj",
                         "--no-build"]
        })
    )
};

// ─────────────────────────────────────────────────────────────────────────────
//  CONNECT TO ALL SERVERS
//  Each server gets its own McpClient.
//  We track which client owns which tool for routing later.
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("Connecting to all servers...\n");

// tool name → owning client (for routing)
var toolRouter = new Dictionary<string, McpClient>(
    StringComparer.OrdinalIgnoreCase);

// tool name → McpClientTool (for LLM schema)
var allTools = new List<McpClientTool>();

// Keep clients alive for the duration — dispose at end
var clients = new List<McpClient>();

foreach (var def in serverDefinitions)
{
    try
    {
        var mcpClient = await McpClient.CreateAsync(def.Transport);
        clients.Add(mcpClient);

        var serverTools = await mcpClient.ListToolsAsync();

        Console.WriteLine($"  ✓ {def.Name,-20} " +
                          $"{mcpClient.ServerInfo?.Name} " +
                          $"({serverTools.Count} tools)");

        foreach (var tool in serverTools)
        {
            // Last-writer wins on name collision — log it
            if (toolRouter.ContainsKey(tool.Name))
            {
                logger.LogWarning(
                    "Tool '{Tool}' already registered — {Server} overrides previous",
                    tool.Name, def.Name);
            }

            toolRouter[tool.Name] = mcpClient;
            allTools.Add(tool);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to connect to {Server}", def.Name);
        Console.WriteLine($"  ✗ {def.Name,-20} FAILED: {ex.Message}");
    }
}

Console.WriteLine($"\n  Total tools in unified registry: {allTools.Count}\n");

// ─────────────────────────────────────────────────────────────────────────────
//  TOOL REGISTRY DISPLAY
//  Show the merged tool list grouped by server — transparency for debugging.
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("=== Unified Tool Registry ===");
foreach (var def in serverDefinitions)
{
    var serverToolNames = toolRouter
        .Where(kv => clients.Any(c =>
            ReferenceEquals(c, kv.Value) &&
            serverDefinitions.First(s => s.Name == def.Name).Name == def.Name))
        .Select(kv => kv.Key)
        .ToList();

    // simpler: just group by matching client reference
}

// Cleaner display — group tools by client
var clientToolMap = allTools
    .GroupBy(t => toolRouter.TryGetValue(t.Name, out var c) ? c : null)
    .Where(g => g.Key is not null);

foreach (var group in clientToolMap)
{
    var serverName = serverDefinitions
        .FirstOrDefault(d =>
            clients.Any(c => ReferenceEquals(c, group.Key) &&
                             clients.IndexOf(c) ==
                             Array.IndexOf(serverDefinitions, d)))?.Name
        ?? "unknown";

    // Simpler: use index alignment
    int idx = clients.IndexOf(group.Key!);
    var name = idx >= 0 ? serverDefinitions[idx].Name : "unknown";

    Console.WriteLine($"\n  [{name}]");
    foreach (var t in group)
        Console.WriteLine($"    {t.Name,-35} {t.Description}");
}

// ─────────────────────────────────────────────────────────────────────────────
//  ORCHESTRATING ROUTER
//  This is the core of the orchestrator pattern.
//  The LLM sees ALL tools. When it calls one, the router finds
//  the correct McpClient and dispatches the call there.
//  The LLM never knows or cares which server owns the tool.
// ─────────────────────────────────────────────────────────────────────────────

// async Task<string> CallToolViaRouterAsync(
//     string toolName,
//     IReadOnlyDictionary<string, object?> arguments)
// {
//     if (!toolRouter.TryGetValue(toolName, out var owningClient))
//         return $"Error: no server registered for tool '{toolName}'";

//     var args = arguments.ToDictionary(k => k.Key, v => v.Value);
//     var result = await owningClient.CallToolAsync(toolName, args);

//     if (result.IsError is true)
//     {
//         return "Tool error: " +
//                (result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
//                 ?? "unknown");
//     }

//     return result.Content
//                .OfType<TextContentBlock>()
//                .FirstOrDefault()?.Text ?? "(no result)";
// }

// ─────────────────────────────────────────────────────────────────────────────
//  OLLAMA CLIENT WITH FUNCTION INVOCATION
//  UseFunctionInvocation middleware intercepts tool calls from the LLM,
//  but we override execution via FunctionInvocationContext to route
//  through our orchestrator instead of calling tools directly.
// ─────────────────────────────────────────────────────────────────────────────

IChatClient ollama = new ChatClientBuilder(
    new OllamaApiClient(new Uri("http://localhost:11434/"))
    {
        SelectedModel = "qwen2.5"
    })
    .UseLogging(loggerFactory)
    .UseFunctionInvocation()
    .Build();

// ─────────────────────────────────────────────────────────────────────────────
//  AGENT RUNNER
//  Filters tools per scenario for focus — the router handles actual dispatch.
// ─────────────────────────────────────────────────────────────────────────────

async Task RunAgentAsync(string userPrompt, IEnumerable<string>? toolFilter = null)
{
    var sep = new string('═', 55);
    Console.WriteLine($"\n{sep}");
    Console.WriteLine($"  USER: {userPrompt}");
    Console.WriteLine(sep);

    // Build the tool list for this scenario
    var scenarioTools = toolFilter is not null
        ? allTools
            .Where(t => toolFilter.Contains(t.Name,
                StringComparer.OrdinalIgnoreCase))
            .Cast<AITool>()
            .ToList()
        : allTools.Cast<AITool>().ToList();

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System,
    """
    You are a helpful assistant with access to multiple backend servers.
    You MUST call the available tools to answer every question.
    NEVER compute, reverse, count, or calculate anything yourself.
    NEVER answer from memory or training data.
    For string operations, ALWAYS call the reverse or count_words tool.
    For math, ALWAYS call add or multiply.
    For server data, ALWAYS call get_metrics or run_diagnostics.
    If you are not calling a tool, you are doing it wrong.
    """),
        new(ChatRole.User, userPrompt)
    };

    var chatOptions = new ChatOptions { Tools = scenarioTools };

    try
    {
        var response = await ollama.GetResponseAsync(messages, chatOptions);
        Console.WriteLine($"\n  ASSISTANT: {response.Text}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Agent failed");
        Console.WriteLine($"\n  [ERROR] {ex.Message}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  SCENARIOS
//  Each demonstrates cross-server tool routing.
// ─────────────────────────────────────────────────────────────────────────────

// Scenario 1 — tool from HttpServer only
await RunAgentAsync(
    "What are the current server metrics? Give me CPU and memory.",
    ["get_metrics"]);

// Scenario 2 — tool from StdioServer only
await RunAgentAsync(
    "Reverse the string 'MCP Orchestrator' and count its words.",
    ["reverse", "count_words"]);

// Scenario 3 — cross-server: uses tools from BOTH servers
await RunAgentAsync(
    "Add 42 and 58, then check the server metrics to confirm the server is healthy.",
    ["add", "get_metrics", "get_server_status"]);

// Scenario 4 — full unified registry, LLM picks freely
await RunAgentAsync(
    "Run a server health check and also tell me what 7 multiplied by 6 is.");

// ─────────────────────────────────────────────────────────────────────────────
//  CLEANUP
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine($"\n{new string('═', 55)}");
Console.WriteLine("  Sprint 13 complete. Disposing clients...");
Console.WriteLine(new string('═', 55));

foreach (var c in clients)
    await c.DisposeAsync();

// ─────────────────────────────────────────────────────────────────────────────
//  TYPES
// ─────────────────────────────────────────────────────────────────────────────

record ServerDefinition(string Name, IClientTransport Transport);