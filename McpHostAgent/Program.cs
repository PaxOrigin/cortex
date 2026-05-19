using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OllamaSharp;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  LOGGING
//  Production pattern: use ILoggerFactory so all layers (Ollama, MCP, app)
//  emit structured logs through the same pipeline.
//  SetMinimumLevel(Information) in production; Trace for debugging.
// ─────────────────────────────────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Warning));  // raise to Trace to see all wire traffic

var logger = loggerFactory.CreateLogger("Agent");

// ─────────────────────────────────────────────────────────────────────────────
//  MCP TRANSPORT + CLIENT
// ─────────────────────────────────────────────────────────────────────────────

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5100/mcp"),
    TransportMode = HttpTransportMode.AutoDetect
});

// ── Sampling handler — wired to real Ollama ───────────────────────────────
// The MCP server can call sampling/createMessage back to us.
// We create a fresh client per request (stateless sampling calls).
static async ValueTask<CreateMessageResult> HandleSamplingAsync(
    CreateMessageRequestParams? request,
    IProgress<ProgressNotificationValue> progress,
    CancellationToken ct)
{
    var samplingClient = new ChatClientBuilder(
        new OllamaApiClient(new Uri("http://localhost:11434/"))
        {
            SelectedModel = "qwen2.5"
        })
        .Build();

    var messages = request?.Messages
        .Select(m => new ChatMessage(
            m.Role == Role.User ? ChatRole.User : ChatRole.Assistant,
            m.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? ""))
        .ToList() ?? [];

    var response = await samplingClient.GetResponseAsync(messages, cancellationToken: ct);

    return new CreateMessageResult
    {
        Role = Role.Assistant,
        Content = [new TextContentBlock { Text = response.Text ?? "" }],
        Model = response.ModelId ?? "qwen2.5"
    };
}

var mcpOptions = new McpClientOptions
{
    ClientInfo = new Implementation { Name = "McpHostAgent", Version = "1.0.0" },
    Capabilities = new ClientCapabilities
    {
        Roots = new RootsCapability { ListChanged = true },
        Sampling = new SamplingCapability()
    },
    Handlers = new McpClientHandlers
    {
        RootsHandler = (_, ct) => ValueTask.FromResult(new ListRootsResult
        {
            Roots =
            [
                new Root { Uri = "file:///tmp/mcp-workspace", Name = "MCP Workspace" },
                new Root { Uri = "file:///tmp/mcp-data",      Name = "MCP Data"      }
            ]
        }),
        SamplingHandler = HandleSamplingAsync
    }
};

logger.LogInformation("Connecting to MCP server...");
await using var mcpClient = await McpClient.CreateAsync(transport, mcpOptions);
logger.LogInformation("Connected to {Name} {Version}",
    mcpClient.ServerInfo?.Name, mcpClient.ServerInfo?.Version);

Console.WriteLine($"Connected to {mcpClient.ServerInfo?.Name} {mcpClient.ServerInfo?.Version}\n");

// ─────────────────────────────────────────────────────────────────────────────
//  TOOL LIST
//  McpClientTool inherits from AIFunction.
//  No adapter code — plug directly into ChatOptions.Tools.
//  Filter to the tools relevant for monitoring tasks — a focused tool list
//  produces significantly more reliable tool selection from small models.
// ─────────────────────────────────────────────────────────────────────────────

var allTools = await mcpClient.ListToolsAsync();

var agentTools = allTools
    .Where(t => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "get_metrics",
        "get_server_status",
        "run_diagnostics",
        "append_audit_log"
    }.Contains(t.Name))
    .ToList<AITool>();

Console.WriteLine($"=== Tools exposed to LLM ({agentTools.Count}/{allTools.Count}) ===");
foreach (var t in agentTools)
    Console.WriteLine($"  {t.Name,-30} {t.Description}");
Console.WriteLine();

// ─────────────────────────────────────────────────────────────────────────────
//  OLLAMA CLIENT
//  Production pattern:
//    OllamaApiClient          — OllamaSharp, the maintained provider
//    .UseLogging(factory)     — structured logs for every request/response
//    .UseFunctionInvocation() — THIS IS THE AGENTIC LOOP
//                               The middleware handles the full cycle:
//                               tool call request → execute → feed result back
//                               → next LLM call → repeat until Stop
//  Result: GetResponseAsync is called ONCE. The middleware drives the loop.
// ─────────────────────────────────────────────────────────────────────────────

IChatClient ollama = new ChatClientBuilder(
    new OllamaApiClient(new Uri("http://localhost:11434/"))
    {
        SelectedModel = "qwen2.5"
    })
    .UseLogging(loggerFactory)       // wire logs — set LogLevel.Trace above to see
    .UseFunctionInvocation()         // the agentic loop — handles everything automatically
    .Build();

// ─────────────────────────────────────────────────────────────────────────────
//  AGENT RUNNER
//  Single GetResponseAsync call — UseFunctionInvocation drives the rest.
//  The middleware calls the tool functions, appends results to the conversation,
//  re-calls the LLM, and repeats until the model returns Stop with text.
// ─────────────────────────────────────────────────────────────────────────────

async Task<string> RunAgentAsync(string userPrompt)
{
    var sep = new string('═', 55);
    Console.WriteLine($"\n{sep}");
    Console.WriteLine($"  USER: {userPrompt}");
    Console.WriteLine(sep);

    var messages = new List<ChatMessage>
    {
        new(ChatRole.System,
            """
            You are a server monitoring assistant.
            Always use tools to gather real data before answering.
            Never answer from memory or training data.
            Never call destructive tools unless the user explicitly requests it.
            Be concise. Report numbers directly.
            """),

        new(ChatRole.User, userPrompt)
    };

    var chatOptions = new ChatOptions { Tools = agentTools };

    try
    {
        // Single call — UseFunctionInvocation handles the full tool loop
        var response = await ollama.GetResponseAsync(messages, chatOptions);

        var answer = response.Text ?? "(no response)";
        Console.WriteLine($"\n  ASSISTANT: {answer}");
        return answer;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Agent failed for prompt: {Prompt}", userPrompt);
        Console.WriteLine($"\n  [ERROR] {ex.Message}");
        return $"Error: {ex.Message}";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  RUN SCENARIOS
// ─────────────────────────────────────────────────────────────────────────────

// Scenario 1 — single tool call
await RunAgentAsync(
    "What are the current server metrics? Give me CPU, memory, and uptime.");

// Scenario 2 — multi-tool chaining
await RunAgentAsync(
    "Run a full server health check. Check the current metrics, " +
    "run diagnostics, and tell me if anything needs attention.");

// Scenario 3 — write + read confirmation
await RunAgentAsync(
    "Log an audit entry saying 'Sprint 11 LLM integration verified', " +
    "then confirm by reading the current metrics.");

Console.WriteLine($"\n{new string('═', 55)}");
Console.WriteLine("  Sprint 11 complete.");
Console.WriteLine(new string('═', 55));