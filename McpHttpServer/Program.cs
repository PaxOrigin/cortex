// If WebApplication is not found, your project SDK is wrong.
// Open McpServerHttp.csproj and ensure the first line is:
//   <Project Sdk="Microsoft.NET.Sdk.Web">
// NOT: <Project Sdk="Microsoft.NET.Sdk">

using Microsoft.AspNetCore.Builder;           // WebApplication
using Microsoft.Extensions.AI;
using StackExchange.Redis;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  SERVICES REGISTRATION
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// Replaced by new redis logic
// builder.Services.AddSingleton<ConnectionRegistry>();
// builder.Services.AddSingleton<LiveMetricsService>();
// builder.Services.AddHostedService<MetricsBackgroundWorker>();

// Redis connection — shared across all services
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect("localhost:6379"));

// Replaces ConnectionRegistry — now backed by Redis pub/sub
builder.Services.AddSingleton<RedisBroadcaster>();

// Keep ConnectionRegistry for the McpServer → broadcaster bridge
builder.Services.AddSingleton<ConnectionRegistry>();

builder.Services.AddSingleton<LiveMetricsService>();

// Replace MetricsBackgroundWorker with the Redis-aware version
builder.Services.AddHostedService<RedisMetricsWorker>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

var app = builder.Build();
app.MapMcp("/mcp");
app.Run();


// ─────────────────────────────────────────────────────────────────────────────
//  ANNOTATED TOOLS
//  readOnlyHint, destructiveHint, idempotentHint, openWorldHint
//  are declared on [McpServerTool] and travel with the tool definition
//  over the protocol. Clients use them to drive confirmation dialogs,
//  retry policies, and safety gates in agentic workflows.
// ─────────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class AnnotatedTools
{
    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Returns current server metrics. Safe to auto-approve and retry.")]
    public static string GetMetrics(LiveMetricsService metrics)
        => JsonSerializer.Serialize(metrics.GetCurrent(),
               new JsonSerializerOptions { WriteIndented = true });

    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Appends an entry to the server audit log. Additive — never deletes.")]
    public static string AppendAuditLog(
        [Description("The message to append")] string message)
        => $"Appended [{DateTime.UtcNow:o}]: {message}";

    [McpServerTool(ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Resets all metrics counters to zero. DESTRUCTIVE — existing data is lost.")]
    public static string ResetMetrics(LiveMetricsService metrics)
    {
        metrics.Reset();
        return "Metrics reset to zero.";
    }

    [McpServerTool(ReadOnly = true, OpenWorld = true),
     Description("Fetches live data from an external source. Open-world: result is unpredictable.")]
    public static string FetchExternalData(
        [Description("The data source identifier")] string source)
        => $"[Simulated external fetch from '{source}' at {DateTime.UtcNow:HH:mm:ss}]";
}


// ─────────────────────────────────────────────────────────────────────────────
//  SERVER-TO-CLIENT LOGGING
//  server.AsClientLoggerProvider() wraps notifications/message into ILogger.
//  Log lines flow over the live connection — the client registers a handler
//  for notifications/message to receive and display them.
// ─────────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class LoggingTools
{
    [McpServerTool(ReadOnly = true),
     Description("Runs a task and sends structured log entries to the client over MCP")]
    public static async Task<string> RunWithClientLogging(
        McpServer server,
        [Description("A label to tag the log entries")] string label,
        CancellationToken ct)
    {
        var loggerProvider = server.AsClientLoggerProvider();
        var logger = loggerProvider.CreateLogger(label);

        logger.LogInformation("Starting operation '{Label}'", label);
        await Task.Delay(200, ct);
        logger.LogDebug("Processing step 1 of 3");
        await Task.Delay(200, ct);
        logger.LogWarning("Step 2 produced a non-critical warning");
        await Task.Delay(200, ct);
        logger.LogInformation("Operation '{Label}' completed", label);

        return $"Sent 4 log entries to client for label '{label}'.";
    }
}


// ─────────────────────────────────────────────────────────────────────────────
//  STRUCTURED OUTPUT + PROGRESS
// ─────────────────────────────────────────────────────────────────────────────

public record ServerStatus(
    string Name, string Status, int ActiveConnections,
    double CpuPercent, double MemoryMb, string Uptime);

[McpServerToolType]
public static class StructuredOutputTools
{
    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Returns full server status as a structured JSON object")]
    public static ServerStatus GetServerStatus(
        ConnectionRegistry registry,
        LiveMetricsService metrics)
    {
        var m = metrics.GetCurrent();
        return new ServerStatus(
            Name: "McpHttpServer", Status: "running",
            ActiveConnections: registry.Count,
            CpuPercent: m.CpuPercent, MemoryMb: m.MemoryMb, Uptime: m.Uptime);
    }

    [McpServerTool(ReadOnly = true),
     Description("Runs a multi-step diagnostic with progress and returns structured results")]
    public static async Task<string> RunDiagnostics(
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        var checks = new[] { "database", "cache", "external-api", "ssl-certificate" };
        var results = new List<object>();

        for (int i = 0; i < checks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(300, ct);
            var passed = checks[i] != "external-api";
            results.Add(new { Check = checks[i], Passed = passed });
            progress.Report(new ProgressNotificationValue
            {
                Progress = i + 1,
                Total = checks.Length,
                Message = $"{checks[i]} → {(passed ? "✓" : "✗")}"
            });
        }

        return JsonSerializer.Serialize(new
        {
            RunAt = DateTime.UtcNow,
            Passed = results.Count(r => (bool)((dynamic)r).Passed),
            Total = results.Count,
            Checks = results
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}


// ─────────────────────────────────────────────────────────────────────────────
//  INFO TOOLS (from Sprint 10)
// ─────────────────────────────────────────────────────────────────────────────

[McpServerToolType]
public static class InfoTools
{
    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Returns the connected client name and declared capabilities")]
    public static string WhoAmI(McpServer server)
    {
        var info = server.ClientInfo;
        var caps = server.ClientCapabilities;
        return $"Client:   {info?.Name} {info?.Version}\n" +
               $"Roots:    {caps?.Roots is not null}\n" +
               $"Sampling: {caps?.Sampling is not null}";
    }

    [McpServerTool(ReadOnly = true),
     Description("Lists the filesystem roots declared by the connected client")]
    public static async Task<string> GetClientRoots(McpServer server, CancellationToken ct)
    {
        try
        {
            var result = await server.SendRequestAsync<object?, ListRootsResult>(
                "roots/list", null, McpJsonUtilities.DefaultOptions, default, ct);

            if (result.Roots.Count == 0)
                return "Client returned no roots.";

            var lines = result.Roots.Select(r => $"  {r.Name ?? "unnamed"}  →  {r.Uri}");
            return $"Client roots ({result.Roots.Count}):\n{string.Join("\n", lines)}";
        }
        catch (Exception ex) { return $"Could not list roots: {ex.Message}"; }
    }

    [McpServerTool(ReadOnly = true),
     Description("Asks the client to run an LLM completion and returns the result")]
    public static async Task<string> AskClient(
        McpServer server,
        [Description("The question to send to the LLM")] string question,
        CancellationToken ct)
    {
        try
        {
            var response = await server.SampleAsync(
                [new ChatMessage(ChatRole.User, question)],
                cancellationToken: ct);
            return $"Model:    {response.ModelId}\nResponse: {response.Text}";
        }
        catch (Exception ex) { return $"Sampling failed: {ex.Message}"; }
    }
}

[McpServerToolType]
public static class MathTools
{
    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Adds two integers and returns the result")]
    public static int Add(
        [Description("First number")] int a,
        [Description("Second number")] int b) => a + b;
}


// ─────────────────────────────────────────────────────────────────────────────
//  RESOURCES
// ─────────────────────────────────────────────────────────────────────────────

[McpServerResourceType]
public static class ServerResources
{
    [McpServerResource(UriTemplate = "metrics://server/live",
        Name = "Live Server Metrics", MimeType = "application/json")]
    [Description("Real-time metrics — background worker pushes updates every 5s")]
    public static string GetLiveMetrics(
        McpServer server, ConnectionRegistry registry, LiveMetricsService metrics)
    {
        registry.Register(server);
        return JsonSerializer.Serialize(metrics.GetCurrent(),
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerResource(UriTemplate = "config://app/settings",
        Name = "Application Settings", MimeType = "application/json")]
    [Description("Static application configuration")]
    public static string GetAppSettings()
        => JsonSerializer.Serialize(new
        {
            version = "1.0.0",
            environment = "development",
            features = new { darkMode = true, betaTools = false },
            rateLimits = new { requestsPerMinute = 60, burstLimit = 10 }
        }, new JsonSerializerOptions { WriteIndented = true });

    [McpServerResource(UriTemplate = "user://profile/{userId}",
        Name = "User Profile", MimeType = "application/json")]
    [Description("Returns the profile for a given user ID")]
    public static string GetUserProfile(string userId)
        => JsonSerializer.Serialize(new
        {
            id = userId,
            name = $"User {userId}",
            role = "developer",
            joinedAt = "2025-01-01T00:00:00Z"
        }, new JsonSerializerOptions { WriteIndented = true });
}


// ─────────────────────────────────────────────────────────────────────────────
//  PROMPTS
// ─────────────────────────────────────────────────────────────────────────────

[McpServerPromptType]
public static class ServerPrompts
{
    [McpServerPrompt,
     Description("System bootstrap — initialises the LLM with server context and safety rules")]
    public static IEnumerable<ChatMessage> SystemInit()
        =>
        [
            new(ChatRole.User,
                "You are connected to McpHttpServer. You have access to tools for metrics, " +
                "diagnostics, audit logging, and user profiles. " +
                "Always inspect tool annotations before calling: " +
                "Destructive=true tools require explicit user confirmation."),
            new(ChatRole.Assistant,
                "Understood. I will request confirmation before invoking any destructive operations.")
        ];

    [McpServerPrompt,
     Description("Generates a prompt that asks the LLM to summarize server health")]
    public static ChatMessage SummarizeHealth(
        [Description("Time window, e.g. '1h' or '24h'")] string window)
        => new(ChatRole.User,
            $"Summarize server health for the past {window}. " +
            $"Use get_server_status, get_metrics, and run_diagnostics. " +
            $"Report findings concisely with pass/fail counts.");
}


// ─────────────────────────────────────────────────────────────────────────────
//  SERVICES
// ─────────────────────────────────────────────────────────────────────────────

public record Metrics(double CpuPercent, double MemoryMb, int RequestCount, string Uptime);

public class LiveMetricsService
{
    private int _requests = 0;
    private double _cpu = 12.5;
    private double _memory = 256.0;
    private readonly DateTime _startTime = DateTime.UtcNow;

    public Metrics GetCurrent()
    {
        _cpu = Math.Round(Math.Clamp(_cpu + (Random.Shared.NextDouble() - 0.5) * 5, 1, 99), 1);
        _memory = Math.Round(Math.Clamp(_memory + (Random.Shared.NextDouble() - 0.5) * 10, 64, 512), 1);
        _requests++;
        return new Metrics(_cpu, _memory, _requests,
            (DateTime.UtcNow - _startTime).ToString(@"hh\:mm\:ss"));
    }

    public void Reset() { _requests = 0; _cpu = 12.5; _memory = 256.0; }
}

// WeakReference prevents memory leaks when clients disconnect.
// The registry is cleaned on every GetLive() iteration.
public class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, WeakReference<McpServer>> _servers = new();

    public void Register(McpServer server)
    {
        var id = server.SessionId ?? server.GetHashCode().ToString();
        _servers.TryAdd(id, new WeakReference<McpServer>(server));
    }

    public int Count => GetLive().Count();

    public IEnumerable<McpServer> GetLive()
    {
        var dead = new List<string>();
        foreach (var (id, weak) in _servers)
        {
            if (weak.TryGetTarget(out var server)) yield return server;
            else dead.Add(id);
        }
        foreach (var id in dead) _servers.TryRemove(id, out _);
    }
}

// Push notifications/resources/updated to all live connections every 5 seconds.
// Subscribed clients re-fetch the resource automatically on receipt.
// public class MetricsBackgroundWorker(
//     ConnectionRegistry registry,
//     ILogger<MetricsBackgroundWorker> logger) : BackgroundService
// {
//     protected override async Task ExecuteAsync(CancellationToken stoppingToken)
//     {
//         logger.LogInformation("Metrics background worker started");
//         while (!stoppingToken.IsCancellationRequested)
//         {
//             await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
//             foreach (var server in registry.GetLive())
//             {
//                 try
//                 {
//                     await server.SendNotificationAsync(
//                         "notifications/resources/updated",
//                         new ResourceUpdatedNotificationParams { Uri = "metrics://server/live" });
//                 }
//                 catch (Exception ex)
//                 {
//                     logger.LogDebug("Could not notify client: {Message}", ex.Message);
//                 }
//             }
//         }
//     }
// }

// ─────────────────────────────────────────────────────────────────────────────
//  REDIS BROADCASTER
//  Publishes resource update notifications to a Redis channel.
//  Any process subscribed to that channel — this one or another instance —
//  will receive the message and forward it to its local MCP connections.
//
//  Pub/Sub channel: "mcp:resources:updated"
//  Message format:  the resource URI (plain string)
// ─────────────────────────────────────────────────────────────────────────────

public class RedisBroadcaster : IAsyncDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ConnectionRegistry _registry;
    private readonly ILogger<RedisBroadcaster> _logger;
    private readonly ISubscriber _subscriber;

    private const string Channel = "mcp:resources:updated";

    public RedisBroadcaster(
        IConnectionMultiplexer redis,
        ConnectionRegistry registry,
        ILogger<RedisBroadcaster> logger)
    {
        _redis = redis;
        _registry = registry;
        _logger = logger;
        _subscriber = redis.GetSubscriber();
    }

    // Called once at startup — subscribe to the Redis channel.
    // Any instance that publishes to this channel triggers this handler
    // on ALL subscribed instances, including the publisher itself.
    public async Task StartAsync(CancellationToken ct)
    {
        await _subscriber.SubscribeAsync(
            RedisChannel.Literal(Channel),
            async (channel, uri) =>
            {
                if (!uri.HasValue) return;

                _logger.LogDebug(
                    "Redis → received update for {Uri}, notifying {Count} local connections",
                    uri, _registry.Count);

                foreach (var server in _registry.GetLive())
                {
                    try
                    {
                        await server.SendNotificationAsync(
                            "notifications/resources/updated",
                            new ResourceUpdatedNotificationParams
                            {
                                Uri = uri.ToString()!
                            });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(
                            "Could not notify MCP client: {Message}", ex.Message);
                    }
                }
            });

        _logger.LogInformation(
            "RedisBroadcaster subscribed to channel '{Channel}'", Channel);
    }

    // Any code path that detects a data change calls this.
    // The message fans out to ALL subscribed instances via Redis.
    public async Task PublishAsync(string resourceUri)
    {
        var count = await _subscriber.PublishAsync(
            RedisChannel.Literal(Channel), resourceUri);

        _logger.LogDebug(
            "Redis ← published update for {Uri} to {Count} subscriber(s)",
            resourceUri, count);
    }

    public async ValueTask DisposeAsync()
    {
        await _subscriber.UnsubscribeAllAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  REDIS METRICS WORKER
//  Replaces MetricsBackgroundWorker.
//  Instead of iterating ConnectionRegistry directly,
//  it publishes to Redis. The broadcaster handles the rest.
//  This means: if you run two server instances,
//  BOTH will forward the notification to their local clients.
// ─────────────────────────────────────────────────────────────────────────────

public class RedisMetricsWorker(
    RedisBroadcaster broadcaster,
    ILogger<RedisMetricsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start the Redis subscription before the first publish
        await broadcaster.StartAsync(stoppingToken);

        logger.LogInformation("RedisMetricsWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            // Publish to Redis — all instances receive and forward
            await broadcaster.PublishAsync("metrics://server/live");
        }
    }
}