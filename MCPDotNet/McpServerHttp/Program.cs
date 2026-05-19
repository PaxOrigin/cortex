// ═══════════════════════════════════════════════════════════════════════════
//  MCP REFERENCE — HTTP SERVER (ASP.NET Core)
//  ModelContextProtocol 1.2  ·  .NET 10
//
//  Project SDK:  Microsoft.NET.Sdk.Web
//  Packages:
//    dotnet add package ModelContextProtocol.AspNetCore
//    dotnet add package Microsoft.Extensions.AI.Abstractions
//
//  Run:
//    dotnet run                              → listens on default port
//    ASPNETCORE_URLS=http://localhost:5101 dotnet run  → second instance
//
//  STATEFUL HTTP is required for sampling, elicitation, and push notifications.
//  Stateless mode (Stateless=true) disables all server-to-client requests.
//
//  Features demonstrated:
//    ✓ Stateful HTTP transport       required for sampling · elicitation · push
//    ✓ Connection registry           WeakReference — no memory leaks on disconnect
//    ✓ Background push worker        notifications/resources/updated every 5s
//    ✓ Tool list change notification SendNotificationAsync(ToolListChanged)
//    ✓ Resource update notification  SendNotificationAsync(ResourceUpdated)
//    ✓ Sampling                      server.AsSamplingChatClient() → IChatClient
//    ✓ Elicitation (form mode)       Boolean · String · UntitledEnum · TitledEnum
//    ✓ All annotation flags          ReadOnly · Destructive · Idempotent · OpenWorld
//    ✓ Server-to-client logging      AsClientLoggerProvider()
//    ✓ Progress + cancellation       IProgress<ProgressNotificationValue>
//    ✓ Resources                     live (subscribable) · static · template
//    ✓ Prompts                       system init · health summary with arg
//    ✓ Dynamic tool management       register at runtime + notify clients
// ═══════════════════════════════════════════════════════════════════════════

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  SETUP
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<LiveMetrics>();
builder.Services.AddSingleton<DynamicToolRegistry>();
builder.Services.AddHostedService<PushNotificationWorker>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()           // stateful = default, required for sampling + elicitation + push
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

var app = builder.Build();
app.MapMcp("/mcp");                // Streamable HTTP at /mcp, legacy SSE at /mcp/sse
app.Run();                         // port via ASPNETCORE_URLS or --urls


// ═══════════════════════════════════════════════════════════════════════════
//  CONNECTION REGISTRY
//  Tracks live McpServer sessions across concurrent HTTP connections.
//  WeakReference: when a client disconnects, the McpServer is GC'd.
//  GetLive() purges stale references on every iteration.
//
//  Multi-instance note: for multiple server processes, replace with
//  a Redis pub/sub broadcaster (see Sprint 12 — confirmed working pattern).
// ═══════════════════════════════════════════════════════════════════════════

public class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, WeakReference<McpServer>> _connections = new();

    public void Register(McpServer server)
        => _connections.TryAdd(
            server.SessionId ?? server.GetHashCode().ToString(),
            new WeakReference<McpServer>(server));

    public int Count => GetLive().Count();

    public IEnumerable<McpServer> GetLive()
    {
        var dead = new List<string>();
        foreach (var (id, weak) in _connections)
        {
            if (weak.TryGetTarget(out var s)) yield return s;
            else dead.Add(id);
        }
        foreach (var id in dead) _connections.TryRemove(id, out _);
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  LIVE METRICS
// ═══════════════════════════════════════════════════════════════════════════

public class LiveMetrics
{
    private double _cpu = 12.5;
    private double _mem = 256.0;
    private int _req = 0;
    private readonly DateTime _start = DateTime.UtcNow;

    public object Snapshot()
    {
        _cpu = Math.Round(Math.Clamp(_cpu + (Random.Shared.NextDouble() - 0.5) * 5, 1, 99), 1);
        _mem = Math.Round(Math.Clamp(_mem + (Random.Shared.NextDouble() - 0.5) * 10, 64, 512), 1);
        return new
        {
            cpu = _cpu,
            memoryMb = _mem,
            requests = ++_req,
            uptime = (DateTime.UtcNow - _start).ToString(@"hh\:mm\:ss")
        };
    }

    public void Reset() { _cpu = 12.5; _mem = 256.0; _req = 0; }
}


// ═══════════════════════════════════════════════════════════════════════════
//  DYNAMIC TOOL REGISTRY
//  Tools can be registered at runtime. After adding/removing, call
//  server.SendNotificationAsync(ToolListChangedNotification) so connected
//  clients know to refresh their tool list.
// ═══════════════════════════════════════════════════════════════════════════

public class DynamicToolRegistry
{
    private readonly ConcurrentDictionary<string, string> _tools = new();
    public void Add(string name, string desc) => _tools[name] = desc;
    public bool Remove(string name) => _tools.TryRemove(name, out _);
    public IDictionary<string, string> GetAll() => _tools;
}


// ═══════════════════════════════════════════════════════════════════════════
//  BACKGROUND PUSH WORKER
//  Sends notifications/resources/updated to all live connections every 5s.
//  Clients subscribed to metrics://server/live will re-read the resource.
//
//  This pattern works for single-instance deployments.
//  For multi-instance: publish to a Redis channel; each instance's subscriber
//  forwards to its own ConnectionRegistry (Sprint 12 Redis backplane pattern).
// ═══════════════════════════════════════════════════════════════════════════

public class PushNotificationWorker(
    ConnectionRegistry registry,
    ILogger<PushNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Push worker started — firing every 5s");

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            foreach (var server in registry.GetLive())
            {
                try
                {
                    await server.SendNotificationAsync(
                        NotificationMethods.ResourceUpdatedNotification,
                        new ResourceUpdatedNotificationParams { Uri = "metrics://server/live" });
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Notify failed: {Message}", ex.Message);
                }
            }
        }
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  TOOLS
// ═══════════════════════════════════════════════════════════════════════════

[McpServerToolType]
public static class InfoTools
{
    // ── McpServer context ─────────────────────────────────────────────────
    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Returns connected client name and declared capabilities")]
    public static string WhoAmI(McpServer server)
    {
        var info = server.ClientInfo;
        var caps = server.ClientCapabilities;
        return $"Client:      {info?.Name} {info?.Version}\n" +
               $"Session:     {server.SessionId ?? "n/a"}\n" +
               $"Roots:       {caps?.Roots is not null}\n" +
               $"Sampling:    {caps?.Sampling is not null}\n" +
               $"Elicitation: {caps?.Elicitation is not null}";
    }

    // ── roots/list — server requests client's filesystem roots ────────────
    [McpServerTool(ReadOnly = true),
     Description("Lists filesystem roots declared by the client via roots/list")]
    public static async Task<string> GetClientRoots(McpServer server, CancellationToken ct)
    {
        if (server.ClientCapabilities?.Roots is null)
            return "Client did not declare Roots capability.";
        try
        {
            var result = await server.SendRequestAsync<object?, ListRootsResult>(
                "roots/list", null, McpJsonUtilities.DefaultOptions, default, ct);
            if (result.Roots.Count == 0) return "Client returned no roots.";
            return string.Join("\n", result.Roots.Select(r => $"{r.Name ?? "unnamed"}  →  {r.Uri}"));
        }
        catch (Exception ex) { return $"Could not list roots: {ex.Message}"; }
    }
}

[McpServerToolType]
public static class MetricsTools
{
    // ── Live metrics — seeds ConnectionRegistry for push notifications ─────
    [McpServerTool(ReadOnly = true, Idempotent = false),
     Description("Returns current server metrics. Call this first to enable push notifications.")]
    public static string GetMetrics(
        McpServer server,
        ConnectionRegistry registry,
        LiveMetrics metrics)
    {
        registry.Register(server);    // required: seeds registry for background worker
        return JsonSerializer.Serialize(metrics.Snapshot(),
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = true),
     Description("Resets all metrics to zero. DESTRUCTIVE but idempotent.")]
    public static string ResetMetrics(LiveMetrics metrics)
    {
        metrics.Reset();
        return "Metrics reset.";
    }
}

[McpServerToolType]
public static class SamplingTools
{
    // ── Sampling — server → client → LLM → server ────────────────────────
    // server.AsSamplingChatClient() creates an IChatClient that routes
    // requests through sampling/createMessage back to the connected client.
    // The client runs the LLM and returns the result.
    // Requires: client declares Sampling capability in McpClientOptions.
    [McpServerTool(ReadOnly = true),
     Description("Asks the client to run an LLM completion (sampling). " +
                 "Client must declare Sampling capability.")]
    public static async Task<string> AskLlm(
        McpServer server,
        [Description("Question for the LLM")] string question,
        CancellationToken ct)
    {
        if (server.ClientCapabilities?.Sampling is null)
            throw new McpException("Client does not support sampling.");

        // AsSamplingChatClient() is the confirmed SDK API for server-side sampling
        var samplingClient = server.AsSamplingChatClient();
        var response = await samplingClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, question)],
            cancellationToken: ct);

        return $"Model:    {response.ModelId}\nAnswer: {response.Text}";
    }
}

[McpServerToolType]
public static class DynamicTools
{
    // ── Tool list change notification ─────────────────────────────────────
    // After mutating the dynamic registry, notify ALL live clients to refresh.
    // Requires stateful HTTP — stateless servers cannot push notifications.
    [McpServerTool(ReadOnly = false, Destructive = false),
     Description("Registers a dynamic tool name and notifies clients to refresh tool list")]
    public static async Task<string> RegisterTool(
        McpServer server,
        DynamicToolRegistry registry,
        [Description("Tool name")] string name,
        [Description("Tool description")] string description,
        CancellationToken ct)
    {
        registry.Add(name, description);

        // Notify this client — in production notify all via ConnectionRegistry
        await server.SendNotificationAsync(
            NotificationMethods.ToolListChangedNotification,
            new ToolListChangedNotificationParams(),
            cancellationToken: ct);

        return $"Registered '{name}'. ToolListChanged notification sent.";
    }
}

[McpServerToolType]
public static class AnnotatedTools
{
    [McpServerTool(ReadOnly = true, OpenWorld = true),
     Description("Fetches from an external source. Open-world: result is non-deterministic.")]
    public static string FetchExternal(
        [Description("Source identifier")] string source)
        => $"[Simulated external fetch '{source}' at {DateTime.UtcNow:HH:mm:ss}]";

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false),
     Description("Appends to an audit log. Additive — never deletes.")]
    public static string AppendAudit(
        [Description("Log message")] string message)
        => $"Appended [{DateTime.UtcNow:o}]: {message}";
}

[McpServerToolType]
public static class ProgressAndLoggingTools
{
    // ── Progress ──────────────────────────────────────────────────────────
    [McpServerTool(ReadOnly = true),
     Description("Multi-step diagnostic with progress. Client must send progressToken.")]
    public static async Task<string> RunDiagnostics(
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        var checks = new[] { "database", "cache", "external-api", "ssl" };
        var results = new List<object>();

        for (int i = 0; i < checks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(300, ct);
            var passed = checks[i] != "external-api"; // simulate one failure
            results.Add(new { check = checks[i], passed });
            progress.Report(new ProgressNotificationValue
            {
                Progress = i + 1,
                Total = checks.Length,
                Message = $"{checks[i]} → {(passed ? "✓" : "✗")}"
            });
        }

        return JsonSerializer.Serialize(new
        {
            runAt = DateTime.UtcNow,
            passed = results.Count(r => (bool)((dynamic)r).passed),
            total = results.Count,
            checks = results
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Server-to-client logging ──────────────────────────────────────────
    [McpServerTool(ReadOnly = true),
     Description("Streams structured log entries to client over notifications/message")]
    public static async Task<string> StreamLogs(
        McpServer server,
        [Description("Log category label")] string label,
        CancellationToken ct)
    {
        var logger = server.AsClientLoggerProvider().CreateLogger(label);
        logger.LogInformation("Starting '{Label}'", label);
        await Task.Delay(150, ct);
        logger.LogDebug("Step 1 detail");
        await Task.Delay(150, ct);
        logger.LogWarning("Step 2 non-critical warning");
        await Task.Delay(150, ct);
        logger.LogInformation("'{Label}' complete", label);
        return $"Sent 4 log entries under logger '{label}'.";
    }
}

[McpServerToolType]
public static class ElicitationTools
{
    [McpServerTool(ReadOnly = false, Destructive = true),
     Description("Deploys a service. Elicits environment via TitledEnum, confirms boolean for production.")]
    public static async Task<string> DeployService(
        McpServer server,
        [Description("Service name")] string service,
        CancellationToken ct)
    {
        if (server.ClientCapabilities?.Elicitation is null)
            throw new McpException("Client does not support elicitation.");

        var r1 = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = $"Deploy '{service}'",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties =
                {
                    ["environment"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                    {
                        Description = "Target environment",
                        OneOf       =
                        [
                            new ()
                                { Const = "dev", Title = "Development" },
                            new ()
                                { Const = "stg", Title = "Staging" },
                            new ()
                                { Const = "prd", Title = "⚠️  Production" }
                        ]
                    },
                    ["version"] = new ElicitRequestParams.StringSchema
                    {
                        Description = "Version tag (e.g. v1.2.3)", MinLength = 1
                    }
                }
            }
        }, ct);

        if (r1.Action != "accept" || r1.Content is null)
            return $"Deployment cancelled ({r1.Action}).";

        var env = r1.Content["environment"].GetString() ?? "dev";
        var version = r1.Content.TryGetValue("version", out var v) ? v.GetString() ?? "latest" : "latest";

        if (env == "prd")
        {
            var r2 = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "⚠️  PRODUCTION — confirm?",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties =
                    {
                        ["confirmed"] = new ElicitRequestParams.BooleanSchema
                        {
                            Description = $"Deploy {service} {version} to PRODUCTION (true/false)"
                        }
                    }
                }
            }, ct);

            var ok = r2.Action == "accept" &&
                     r2.Content?.TryGetValue("confirmed", out var c) is true &&
                     c.ValueKind == System.Text.Json.JsonValueKind.True;

            if (!ok) return "Production deployment aborted.";
        }

        await Task.Delay(300, ct);
        return $"✓ Deployed '{service}' {version} → {env} at {DateTime.UtcNow:HH:mm:ss} UTC.";
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  RESOURCES
// ═══════════════════════════════════════════════════════════════════════════

[McpServerResourceType]
public static class ServerResources
{
    // ── Live metrics — clients subscribe, background worker pushes ─────────
    // IMPORTANT: Call registry.Register(server) here so the background worker
    // can push notifications/resources/updated to this connection.
    [McpServerResource(UriTemplate = "metrics://server/live", Name = "Live Metrics", MimeType = "application/json")]
    [Description("Real-time metrics. Subscribe to receive push updates every 5s. Read this first to enable push.")]
    public static string GetLiveMetrics(
        McpServer server,
        ConnectionRegistry registry,
        LiveMetrics metrics)
    {
        registry.Register(server);
        return JsonSerializer.Serialize(metrics.Snapshot(),
            new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Static direct resource ────────────────────────────────────────────
    [McpServerResource(UriTemplate = "config://app/settings", Name = "App Settings", MimeType = "application/json")]
    [Description("Static application configuration")]
    public static string GetSettings()
        => JsonSerializer.Serialize(new
        {
            version = "1.0.0",
            environment = "production",
            features = new { darkMode = true, betaTools = false },
            rateLimits = new { requestsPerMinute = 60 }
        }, new JsonSerializerOptions { WriteIndented = true });

    // ── Template resource ─────────────────────────────────────────────────
    [McpServerResource(UriTemplate = "user://profile/{userId}", Name = "User Profile", MimeType = "application/json")]
    [Description("User profile by ID. URI example: user://profile/42")]
    public static string GetUserProfile(string userId)
        => JsonSerializer.Serialize(new
        {
            id = userId,
            name = $"User {userId}",
            role = "developer",
            joinedAt = "2025-01-01T00:00:00Z"
        }, new JsonSerializerOptions { WriteIndented = true });
}


// ═══════════════════════════════════════════════════════════════════════════
//  PROMPTS
// ═══════════════════════════════════════════════════════════════════════════

[McpServerPromptType]
public static class ServerPrompts
{
    [McpServerPrompt,
     Description("System init — safety rules and tool guidance for the LLM")]
    public static IEnumerable<ChatMessage> SystemInit()
        =>
        [
            new(ChatRole.User,
                "You are connected to McpServerHttp. " +
                "Always use tools for real data. " +
                "Destructive=true tools require explicit user confirmation. " +
                "Never answer from memory or training data."),
            new(ChatRole.Assistant,
                "Understood. I will request confirmation before any destructive operations.")
        ];

    [McpServerPrompt,
     Description("Health summary prompt for a given time window")]
    public static ChatMessage HealthSummary(
        [Description("Time window, e.g. '1h' or '24h'")] string window)
        => new(ChatRole.User,
            $"Summarise server health for the past {window}. " +
            "Use get_metrics and run_diagnostics. Report pass/fail counts and anomalies.");
}