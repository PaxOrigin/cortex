using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OllamaSharp;
using System.Diagnostics;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  LOGGING
// ─────────────────────────────────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Warning));

var logger = loggerFactory.CreateLogger("Orchestrator");

// ─────────────────────────────────────────────────────────────────────────────
//  ELICITATION HANDLER
// ─────────────────────────────────────────────────────────────────────────────

static ValueTask<ElicitResult> HandleElicitationAsync(
    ElicitRequestParams? request,
    CancellationToken ct)
{
    if (request?.RequestedSchema?.Properties is null or { Count: 0 })
        return ValueTask.FromResult(new ElicitResult { Action = "decline" });

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n  ┌─ ELICITATION REQUEST ─────────────────────────");
    Console.WriteLine($"  │  {request.Message}");
    Console.WriteLine($"  └───────────────────────────────────────────────");
    Console.ResetColor();

    var content = new Dictionary<string, JsonElement>();

    foreach (var (key, schema) in request.RequestedSchema.Properties)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;

        switch (schema)
        {
            case ElicitRequestParams.BooleanSchema boolSchema:
                Console.Write($"  {boolSchema.Description ?? key} (true/false): ");
                Console.ResetColor();
                var boolInput = Console.ReadLine()?.Trim().ToLower();
                if (boolInput is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(boolInput is "true" or "yes" or "y" or "1"));
                break;

            case ElicitRequestParams.StringSchema strSchema:
                Console.Write($"  {strSchema.Description ?? key}: ");
                Console.ResetColor();
                var strInput = Console.ReadLine()?.Trim();
                if (strInput is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(strInput));
                break;

            case ElicitRequestParams.NumberSchema numSchema:
                Console.Write($"  {numSchema.Description ?? key} (number): ");
                Console.ResetColor();
                var numInput = Console.ReadLine()?.Trim();
                if (double.TryParse(numInput, out var num))
                    content[key] = JsonSerializer.Deserialize<JsonElement>(
                        JsonSerializer.Serialize(num));
                break;

            case ElicitRequestParams.UntitledSingleSelectEnumSchema enumSchema:
                var opts = enumSchema.Enum ?? [];
                Console.Write($"  {enumSchema.Description ?? key} [{string.Join(", ", opts)}]: ");
                Console.ResetColor();
                var enumInput = Console.ReadLine()?.Trim();
                if (enumInput is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(enumInput));
                break;

            default:
                Console.Write($"  {key}: ");
                Console.ResetColor();
                var rawInput = Console.ReadLine()?.Trim();
                if (rawInput is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(rawInput));
                break;
        }
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("  ✓ Elicitation accepted — returning to server\n");
    Console.ResetColor();

    return ValueTask.FromResult(new ElicitResult { Action = "accept", Content = content });
}

// ─────────────────────────────────────────────────────────────────────────────
//  AUDIT LOG
// ─────────────────────────────────────────────────────────────────────────────

var auditLog = new List<AuditEntry>();

void PrintAuditLog()
{
    if (auditLog.Count == 0) return;

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"\n  ── Tool Audit Log ──────────────────────────────────");
    Console.WriteLine($"  {"Tool",-30} {"Server",-14} {"ms",6}  {"Status",-8}  Preview");
    Console.WriteLine($"  {"────",-30} {"──────",-14} {"──",6}  {"──────",-8}  ───────");

    foreach (var e in auditLog)
    {
        var status = e.Success ? "✓ OK" : "✗ ERR";
        var preview = e.ResultPreview.Length > 40
            ? e.ResultPreview[..40] + "..."
            : e.ResultPreview;
        Console.WriteLine(
            $"  {e.ToolName,-30} {e.ServerName,-14} {e.LatencyMs,6}  {status,-8}  {preview}");
    }

    Console.ResetColor();
    auditLog.Clear();
}

// ─────────────────────────────────────────────────────────────────────────────
//  SERVER REGISTRY + CONNECT
// ─────────────────────────────────────────────────────────────────────────────

var serverDefinitions = new[]
{
    new ServerDefinition("HttpServer",
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint      = new Uri("http://localhost:5100/mcp"),
            TransportMode = HttpTransportMode.AutoDetect
        })),

    new ServerDefinition("StdioServer",
        new StdioClientTransport(new StdioClientTransportOptions
        {
            Name      = "McpServerDotNet",
            Command   = "dotnet",
            Arguments = ["run", "--project",
                         "../McpServerDotNet/McpServerDotNet.csproj",
                         "--no-build"]
        }))
};

Console.WriteLine("Connecting to all servers...\n");

var allTools = new List<McpClientTool>();
var toolOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var clients = new List<McpClient>();

var mcpClientOptions = new McpClientOptions
{
    ClientInfo = new Implementation { Name = "McpOrchestrator", Version = "1.0.0" },
    Capabilities = new ClientCapabilities
    {
        Roots = new RootsCapability { ListChanged = true },
        Sampling = new SamplingCapability(),
        Elicitation = new ElicitationCapability
        {
            Form = new FormElicitationCapability()
        }
    },
    Handlers = new McpClientHandlers
    {
        RootsHandler = (_, ct) => ValueTask.FromResult(new ListRootsResult
        {
            Roots = [new Root { Uri = "file:///tmp/mcp-workspace", Name = "MCP Workspace" }]
        }),
        ElicitationHandler = HandleElicitationAsync
    }
};

foreach (var def in serverDefinitions)
{
    try
    {
        var mcpClient = await McpClient.CreateAsync(def.Transport, mcpClientOptions);
        var serverTools = await mcpClient.ListToolsAsync();

        clients.Add(mcpClient);
        Console.WriteLine($"  ✓ {def.Name,-14} {mcpClient.ServerInfo?.Name} ({serverTools.Count} tools)");

        foreach (var tool in serverTools)
        {
            if (toolOwner.ContainsKey(tool.Name))
                logger.LogWarning("Tool '{Tool}' collision — {Server} overrides", tool.Name, def.Name);

            toolOwner[tool.Name] = def.Name;
            allTools.Add(tool);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to connect to {Server}", def.Name);
        Console.WriteLine($"  ✗ {def.Name,-14} FAILED: {ex.Message}");
    }
}

Console.WriteLine($"\n  Unified registry: {allTools.Count} tools across {clients.Count} servers\n");

// ─────────────────────────────────────────────────────────────────────────────
//  OLLAMA CLIENT
//
//  UseFunctionInvocation IS the agentic loop — confirmed working pattern.
//  The middleware callback intercepts each tool call for audit logging
//  before executing it. This gives us observability without a manual loop.
// ─────────────────────────────────────────────────────────────────────────────

IChatClient ollama = new ChatClientBuilder(
    new OllamaApiClient(new Uri("http://localhost:11434/"))
    {
        SelectedModel = "qwen2.5"
    })
    .UseLogging(loggerFactory)
    .UseFunctionInvocation()
    .Use(inner => new AuditingChatClient(inner, auditLog, toolOwner))
    .Build();

// ─────────────────────────────────────────────────────────────────────────────
//  AGENT RUNNER
//  Single GetResponseAsync call — UseFunctionInvocation drives the full loop:
//    tool call request → execute (via middleware) → feed back → repeat → Stop
// ─────────────────────────────────────────────────────────────────────────────

async Task RunAgentAsync(
    string userPrompt,
    ConversationHistory history,
    IEnumerable<string>? toolFilter = null)
{
    var sep = new string('═', 55);
    Console.WriteLine($"\n{sep}");
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"  USER: {userPrompt}");
    Console.ResetColor();
    Console.WriteLine(sep);

    var scopedTools = (toolFilter is not null
        ? allTools.Where(t => toolFilter.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
        : allTools).ToList();

    history.Add(new ChatMessage(ChatRole.User, userPrompt));

    var chatOptions = new ChatOptions { Tools = scopedTools.Cast<AITool>().ToList() };

    try
    {
        var response = await ollama.GetResponseAsync(history.GetWindow(), chatOptions);

        // Simulate streaming — chunks of 4 chars with small delay
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  ASSISTANT: ");
        Console.ResetColor();

        foreach (var chunk in (response.Text ?? "(no response)").Chunk(4))
        {
            Console.Write(new string(chunk));
            await Task.Delay(10);
        }

        Console.WriteLine();
        history.AddRange(response.Messages);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Agent failed for: {Prompt}", userPrompt);
        Console.WriteLine($"\n  [ERROR] {ex.Message}");
    }

    PrintAuditLog();
}

// ─────────────────────────────────────────────────────────────────────────────
//  CONVERSATION HISTORY
// ─────────────────────────────────────────────────────────────────────────────

var history = new ConversationHistory(windowSize: 10);
history.SetSystem(
    """
    You are a production server monitoring assistant with access to multiple backend servers.
    ALWAYS use tools to answer. NEVER guess or compute from memory.
    For math call add or multiply.
    For server data call get_metrics, get_server_status, or run_diagnostics.
    Be concise and report exact tool output values.
    """);

// ─────────────────────────────────────────────────────────────────────────────
//  SCENARIOS
// ─────────────────────────────────────────────────────────────────────────────

// 1 — HTTP server: metrics
await RunAgentAsync("What are the current CPU and memory usage?",
    history, ["get_metrics"]);

// 2 — Stdio server: math
await RunAgentAsync("What is 12 multiplied by 12?",
    history, ["multiply"]);

// 3 — Cross-server: math + metrics
await RunAgentAsync("Calculate 99 + 1, then check if the server is healthy.",
    history, ["add", "get_metrics", "get_server_status"]);

// 4 — Multi-step diagnostics
await RunAgentAsync("Run a full health check and tell me what needs attention.",
    history, ["get_metrics", "run_diagnostics"]);

// 5 — Elicitation: DeployService
Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine("\n  [elicitation scenario — you will be prompted for input]");
Console.ResetColor();

await RunAgentAsync("Deploy the 'api-gateway' service using the deploy_service tool.",
    history, ["deploy_service"]);

// 6 — Elicitation: GenerateReport
await RunAgentAsync("Generate a server report using the generate_report tool.",
    history, ["generate_report"]);

// 7 — Conversation memory: no tools, uses history context
await RunAgentAsync("Summarise the server health in one sentence based on what we found.",
    history);

// ─────────────────────────────────────────────────────────────────────────────
//  CLEANUP
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine($"\n{new string('═', 55)}");
Console.WriteLine($"  Final sprint complete.");
Console.WriteLine($"  History: {history.TotalMessages} messages across {clients.Count} servers");
Console.WriteLine(new string('═', 55));

foreach (var c in clients)
    await c.DisposeAsync();

// ─────────────────────────────────────────────────────────────────────────────
//  TYPES
// ─────────────────────────────────────────────────────────────────────────────

record ServerDefinition(string Name, IClientTransport Transport);

record AuditEntry(
    string ToolName,
    string ServerName,
    long LatencyMs,
    bool Success,
    string ResultPreview);

class ConversationHistory(int windowSize = 10)
{
    private readonly List<ChatMessage> _messages = [];
    private ChatMessage? _system;

    public void SetSystem(string content)
        => _system = new ChatMessage(ChatRole.System, content);

    public void Add(ChatMessage message)
        => _messages.Add(message);

    public void AddRange(IEnumerable<ChatMessage> messages)
        => _messages.AddRange(messages);

    public List<ChatMessage> GetWindow()
    {
        var window = _messages.TakeLast(windowSize * 2).ToList();
        if (_system is not null) window.Insert(0, _system);
        return window;
    }

    public int TotalMessages => _messages.Count;
}

// Add this class at the bottom with your other types
class AuditingChatClient(
    IChatClient inner,
    List<AuditEntry> auditLog,
    Dictionary<string, string> toolOwner)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var response = await base.GetResponseAsync(messages, options, ct);

        foreach (var msg in response.Messages)
            foreach (var call in msg.Contents.OfType<FunctionCallContent>())
            {
                var sw = Stopwatch.StartNew();
                var serverName = toolOwner.TryGetValue(call.Name, out var sn) ? sn : "unknown";

                // find the matching result in the same response
                var result = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>()
                    .FirstOrDefault(r => r.CallId == call.CallId);

                sw.Stop();

                auditLog.Add(new AuditEntry(
                    call.Name, serverName, sw.ElapsedMilliseconds,
                    result is not null,
                    result?.Result?.ToString()?[..Math.Min(60,
                        result.Result?.ToString()?.Length ?? 0)] ?? "(no result)"));
            }

        return response;
    }
}