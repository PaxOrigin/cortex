using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  TRANSPORT
// ─────────────────────────────────────────────────────────────────────────────

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5100/mcp"),
    TransportMode = HttpTransportMode.AutoDetect
});

// ─────────────────────────────────────────────────────────────────────────────
//  CLIENT OPTIONS — full capability declaration
//  Senior concept: what you declare here shapes what the server is allowed
//  to do back to you. Undeclared capabilities = server cannot use them.
// ─────────────────────────────────────────────────────────────────────────────

var options = new McpClientOptions
{
    ClientInfo = new Implementation { Name = "McpHostHttp", Version = "1.0.0" },

    Capabilities = new ClientCapabilities
    {
        Roots = new RootsCapability { ListChanged = true },
        Sampling = new SamplingCapability()
    },

    Handlers = new McpClientHandlers
    {
        // ── ROOTS HANDLER ──────────────────────────────────────────────────
        // Server calls roots/list → this fires.
        // Declare which filesystem locations the server may work with.
        RootsHandler = (request, ct) =>
        {
            Console.WriteLine("\n  [←roots] Server requested our roots");
            return ValueTask.FromResult(new ListRootsResult
            {
                Roots =
                [
                    new Root { Uri = "file:///tmp/mcp-workspace", Name = "MCP Workspace" },
                    new Root { Uri = "file:///tmp/mcp-data",      Name = "MCP Data"      }
                ]
            });
        },

        // ── SAMPLING HANDLER ───────────────────────────────────────────────
        // Server calls sampling/createMessage → this fires.
        // The server hands us messages; we run the LLM and return the result.
        // Sprint 11: replace simulation with a real IChatClient.
        SamplingHandler = (request, progress, ct) =>
        {
            var question = request?.Messages
                .LastOrDefault(m => m.Role == Role.User)
                ?.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
                ?? "(no question)";

            Console.WriteLine($"\n  [←sampling] Server sent LLM request: \"{question}\"");

            var answer =
                $"[Simulated LLM] You asked: '{question}'. " +
                $"Sprint 11 replaces this with a real model.";

            Console.WriteLine($"  [←sampling] Returning simulated answer");

            return ValueTask.FromResult(new CreateMessageResult
            {
                Role = Role.Assistant,
                Content = [new TextContentBlock { Text = answer }],
                Model = "simulated-1.0"
            });
        }
    }
};

// ─────────────────────────────────────────────────────────────────────────────
//  CONNECT
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("Connecting to HTTP MCP server...\n");
await using var client = await McpClient.CreateAsync(transport, options);
Console.WriteLine("Connected!\n");

// ─────────────────────────────────────────────────────────────────────────────
//  SENIOR FEATURE 1 — SERVER-TO-CLIENT LOG HANDLER
//  The server can push log lines over notifications/message.
//  Register the handler BEFORE calling any tools so no logs are missed.
//  This is the client-side complement to server.AsClientLoggerProvider().
// ─────────────────────────────────────────────────────────────────────────────

client.RegisterNotificationHandler(
    NotificationMethods.LoggingMessageNotification,
    (notification, ct) =>
    {
        if (notification.Params is not null)
        {
            var log = JsonSerializer.Deserialize<LoggingMessageNotificationParams>(
                notification.Params, McpJsonUtilities.DefaultOptions);
            if (log is not null)
            {
                var level = log.Level.ToString().ToUpper();
                Console.WriteLine($"  [server-log:{level}] [{log.Logger}] {log.Data}");
            }
        }
        return ValueTask.CompletedTask;
    });

// ─────────────────────────────────────────────────────────────────────────────
//  SENIOR FEATURE 2 — RESOURCE UPDATE HANDLER
//  Fires when the server pushes notifications/resources/updated.
//  The background worker sends this every 5 seconds for metrics://server/live.
// ─────────────────────────────────────────────────────────────────────────────

client.RegisterNotificationHandler(
    NotificationMethods.ResourceUpdatedNotification,
    async (notification, ct) =>
    {
        if (notification.Params is not null)
        {
            var p = JsonSerializer.Deserialize<ResourceUpdatedNotificationParams>(
                notification.Params, McpJsonUtilities.DefaultOptions);
            if (p is not null)
                Console.WriteLine($"\n  [←resource-update] '{p.Uri}' changed — re-fetching...");
        }
        await ValueTask.CompletedTask;
    });

// ─────────────────────────────────────────────────────────────────────────────
//  SERVER MANIFESTO
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine("══════════════════════════════════════════════════════");
Console.WriteLine("  SERVER MANIFESTO");
Console.WriteLine("══════════════════════════════════════════════════════");
Console.WriteLine($"  Name:      {client.ServerInfo?.Name}");
Console.WriteLine($"  Version:   {client.ServerInfo?.Version}");

// Senior feature: SessionId is unique per stateful HTTP connection.
// Log it for debugging multi-client scenarios and session resumption.
Console.WriteLine($"  SessionId: {client.SessionId ?? "n/a (stateless)"}");
Console.WriteLine();
Console.WriteLine($"  Tools:     {(client.ServerCapabilities?.Tools is not null ? "✓" : "✗")}");
Console.WriteLine($"  Resources: {(client.ServerCapabilities?.Resources is not null ? "✓" : "✗")}");
Console.WriteLine($"  Prompts:   {(client.ServerCapabilities?.Prompts is not null ? "✓" : "✗")}");
Console.WriteLine($"  Logging:   {(client.ServerCapabilities?.Logging is not null ? "✓" : "✗")}");

Console.WriteLine("\n  Client Capabilities Declared:");
Console.WriteLine($"    Roots:    {(options.Capabilities?.Roots is not null ? "✓" : "✗")}");
Console.WriteLine($"    Sampling: {(options.Capabilities?.Sampling is not null ? "✓" : "✗")}");

// ─────────────────────────────────────────────────────────────────────────────
//  SENIOR FEATURE 3 — TOOL ANNOTATION INSPECTION
//  Before calling any tool, a production host should inspect annotations.
//  This drives UI decisions: auto-approve, confirm, or block.
//  Destructive=true → show a confirmation dialog in real UIs.
//  ReadOnly=true    → safe to auto-approve and retry.
// ─────────────────────────────────────────────────────────────────────────────

if (client.ServerCapabilities?.Tools is not null)
{
    var tools = await client.ListToolsAsync();

    Console.WriteLine($"\n══════════════════════════════════════════════════════");
    Console.WriteLine($"  TOOL REGISTRY WITH ANNOTATIONS ({tools.Count} tools)");
    Console.WriteLine($"══════════════════════════════════════════════════════");

    foreach (var tool in tools)
    {
        var ann = tool.ProtocolTool.Annotations;
        var ro = ann?.ReadOnlyHint is true ? "ReadOnly" : null;
        var des = ann?.DestructiveHint is true ? "Destructive" : null;
        var ide = ann?.IdempotentHint is true ? "Idempotent" : null;
        var ow = ann?.OpenWorldHint is true ? "OpenWorld" : null;
        var tags = string.Join(", ", new[] { ro, des, ide, ow }.Where(t => t is not null));

        Console.WriteLine($"  {tool.Name,-35} [{(string.IsNullOrEmpty(tags) ? "no annotations" : tags)}]");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  INFO TOOLS (Sprint 10)
    // ─────────────────────────────────────────────────────────────────────

    Console.WriteLine($"\n══════════════════════════════════════════════════════");
    Console.WriteLine($"  INFO TOOLS");
    Console.WriteLine($"══════════════════════════════════════════════════════");

    Console.WriteLine("\n[who_am_i]");
    var who = await client.CallToolAsync("who_am_i", new Dictionary<string, object?>());
    Console.WriteLine($"  {who.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n  ")}");

    Console.WriteLine("\n[get_client_roots]");
    var roots = await client.CallToolAsync("get_client_roots", new Dictionary<string, object?>());
    Console.WriteLine($"  {roots.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n  ")}");

    Console.WriteLine("\n[ask_client]");
    var ask = await client.CallToolAsync("ask_client",
        new Dictionary<string, object?> { ["question"] = "What is the Model Context Protocol?" });
    Console.WriteLine($"  {ask.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n  ")}");

    // ─────────────────────────────────────────────────────────────────────
    //  ANNOTATED TOOLS + ERROR TIER DEMO
    //  Senior concept: two distinct error tiers.
    //    IsError=true   → tool-level error, LLM can recover and retry
    //    Exception      → protocol error, something is structurally broken
    // ─────────────────────────────────────────────────────────────────────

    Console.WriteLine($"\n══════════════════════════════════════════════════════");
    Console.WriteLine($"  ANNOTATED TOOLS + ERROR HANDLING");
    Console.WriteLine($"══════════════════════════════════════════════════════");

    // ReadOnly=true — safe to auto-approve
    Console.WriteLine("\n[get_metrics]  ← ReadOnly=true, auto-approvable");
    var metrics = await client.CallToolAsync("get_metrics", new Dictionary<string, object?>());
    Console.WriteLine($"  {metrics.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n  ")}");

    // Destructive=false — additive write
    Console.WriteLine("\n[append_audit_log]  ← Destructive=false, additive");
    var audit = await client.CallToolAsync("append_audit_log",
        new Dictionary<string, object?> { ["message"] = "Host connected and verified all tools" });
    Console.WriteLine($"  {audit.Content.OfType<TextContentBlock>().First().Text}");

    // Destructive=true — warn before calling
    Console.WriteLine("\n[reset_metrics]  ← Destructive=true — in a real UI: show confirmation");
    Console.WriteLine("  [host] Annotation check: Destructive=true. Proceeding for demo only.");
    var reset = await client.CallToolAsync("reset_metrics", new Dictionary<string, object?>());
    Console.WriteLine($"  {reset.Content.OfType<TextContentBlock>().First().Text}");

    // Tool-level error (IsError) vs protocol exception
    Console.WriteLine("\n[error tier demo]");
    try
    {
        // Protocol error: tool does not exist → throws McpProtocolException
        var bad = await client.CallToolAsync("nonexistent_tool", new Dictionary<string, object?>());

        // If it returns instead of throwing (IsError=true path)
        if (bad.IsError is true)
        {
            var msg = bad.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            Console.WriteLine($"  [IsError=true — tool returned error, LLM can recover]: {msg}");
        }
    }
    catch (McpProtocolException ex)
    {
        Console.WriteLine($"  [McpProtocolException — protocol level, not recoverable]: {ex.Message}");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  LOGGING TOOL — server sends logs to client over MCP
    // ─────────────────────────────────────────────────────────────────────

    Console.WriteLine($"\n══════════════════════════════════════════════════════");
    Console.WriteLine($"  SERVER-TO-CLIENT LOGGING");
    Console.WriteLine($"══════════════════════════════════════════════════════");

    Console.WriteLine("\n[run_with_client_logging]");
    var logging = await client.CallToolAsync("run_with_client_logging",
        new Dictionary<string, object?> { ["label"] = "sprint-10-demo" });
    Console.WriteLine($"  → {logging.Content.OfType<TextContentBlock>().First().Text}");

    // ─────────────────────────────────────────────────────────────────────
    //  STRUCTURED OUTPUT + PROGRESS
    // ─────────────────────────────────────────────────────────────────────

    Console.WriteLine($"\n══════════════════════════════════════════════════════");
    Console.WriteLine($"  STRUCTURED OUTPUT + PROGRESS");
    Console.WriteLine($"══════════════════════════════════════════════════════");

    Console.WriteLine("\n[get_server_status]  ← structured JSON record");
    var status = await client.CallToolAsync("get_server_status", new Dictionary<string, object?>());
    Console.WriteLine($"  {status.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n  ")}");

    Console.WriteLine("\n[run_diagnostics]  ← progress + structured result");
    var diag = await client.CallToolAsync(
        "run_diagnostics",
        new Dictionary<string, object?>(),
        progress: new Progress<ProgressNotificationValue>(v =>
            Console.WriteLine($"  [progress] {(int)(v.Progress / (v.Total ?? 1) * 100),3}% — {v.Message}"))
    );
    Console.WriteLine($"  Result:\n  {diag.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n  ")}");

    // Basic math tool
    Console.WriteLine("\n[add]  ← confirming HTTP transport works");
    var add = await client.CallToolAsync("add",
        new Dictionary<string, object?> { ["a"] = 99, ["b"] = 1 });
    Console.WriteLine($"  add(99, 1) = {add.Content.OfType<TextContentBlock>().First().Text}");
}

// ─────────────────────────────────────────────────────────────────────────────
//  RESOURCES + LIVE SUBSCRIPTION
//  Senior concept: subscribe → wait for push → unsubscribe.
//  This is the full subscription lifecycle — critical for reactive agents.
// ─────────────────────────────────────────────────────────────────────────────

if (client.ServerCapabilities?.Resources is not null)
{
    Console.WriteLine($"\n══════════════════════════════════════════════════════");
    Console.WriteLine($"  RESOURCES");
    Console.WriteLine($"══════════════════════════════════════════════════════");

    var resources = await client.ListResourcesAsync();
    var templates = await client.ListResourceTemplatesAsync();
    Console.WriteLine($"  Direct ({resources.Count}): {string.Join(", ", resources.Select(r => r.Name))}");
    Console.WriteLine($"  Templates ({templates.Count}): {string.Join(", ", templates.Select(t => t.Name))}");

    Console.WriteLine("\n[reading config://app/settings]");
    var config = await client.ReadResourceAsync("config://app/settings");
    foreach (var c in config.Contents)
        if (c is TextResourceContents t)
            Console.WriteLine($"  {t.Text.Replace("\n", "\n  ")}");

    Console.WriteLine("\n[reading user://profile/42]");
    var profile = await client.ReadResourceAsync("user://profile/42");
    foreach (var c in profile.Contents)
        if (c is TextResourceContents t)
            Console.WriteLine($"  {t.Text.Replace("\n", "\n  ")}");

    // ── LIVE SUBSCRIPTION ─────────────────────────────────────────────────
    // Senior feature: full subscribe → receive push → unsubscribe lifecycle.
    // The background worker fires notifications/resources/updated every 5s.
    // SubscribeToResourceAsync wires the callback and returns IAsyncDisposable.

    Console.WriteLine("\n[subscribing to metrics://server/live]");
    // ── Seed the registry first ──────────────────────────────────────────────
    // Reading the resource registers this McpServer instance in the
    // ConnectionRegistry. Without this, the background worker has no
    // connections to notify.
    Console.WriteLine("  [seeding registry — reading resource once]");
    var seed = await client.ReadResourceAsync("metrics://server/live");
    foreach (var c in seed.Contents)
        if (c is TextResourceContents t)
            Console.WriteLine($"  Initial read:\n  {t.Text.Replace("\n", "\n  ")}");


    Console.WriteLine("  Waiting up to 12s for a push notification from the background worker...\n");

    var updateReceived = new TaskCompletionSource<bool>();

    await using var sub = await client.SubscribeToResourceAsync(
        "metrics://server/live",
        async (notification, ct) =>
        {
            Console.WriteLine($"  [←push] metrics://server/live updated — re-reading...");

            var updated = await client.ReadResourceAsync(notification.Uri, cancellationToken: ct);
            foreach (var c in updated.Contents)
                if (c is TextResourceContents t)
                    Console.WriteLine($"  {t.Text.Replace("\n", "\n  ")}");

            updateReceived.TrySetResult(true);
        });

    // Wait for one update, then unsubscribe
    var timeout = Task.Delay(TimeSpan.FromSeconds(12));
    var winner = await Task.WhenAny(updateReceived.Task, timeout);

    if (winner == timeout)
        Console.WriteLine("  [timeout] No update received in 12s — server may need a moment to warm up.");

    // IAsyncDisposable — unsubscribes cleanly on await using block exit
    Console.WriteLine("\n  [unsubscribed from metrics://server/live]");
}

// ─────────────────────────────────────────────────────────────────────────────
//  PROMPTS
// ─────────────────────────────────────────────────────────────────────────────

if (client.ServerCapabilities?.Prompts is not null)
{
    Console.WriteLine($"\n══════════════════════════════════════════════════════");
    Console.WriteLine($"  PROMPTS");
    Console.WriteLine($"══════════════════════════════════════════════════════");

    var prompts = await client.ListPromptsAsync();
    foreach (var p in prompts)
    {
        Console.Write($"  - {p.Name}: {p.Description}");
        if (p.ProtocolPrompt.Arguments is { Count: > 0 })
            Console.Write($"  (args: {string.Join(", ", p.ProtocolPrompt.Arguments.Select(a => a.Name))})");
        Console.WriteLine();
    }

    Console.WriteLine("\n[getting 'system_init']");
    var init = await client.GetPromptAsync("system_init");
    foreach (var msg in init.Messages)
        if (msg.Content is TextContentBlock t)
            Console.WriteLine($"  [{msg.Role}]: {t.Text[..Math.Min(80, t.Text.Length)]}...");

    Console.WriteLine("\n[getting 'summarize_health' with args]");
    var summary = await client.GetPromptAsync("summarize_health",
        new Dictionary<string, object?> { ["window"] = "1h" });
    foreach (var msg in summary.Messages)
        if (msg.Content is TextContentBlock t)
            Console.WriteLine($"  [{msg.Role}]: {t.Text}");
}

// ─────────────────────────────────────────────────────────────────────────────
//  DONE
// ─────────────────────────────────────────────────────────────────────────────

Console.WriteLine($"\n══════════════════════════════════════════════════════");
Console.WriteLine($"  Sprint 10 complete. Client shutting down gracefully.");
Console.WriteLine($"══════════════════════════════════════════════════════");

// await using client → DisposeAsync called here automatically
// Sends a proper close handshake over HTTP before the process exits