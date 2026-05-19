// ═══════════════════════════════════════════════════════════════════════════
//  MCP REFERENCE — MODEL-AGNOSTIC ORCHESTRATOR / CLIENT
//  ModelContextProtocol 1.2  ·  .NET 10
//
//  Packages:
//    dotnet add package ModelContextProtocol
//    dotnet add package Microsoft.Extensions.AI
//    dotnet add package OllamaSharp
//    dotnet add package Microsoft.Extensions.Logging.Console
//
//  Pre-requisites:
//    • Ollama running:  ollama serve  +  ollama pull qwen2.5
//    • McpServerHttp running on port 5100
//    • McpServerStdio built:  cd McpServerStdio && dotnet build
//
//  Features demonstrated:
//    ✓ Model-agnostic IChatClient     swap Ollama/OpenAI/Azure at one place
//    ✓ Multiple server connections    stdio + HTTP unified tool registry
//    ✓ All capability declarations    Roots · Sampling · Elicitation (Form)
//    ✓ All client handlers            RootsHandler · SamplingHandler · ElicitationHandler
//    ✓ All elicitation schema types   Boolean · String · UntitledEnum · TitledEnum
//    ✓ All notification handlers      ToolListChanged · ResourceListChanged
//                                     ResourceUpdated · PromptListChanged · LoggingMessage
//    ✓ SetLoggingLevelAsync           client controls server log verbosity
//    ✓ SubscribeToResourceAsync       full subscribe → push → unsubscribe lifecycle
//    ✓ Progress<T> on CallToolAsync   per-call progress reporting
//    ✓ Session resumption             save SessionId + ResumeSessionAsync pattern
//    ✓ Conversation history           sliding window, system prompt preserved
//    ✓ Audit logging                  per-call tool name · server · duration · outcome
//    ✓ UseFunctionInvocation          agentic loop — one GetResponseAsync call
//    ✓ Error handling                 IsError · McpProtocolException
//    ✓ Direct CallToolAsync           with and without Progress<T>
//    ✓ Resources                      list · templates · read · subscribe
//    ✓ Prompts                        list · get with args
//    ✓ Tool annotation inspection     display flags before calling
//    ✓ StandardErrorLines             capture stdio server stderr
// ═══════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OllamaSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  LOGGING
// ─────────────────────────────────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Warning));   // Trace for wire-level debugging

var logger = loggerFactory.CreateLogger("Orchestrator");

// ─────────────────────────────────────────────────────────────────────────────
//  MCP CLIENT OPTIONS — full capability declaration
//  What you declare here determines what the server is allowed to call back.
// ─────────────────────────────────────────────────────────────────────────────

var mcpOptions = new McpClientOptions
{
    ClientInfo = new Implementation { Name = "McpOrchestrator", Version = "1.0.0" },

    Capabilities = new ClientCapabilities
    {
        // Roots: expose filesystem paths to server — server calls roots/list
        Roots = new RootsCapability { ListChanged = true },

        // Sampling: server can request LLM completions — server calls sampling/createMessage
        Sampling = new SamplingCapability(),

        // Elicitation (form mode): server can ask user questions mid-tool
        Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() }
    },

    Handlers = new McpClientHandlers
    {
        // ── ROOTS — server asks what paths the client exposes ─────────────
        RootsHandler = (_, ct) =>
        {
            Console.WriteLine("  [←roots] Server requested our filesystem roots");
            return ValueTask.FromResult(new ListRootsResult
            {
                Roots =
                [
                    new Root { Uri = "file:///tmp/mcp-workspace", Name = "Workspace" },
                    new Root { Uri = "file:///tmp/mcp-data",      Name = "Data"      }
                ]
            });
        },

        // ── SAMPLING — server requests LLM completion ─────────────────────
        // In production: use your real IChatClient here.
        // Keep this client SEPARATE from the agentic client — no UseFunctionInvocation.
        SamplingHandler = async (request, progress, ct) =>
        {
            Console.WriteLine("  [←sampling] Server requested LLM completion");

            var samplingClient = new ChatClientBuilder(
                new OllamaApiClient(new Uri("http://localhost:11434/"))
                { SelectedModel = "qwen2.5" })
                .Build();  // no UseFunctionInvocation — sampling is a single completion

            var messages = request?.Messages
                .Select(m => new ChatMessage(
                    m.Role == Role.User ? ChatRole.User : ChatRole.Assistant,
                    m.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? ""))
                .ToList() ?? [];

            var response = await samplingClient.GetResponseAsync(messages, cancellationToken: ct);

            Console.WriteLine($"  [←sampling] → {response.Text?[..Math.Min(60, response.Text?.Length ?? 0)]}...");

            return new CreateMessageResult
            {
                Role = Role.Assistant,
                Content = [new TextContentBlock { Text = response.Text ?? "" }],
                Model = response.ModelId ?? "qwen2.5"
            };
        },

        // ── ELICITATION — server sends form schema, we collect user input ──
        ElicitationHandler = HandleElicitation
    }
};

// ─────────────────────────────────────────────────────────────────────────────
//  CONNECT TO SERVERS
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("Connecting to servers...\n");

// ── HTTP server (stateful) ────────────────────────────────────────────────────
var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5100/mcp"),
    TransportMode = HttpTransportMode.AutoDetect  // tries StreamableHttp, falls back to SSE
});
await using var httpClient = await McpClient.CreateAsync(httpTransport, mcpOptions);

// ── stdio server (child process) ──────────────────────────────────────────────
var stdioTransport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "StdioServer",
    Command = "dotnet",
    Arguments = ["run", "--project", "../McpServerStdio/McpServerStdio.csproj", "--no-build"],
    ShutdownTimeout = TimeSpan.FromSeconds(10),
    // Capture server's stderr — useful for debugging without corrupting the MCP stream
    StandardErrorLines = line => Console.Error.WriteLine($"[stdio-err] {line}")
});
await using var stdioClient = await McpClient.CreateAsync(stdioTransport, mcpOptions);

Console.WriteLine($"  ✓ HTTP  → {httpClient.ServerInfo?.Name}  " +
                  $"(session: {httpClient.SessionId ?? "n/a"})");
Console.WriteLine($"  ✓ stdio → {stdioClient.ServerInfo?.Name}\n");

// ── Save session for resumption ───────────────────────────────────────────────
// In production: persist to Redis or a database for crash recovery
var savedSessions = new[]
{
    new SessionSnapshot(httpClient.SessionId,   httpClient.ServerCapabilities,
                        httpClient.ServerInfo,  "HTTP"),
    new SessionSnapshot(stdioClient.SessionId,  stdioClient.ServerCapabilities,
                        stdioClient.ServerInfo, "stdio")
};

// ─────────────────────────────────────────────────────────────────────────────
//  AUDIT STATE
// ─────────────────────────────────────────────────────────────────────────────

var auditLog = new ConcurrentBag<AuditEntry>();
var toolOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

void PrintAuditLog()
{
    var entries = auditLog.ToList();
    if (entries.Count == 0) return;
    auditLog.Clear();

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"\n  ── Audit ─────────────────────────────────────────");
    Console.WriteLine($"  {"Tool",-30} {"Server",-10} {"ms",6}  Status   Preview");
    Console.WriteLine($"  {"────",-30} {"──────",-10} {"──",6}  ──────   ───────");
    foreach (var e in entries.OrderBy(x => x.ToolName))
    {
        var status = e.Success ? "✓ OK " : "✗ ERR";
        var preview = e.Preview.Length > 40 ? e.Preview[..40] + "…" : e.Preview;
        Console.WriteLine($"  {e.ToolName,-30} {e.Server,-10} {e.DurationMs,6}  {status}    {preview}");
    }
    Console.ResetColor();
}

// ─────────────────────────────────────────────────────────────────────────────
//  REGISTER NOTIFICATION HANDLERS
//  Register BEFORE any tool/resource/prompt calls — notifications are async.
// ─────────────────────────────────────────────────────────────────────────────

void RegisterNotifications(McpClient client, string label)
{
    // ── Tool list changed ─────────────────────────────────────────────────
    // Fires when: server calls SendNotificationAsync(ToolListChangedNotification)
    // Action: refresh the tool list and rebuild the registry
    client.RegisterNotificationHandler(
        NotificationMethods.ToolListChangedNotification,
        async (_, ct) =>
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            Console.WriteLine($"\n  [←{label}:ToolListChanged] {tools.Count} tools now available");
        });

    // ── Resource list changed ─────────────────────────────────────────────
    client.RegisterNotificationHandler(
        NotificationMethods.ResourceListChangedNotification,
        async (_, ct) =>
        {
            var res = await client.ListResourcesAsync(cancellationToken: ct);
            Console.WriteLine($"\n  [←{label}:ResourceListChanged] {res.Count} resources");
        });

    // ── Prompt list changed ───────────────────────────────────────────────
    client.RegisterNotificationHandler(
        NotificationMethods.PromptListChangedNotification,
        async (_, ct) =>
        {
            var prompts = await client.ListPromptsAsync(cancellationToken: ct);
            Console.WriteLine($"\n  [←{label}:PromptListChanged] {prompts.Count} prompts");
        });

    // ── Resource updated — specific resource content changed ──────────────
    // Fires when: server calls SendNotificationAsync(ResourceUpdatedNotification, { Uri: "..." })
    // Action: re-read the changed resource URI
    client.RegisterNotificationHandler(
        NotificationMethods.ResourceUpdatedNotification,
        (notification, ct) =>
        {
            if (notification.Params is not null)
            {
                var p = JsonSerializer.Deserialize<ResourceUpdatedNotificationParams>(
                    notification.Params, McpJsonUtilities.DefaultOptions);
                Console.WriteLine($"\n  [←{label}:ResourceUpdated] '{p?.Uri}' changed");
            }
            return ValueTask.CompletedTask;
        });

    // ── Server log message ────────────────────────────────────────────────
    // Fires when: server calls AsClientLoggerProvider().CreateLogger(x).LogXxx(...)
    // Level controlled by SetLoggingLevelAsync below
    client.RegisterNotificationHandler(
        NotificationMethods.LoggingMessageNotification,
        (notification, ct) =>
        {
            if (notification.Params is not null)
            {
                var log = JsonSerializer.Deserialize<LoggingMessageNotificationParams>(
                    notification.Params, McpJsonUtilities.DefaultOptions);
                if (log is not null)
                    Console.WriteLine($"\n  [←{label}:log:{log.Level}] [{log.Logger}] {log.Data}");
            }
            return ValueTask.CompletedTask;
        });
}

RegisterNotifications(httpClient, "HTTP");
RegisterNotifications(stdioClient, "stdio");

// ─────────────────────────────────────────────────────────────────────────────
//  SET SERVER LOG LEVEL
//  Client tells server what minimum log level to send via notifications/message.
//  Server must declare Logging capability (automatic in this SDK).
//  Levels: Debug · Info · Notice · Warning · Error · Critical · Alert · Emergency
// ─────────────────────────────────────────────────────────────────────────────

if (httpClient.ServerCapabilities?.Logging is not null)
{
    await httpClient.SetLoggingLevelAsync(LoggingLevel.Debug);
    Console.WriteLine("  HTTP server log level → debug");
}

if (stdioClient.ServerCapabilities?.Logging is not null)
{
    await stdioClient.SetLoggingLevelAsync(LoggingLevel.Info);
    Console.WriteLine("  stdio server log level → info\n");
}

// ─────────────────────────────────────────────────────────────────────────────
//  SERVER MANIFESTO
// ─────────────────────────────────────────────────────────────────────────────

static void PrintManifesto(McpClient client, string label)
{
    var caps = client.ServerCapabilities;
    Console.WriteLine($"\n  [{label}] {client.ServerInfo?.Name} {client.ServerInfo?.Version}");
    Console.WriteLine($"    Session:     {client.SessionId ?? "n/a"}");
    Console.WriteLine($"    Tools:       {(caps?.Tools is not null ? "✓" : "✗")}");
    Console.WriteLine($"    Resources:   {(caps?.Resources is not null ? "✓" : "✗")} " +
                      $"subscribe={caps?.Resources?.Subscribe is true}");
    Console.WriteLine($"    Prompts:     {(caps?.Prompts is not null ? "✓" : "✗")}");
    Console.WriteLine($"    Logging:     {(caps?.Logging is not null ? "✓" : "✗")}");
}

Console.WriteLine("═══ SERVER MANIFESTO ═══════════════════════════════");
PrintManifesto(httpClient, "HTTP");
PrintManifesto(stdioClient, "stdio");

// ─────────────────────────────────────────────────────────────────────────────
//  UNIFIED TOOL REGISTRY
//  One entry per tool — last writer wins on name collision (logged as warning).
//  Tool annotations are displayed so you can see what each tool declares.
// ─────────────────────────────────────────────────────────────────────────────

var allTools = new List<McpClientTool>();

async Task LoadServerTools(McpClient client, string label)
{
    var tools = await client.ListToolsAsync();
    foreach (var t in tools)
    {
        if (toolOwner.ContainsKey(t.Name))
            logger.LogWarning("Tool '{Tool}' collision — {Label} overrides", t.Name, label);
        toolOwner[t.Name] = label;
        allTools.Add(t);
    }
    Console.WriteLine($"\n  [{label}] {tools.Count} tools loaded");
}

await LoadServerTools(httpClient, "HTTP");
await LoadServerTools(stdioClient, "stdio");

Console.WriteLine($"\n  Total: {allTools.Count} tools across 2 servers");
Console.WriteLine("\n═══ TOOL REGISTRY WITH ANNOTATIONS ═════════════════");

foreach (var grp in allTools.GroupBy(t => toolOwner[t.Name]))
{
    Console.WriteLine($"\n  [{grp.Key}]");
    foreach (var t in grp)
    {
        var ann = t.ProtocolTool.Annotations;
        var flags = new List<string>();
        if (ann?.ReadOnlyHint is true) flags.Add("ReadOnly");
        if (ann?.DestructiveHint is true) flags.Add("Destructive");
        if (ann?.IdempotentHint is true) flags.Add("Idempotent");
        if (ann?.OpenWorldHint is true) flags.Add("OpenWorld");
        var tag = flags.Count > 0 ? $" [{string.Join(",", flags)}]" : "";
        Console.WriteLine($"    {t.Name,-35}{tag}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  LLM CLIENT (model-agnostic)
// ─────────────────────────────────────────────────────────────────────────────

var llm = CreateLlmClient("qwen2.5", loggerFactory);

// ─────────────────────────────────────────────────────────────────────────────
//  CONVERSATION HISTORY (shared across agent runs)
// ─────────────────────────────────────────────────────────────────────────────

var history = new ConversationHistory(windowSize: 10);
history.SetSystem(
    """
    You are a production assistant with access to multiple MCP servers.
    ALWAYS use tools for real data — never answer from memory.
    Tools marked Destructive=true require explicit user confirmation.
    Be concise. Report exact values from tool results.
    """);

// ─────────────────────────────────────────────────────────────────────────────
//  AGENT RUNNER
//  Single GetResponseAsync — UseFunctionInvocation drives the full loop.
//  After the call, inspects response.Messages to build the audit log.
// ─────────────────────────────────────────────────────────────────────────────

async Task RunAsync(string prompt, IEnumerable<string>? toolFilter = null)
{
    Console.WriteLine($"\n{"═" + new string('═', 54)}");
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"  USER: {prompt}");
    Console.ResetColor();
    Console.WriteLine(new string('═', 55));

    var scoped = (toolFilter is not null
        ? allTools.Where(t => toolFilter.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
        : allTools).Cast<AITool>().ToList();

    history.Add(new ChatMessage(ChatRole.User, prompt));

    var sw = Stopwatch.StartNew();
    try
    {
        var response = await llm.GetResponseAsync(
            history.GetWindow(),
            new ChatOptions { Tools = scoped });

        sw.Stop();

        // ── Audit: inspect which tools were called ─────────────────────────
        // UseFunctionInvocation executes tools internally and appends both
        // FunctionCallContent and FunctionResultContent to response.Messages.
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        var results = response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();

        foreach (var call in calls)
        {
            var res = results.FirstOrDefault(r => r.CallId == call.CallId || r.CallId == call.Name);
            var preview = res?.Result?.ToString() ?? "(no result)";
            auditLog.Add(new AuditEntry(
                call.Name,
                toolOwner.TryGetValue(call.Name, out var srv) ? srv : "?",
                sw.ElapsedMilliseconds / Math.Max(1, calls.Count), // approximate per-tool
                res is not null,
                preview.Length > 50 ? preview[..50] : preview));
        }

        // ── Streaming output (simulated) ───────────────────────────────────
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("\n  ASSISTANT: ");
        Console.ResetColor();
        foreach (var chunk in (response.Text ?? "(no response)").Chunk(4))
        {
            Console.Write(new string(chunk));
            await Task.Delay(8);
        }
        Console.WriteLine();

        history.AddRange(response.Messages);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Agent failed: {Prompt}", prompt);
        Console.WriteLine($"\n  [ERROR] {ex.Message}");
    }

    PrintAuditLog();
}

// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 1 — DIRECT TOOL CALLS (no LLM)
//  Demonstrates CallToolAsync with Progress<T> and error handling tiers.
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("\n\n═══ DIRECT TOOL CALLS ═══════════════════════════════");

// ── Progress<T> on CallToolAsync ─────────────────────────────────────────────
Console.WriteLine("\n[run_diagnostics — with Progress<T>]");
var diagResult = await httpClient.CallToolAsync(
    "run_diagnostics",
    new Dictionary<string, object?>(),
    progress: new Progress<ProgressNotificationValue>(v =>
        Console.WriteLine($"  [progress] {(int)(v.Progress / (v.Total ?? 1) * 100),3}% — {v.Message}")));

Console.WriteLine($"  → {diagResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text?[..80]}...");

// ── IsError — tool-level error, LLM can recover ──────────────────────────────
Console.WriteLine("\n[divide — McpException → IsError=true]");
var divResult = await stdioClient.CallToolAsync("divide",
    new Dictionary<string, object?> { ["a"] = 10.0, ["b"] = 0.0 });

if (divResult.IsError is true)
    Console.WriteLine($"  IsError=true (recoverable): " +
        $"{divResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text}");

// ── McpProtocolException — structural failure, not IsError ───────────────────
Console.WriteLine("\n[validate_input — McpProtocolException]");
try
{
    await stdioClient.CallToolAsync("validate_input",
        new Dictionary<string, object?> { ["input"] = "" });
}
catch (McpProtocolException ex)
{
    Console.WriteLine($"  McpProtocolException (structural, not recoverable): {ex.Message}");
}

// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 2 — LLM-DRIVEN TOOL CALLS
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("\n\n═══ LLM-DRIVEN SCENARIOS ════════════════════════════");

// Scenario: single server — HTTP metrics
await RunAsync("What are the current server metrics?", ["get_metrics"]);

// Scenario: single server — stdio math
await RunAsync("What is 12 multiplied by 12?", ["multiply"]);

// Scenario: cross-server — math (stdio) + metrics (HTTP) in one turn
await RunAsync(
    "Add 99 and 1, then check whether the server is healthy.",
    ["add", "get_metrics", "get_server_status"]);

// Scenario: multi-step diagnostics
await RunAsync(
    "Run a full health check and tell me what needs attention.",
    ["get_metrics", "run_diagnostics"]);

// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 3 — RESOURCES
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("\n\n═══ RESOURCES ═══════════════════════════════════════");

if (httpClient.ServerCapabilities?.Resources is not null)
{
    // ── List direct + template ────────────────────────────────────────────
    var resources = await httpClient.ListResourcesAsync();
    var templates = await httpClient.ListResourceTemplatesAsync();

    Console.WriteLine($"\n  Direct ({resources.Count}):   " +
                      string.Join(", ", resources.Select(r => r.Name)));
    Console.WriteLine($"  Templates ({templates.Count}): " +
                      string.Join(", ", templates.Select(t => t.UriTemplate)));

    // ── Read a direct resource ────────────────────────────────────────────
    Console.WriteLine("\n  [reading config://app/settings]");
    var config = await httpClient.ReadResourceAsync("config://app/settings");
    foreach (var c in config.Contents.OfType<TextResourceContents>())
        Console.WriteLine($"  {c.Text?[..Math.Min(120, c.Text?.Length ?? 0)]}...");

    // ── Read a template resource (fill in the URI parameter) ──────────────
    Console.WriteLine("\n  [reading user://profile/42]");
    var profile = await httpClient.ReadResourceAsync("user://profile/42");
    foreach (var c in profile.Contents.OfType<TextResourceContents>())
        Console.WriteLine($"  {c.Text}");

    // ── Subscribe to resource updates — full lifecycle ─────────────────────
    // 1. Read first to seed ConnectionRegistry (background worker needs it)
    // 2. Subscribe with callback
    // 3. Wait for push notification from background worker (every 5s)
    // 4. IAsyncDisposable cleanly unsubscribes on await using exit
    if (httpClient.ServerCapabilities.Resources.Subscribe is true)
    {
        Console.WriteLine("\n  [subscribing to metrics://server/live — waiting ≤12s]");

        await httpClient.ReadResourceAsync("metrics://server/live"); // seed registry

        var received = new TaskCompletionSource<bool>();

        await using var sub = await httpClient.SubscribeToResourceAsync(
            "metrics://server/live",
            async (notification, ct) =>
            {
                Console.WriteLine($"  [←push] '{notification.Uri}' updated — re-reading...");
                var updated = await httpClient.ReadResourceAsync(notification.Uri, cancellationToken: ct);
                foreach (var c in updated.Contents.OfType<TextResourceContents>())
                    Console.WriteLine($"  {c.Text?[..Math.Min(80, c.Text?.Length ?? 0)]}...");
                received.TrySetResult(true);
            });

        await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(12)));

        if (!received.Task.IsCompleted)
            Console.WriteLine("  [timeout] Server may not have pushed yet — check background worker");

        Console.WriteLine("  [unsubscribed — IAsyncDisposable fired on await using exit]");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 4 — PROMPTS
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("\n\n═══ PROMPTS ═════════════════════════════════════════");

if (httpClient.ServerCapabilities?.Prompts is not null)
{
    var prompts = await httpClient.ListPromptsAsync();
    Console.WriteLine($"\n  Available prompts ({prompts.Count}):");
    foreach (var p in prompts)
    {
        var promptArgs = p.ProtocolPrompt.Arguments is { Count: > 0 }
            ? $" (args: {string.Join(", ", p.ProtocolPrompt.Arguments.Select(a => a.Name))})"
            : "";
        Console.WriteLine($"    {p.Name}: {p.Description}{promptArgs}");
    }

    // ── Prompt with no args ───────────────────────────────────────────────
    Console.WriteLine("\n  [getting 'system_init']");
    var init = await httpClient.GetPromptAsync("system_init");
    foreach (var msg in init.Messages)
        if (msg.Content is TextContentBlock t)
            Console.WriteLine($"  [{msg.Role}]: {t.Text?[..Math.Min(80, t.Text?.Length ?? 0)]}...");

    // ── Prompt with required arg ──────────────────────────────────────────
    Console.WriteLine("\n  [getting 'health_summary' window='1h']");
    var summary = await httpClient.GetPromptAsync("health_summary",
        new Dictionary<string, object?> { ["window"] = "1h" });
    foreach (var msg in summary.Messages)
        if (msg.Content is TextContentBlock t)
            Console.WriteLine($"  [{msg.Role}]: {t.Text}");
}

// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 5 — ELICITATION + SAMPLING via LLM
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("\n\n═══ ELICITATION (you will be prompted) ══════════════");
await RunAsync("Deploy the 'api-gateway' service using deploy_service.", ["deploy_service"]);

Console.WriteLine("\n\n═══ SAMPLING (server asks client to run LLM) ════════");
await RunAsync("Ask the LLM what MCP stands for using ask_llm.", ["ask_llm"]);

// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 6 — CONVERSATION MEMORY
//  The sliding window history persists across all RunAsync calls.
//  This final prompt references earlier results with no tool calls.
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("\n\n═══ CONVERSATION MEMORY ═════════════════════════════");
await RunAsync("Based on everything we've done, summarise server health in one sentence.");

// ─────────────────────────────────────────────────────────────────────────────
//  SECTION 7 — SESSION RESUMPTION PATTERN
//  After saving the session snapshot, you can reconnect without repeating
//  the full handshake. Use this after a transient network failure.
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("\n\n═══ SESSION RESUMPTION PATTERN ══════════════════════");

foreach (var session in savedSessions.Where(s => s.SessionId is not null))
{
    Console.WriteLine($"  [{session.ServerLabel}] Saved session: {session.SessionId}");
    Console.WriteLine("  Demonstrating resumption (connects then immediately disposes)...");

    try
    {
        var resumeTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost:5100/mcp"),
            KnownSessionId = session.SessionId
        });

        await using var resumed = await McpClient.ResumeSessionAsync(
            resumeTransport,
            new ResumeClientSessionOptions
            {
                ServerCapabilities = session.Capabilities!,
                ServerInfo = session.ServerInfo!
            });

        Console.WriteLine($"  ✓ Session resumed — server: {resumed.ServerInfo?.Name}");

        var tools = await resumed.ListToolsAsync();
        Console.WriteLine($"  ✓ Tool list refreshed: {tools.Count} tools (no re-handshake)");
    }
    catch (Exception ex)
    {
        // stdio has no SessionId — skip gracefully
        logger.LogDebug("Resume skipped for {Label}: {Message}", session.ServerLabel, ex.Message);
        Console.WriteLine($"  [skipped — {ex.Message[..Math.Min(60, ex.Message.Length)]}]");
    }
}
// ─────────────────────────────────────────────────────────────────────────────
//  DONE
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine($"\n{"═" + new string('═', 54)}");
Console.WriteLine($"  Complete. {history.TotalMessages} messages · {auditLog.Count} tool calls logged");
Console.WriteLine(new string('═', 55));

// httpClient + stdioClient disposed automatically via await using
// stdioClient.DisposeAsync() sends SIGTERM + waits ShutdownTimeout (10s)
// httpClient.DisposeAsync()  closes HTTP session gracefully

// ─────────────────────────────────────────────────────────────────────────────
//  MODEL-AGNOSTIC CHAT CLIENT FACTORY
//  Change the provider here — everything downstream uses IChatClient.
//  No provider-specific code exists anywhere else in this file.
// ─────────────────────────────────────────────────────────────────────────────

IChatClient CreateLlmClient(string modelId, ILoggerFactory lf)
{
    // ── Ollama (local, free) ───────────────────────────────────────────────
    return new ChatClientBuilder(
        new OllamaApiClient(new Uri("http://localhost:11434/")) { SelectedModel = modelId })
        .UseLogging(lf)
        .UseFunctionInvocation()   // handles the full tool-call → execute → feed back loop
        .Build();

    // ── OpenAI ────────────────────────────────────────────────────────────
    // return new ChatClientBuilder(
    //     new OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY")!)
    //         .GetChatClient(modelId))
    //     .UseLogging(lf).UseFunctionInvocation().Build();

    // ── Azure OpenAI ──────────────────────────────────────────────────────
    // return new ChatClientBuilder(
    //     new AzureOpenAIClient(new Uri(Environment.GetEnvironmentVariable("AZURE_ENDPOINT")!),
    //         new Azure.Identity.DefaultAzureCredential()).GetChatClient(modelId))
    //     .UseLogging(lf).UseFunctionInvocation().Build();
}

// ─────────────────────────────────────────────────────────────────────────────
//  ELICITATION HANDLER — handles every schema type the server can send
// ─────────────────────────────────────────────────────────────────────────────

ValueTask<ElicitResult> HandleElicitation(
    ElicitRequestParams? request,
    CancellationToken ct)
{
    if (request?.RequestedSchema?.Properties is null or { Count: 0 })
        return ValueTask.FromResult(new ElicitResult { Action = "decline" });

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n  ╔═ ELICITATION ══════════════════════════════════");
    Console.WriteLine($"  ║  {request.Message}");
    Console.WriteLine($"  ╚════════════════════════════════════════════════");
    Console.ResetColor();

    var content = new Dictionary<string, JsonElement>();

    foreach (var (key, schema) in request.RequestedSchema.Properties)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;

        switch (schema)
        {
            case ElicitRequestParams.BooleanSchema b:
                Console.Write($"  {b.Description ?? key} (true/false): ");
                Console.ResetColor();
                var bi = Console.ReadLine()?.Trim().ToLower();
                if (bi is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = ToJson(bi is "true" or "yes" or "y" or "1");
                break;

            case ElicitRequestParams.StringSchema s:
                Console.Write($"  {s.Description ?? key}: ");
                Console.ResetColor();
                var si = Console.ReadLine()?.Trim();
                if (si is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = ToJson(si);
                break;

            case ElicitRequestParams.NumberSchema n:
                Console.Write($"  {n.Description ?? key} (number): ");
                Console.ResetColor();
                if (double.TryParse(Console.ReadLine()?.Trim(), out var num))
                    content[key] = ToJson(num);
                break;

            // UntitledSingleSelectEnum: enum values are also the display text
            case ElicitRequestParams.UntitledSingleSelectEnumSchema u:
                Console.Write($"  {u.Description ?? key} [{string.Join(", ", u.Enum ?? [])}]: ");
                Console.ResetColor();
                var ui = Console.ReadLine()?.Trim();
                if (ui is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = ToJson(ui);
                break;

            // TitledSingleSelectEnum: const (stored value) ≠ title (display text)
            case ElicitRequestParams.TitledSingleSelectEnumSchema t:
                Console.WriteLine($"  {t.Description ?? key}:");
                if (t.OneOf is not null)
                    foreach (var opt in t.OneOf)
                        Console.WriteLine($"    {opt.Const,10} — {opt.Title}");
                Console.Write("  Enter value (const): ");
                Console.ResetColor();
                var ti = Console.ReadLine()?.Trim();
                if (ti is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = ToJson(ti);
                break;

            default:
                Console.Write($"  {key}: ");
                Console.ResetColor();
                var ri = Console.ReadLine()?.Trim();
                if (ri is null) return ValueTask.FromResult(new ElicitResult { Action = "cancel" });
                content[key] = ToJson(ri);
                break;
        }
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("  ✓ Accepted — returning to server\n");
    Console.ResetColor();
    return ValueTask.FromResult(new ElicitResult { Action = "accept", Content = content });

    static JsonElement ToJson<T>(T v)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(v));
}


// ─────────────────────────────────────────────────────────────────────────────
//  TYPES — must precede top-level statements in C# top-level programs
// ─────────────────────────────────────────────────────────────────────────────

// ── Audit record — captured for every tool call ───────────────────────────────
record AuditEntry(
    string ToolName,
    string Server,
    long DurationMs,
    bool Success,
    string Preview);

// ── Session snapshot — saved for potential resumption ─────────────────────────
record SessionSnapshot(
    string? SessionId,
    ServerCapabilities? Capabilities,
    Implementation? ServerInfo,
    string ServerLabel);

// ── Conversation history with sliding window ──────────────────────────────────
// System message is always preserved regardless of window size.
class ConversationHistory(int windowSize = 10)
{
    private readonly List<ChatMessage> _messages = [];
    private ChatMessage? _system;

    public void SetSystem(string prompt)
        => _system = new ChatMessage(ChatRole.System, prompt);

    public void Add(ChatMessage m)
        => _messages.Add(m);

    public void AddRange(IEnumerable<ChatMessage> ms)
        => _messages.AddRange(ms);

    public int TotalMessages => _messages.Count;

    // Returns system + last N message pairs — stays within model context limits
    public List<ChatMessage> GetWindow()
    {
        var window = _messages.TakeLast(windowSize * 2).ToList();
        if (_system is not null) window.Insert(0, _system);
        return window;
    }
}

