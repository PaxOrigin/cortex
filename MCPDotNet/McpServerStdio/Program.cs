// ═══════════════════════════════════════════════════════════════════════════
//  MCP REFERENCE — STDIO SERVER
//  ModelContextProtocol 1.2  ·  .NET 10
//
//  Project SDK:  Microsoft.NET.Sdk
//  Packages:
//    dotnet add package ModelContextProtocol
//    dotnet add package Microsoft.Extensions.Hosting
//    dotnet add package Microsoft.Extensions.AI.Abstractions
//
//  CRITICAL: stdout belongs to the MCP JSON-RPC stream.
//  All application logs MUST go to stderr — see logging setup below.
//
//  Features demonstrated:
//    ✓ All tool content types        text · image · audio · embedded-text · embedded-blob · mixed
//    ✓ Content annotations           Audience · Priority
//    ✓ All tool annotation flags     ReadOnly · Destructive · Idempotent · OpenWorld
//    ✓ Dependency injection          constructor injection · method-parameter injection
//    ✓ Runtime-injected parameters   McpServer · IProgress<T> · CancellationToken
//    ✓ Server-to-client logging      AsClientLoggerProvider()
//    ✓ Elicitation (form mode)       Boolean · String · UntitledEnum · TitledEnum · multi-step
//    ✓ Error handling                McpException · McpProtocolException · generic exception
//    ✓ Resources                     direct · template · TextResourceContents · BlobResourceContents
//    ✓ Prompts                       no-arg · multi-turn · optional-arg
// ═══════════════════════════════════════════════════════════════════════════

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
//  HOST SETUP
// ─────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

// Send ALL logs to stderr — stdout is exclusively for MCP JSON-RPC messages
builder.Logging.AddConsole(opts =>
    opts.LogToStandardErrorThreshold = LogLevel.Trace);

// ── Domain services — injected into non-static tool classes ──────────────────
builder.Services.AddSingleton<ITimeService, TimeService>();
builder.Services.AddSingleton<IDataService, InMemoryDataService>();

// ── MCP wiring ────────────────────────────────────────────────────────────────
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()      // discovers every [McpServerToolType]
    .WithResourcesFromAssembly()  // discovers every [McpServerResourceType]
    .WithPromptsFromAssembly();   // discovers every [McpServerPromptType]

await builder.Build().RunAsync();


// ═══════════════════════════════════════════════════════════════════════════
//  DOMAIN SERVICES
// ═══════════════════════════════════════════════════════════════════════════

public interface ITimeService
{
    DateTime Now();
    string Timezone();
    string Format(string fmt);
}

public sealed class TimeService : ITimeService
{
    public DateTime Now() => DateTime.Now;
    public string Timezone() => TimeZoneInfo.Local.DisplayName;
    public string Format(string f) => DateTime.Now.ToString(f);
}

public interface IDataService
{
    string Get(string id);
    IEnumerable<string> Keys();
    void Set(string id, string value);
}

public class InMemoryDataService : IDataService
{
    private readonly Dictionary<string, string> _store = new()
    {
        ["item-1"] = "First item",
        ["item-2"] = "Second item"
    };
    public string Get(string id) => _store.TryGetValue(id, out var v) ? v
        : throw new KeyNotFoundException(id);
    public IEnumerable<string> Keys() => _store.Keys;
    public void Set(string id, string v) => _store[id] = v;
}


// ═══════════════════════════════════════════════════════════════════════════
//  TOOLS — BASIC (static classes, method-parameter DI)
// ═══════════════════════════════════════════════════════════════════════════

[McpServerToolType]
public static class MathTools
{
    // ReadOnly=true  → no side effects → agents may auto-approve
    // Idempotent=true → same args always same result → safe to retry
    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Adds two integers")]
    public static int Add(
        [Description("First operand")] int a,
        [Description("Second operand")] int b) => a + b;

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Multiplies two integers")]
    public static int Multiply(
        [Description("First operand")] int a,
        [Description("Second operand")] int b) => a * b;
}

[McpServerToolType]
public static class TextTools
{
    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Reverses a string")]
    public static string Reverse(
        [Description("String to reverse")] string input)
        => new string(input.Reverse().ToArray());

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Counts the words in a string")]
    public static int CountWords(
        [Description("Text to analyse")] string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}


// ═══════════════════════════════════════════════════════════════════════════
//  TOOLS — DEPENDENCY INJECTION (non-static = constructor injection)
//  Services declared in the constructor are resolved from the DI container.
//  They are INVISIBLE to the caller — not in the tool's JSON Schema.
// ═══════════════════════════════════════════════════════════════════════════

[McpServerToolType]
public class TimeTools(ITimeService time, ILogger<TimeTools> logger)
{
    [McpServerTool(ReadOnly = true, Idempotent = false),
     Description("Returns current UTC time and local timezone")]
    public string GetCurrentTime()
    {
        logger.LogInformation("GetCurrentTime called");
        return $"UTC: {time.Now():o}\nTimezone: {time.Timezone()}";
    }

    [McpServerTool(ReadOnly = true, Idempotent = false),
     Description("Formats the current time with a .NET format string")]
    public string FormatTime(
        [Description("Format string, e.g. 'yyyy-MM-dd HH:mm:ss'")] string format)
        => time.Format(format);

    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Returns server runtime info")]
    public string GetServerInfo()
    {
        logger.LogInformation("GetServerInfo called at {Time}", time.Now());
        return $"Server time: {time.Now():o}\n" +
               $"Runtime:     .NET {Environment.Version}\n" +
               $"OS:          {Environment.OSVersion}";
    }
}

[McpServerToolType]
public class DataTools(IDataService data)
{
    // ReadOnly=true, Idempotent=true → safe for autonomous agents to call freely
    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Reads a data item by ID")]
    public string ReadItem([Description("Item ID")] string id)
    {
        try { return data.Get(id); }
        catch (KeyNotFoundException)
        {
            // McpException: message IS sent to the client inside IsError=true result
            // LLM sees it and can reason about the failure (e.g. try a different ID)
            throw new McpException($"Item '{id}' not found");
        }
    }

    // Destructive=false → additive only, never deletes
    [McpServerTool(ReadOnly = false, Destructive = false, Idempotent = false),
     Description("Stores a data item. Additive — never deletes.")]
    public string WriteItem(
        [Description("Item ID")] string id,
        [Description("Item value")] string value)
    {
        data.Set(id, value);
        return $"Stored '{id}'";
    }

    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Lists all item IDs")]
    public string ListItems() => string.Join(", ", data.Keys());
}


// ═══════════════════════════════════════════════════════════════════════════
//  TOOLS — ALL CONTENT BLOCK TYPES
//  Every type the MCP protocol supports, including content annotations.
// ═══════════════════════════════════════════════════════════════════════════

[McpServerToolType]
public static class ContentTypeTools
{
    // ── TEXT — string auto-wraps into TextContentBlock ────────────────────
    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Returns plain text. String is auto-wrapped by the SDK.")]
    public static string GetText(
        [Description("Message to echo")] string message)
        => $"Echo: {message}";

    // ── TEXT with content annotations ─────────────────────────────────────
    // Audience: who should see this — User (human) or Assistant (LLM only)
    // Priority: 0.0 = optional, 1.0 = critical
    [McpServerTool(ReadOnly = true),
     Description("Returns annotated text — LLM-only, high priority")]
    public static TextContentBlock GetAnnotatedText()
        => new()
        {
            Text = "This is for the LLM's reasoning only — do not show to the user.",
            Annotations = new Annotations
            {
                Audience = [Role.Assistant],  // LLM-only
                Priority = 0.9f               // near-critical
            }
        };

    // ── IMAGE ─────────────────────────────────────────────────────────────
    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Returns a small PNG image as an ImageContentBlock")]
    public static ImageContentBlock GetImage()
    {
        // Minimal valid 8×8 red PNG — replace with real image generation
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAFklEQVQI12P8" +
            "z8BQDwADhAF/Qc2rNAAAAABJRU5ErkJggg==");
        return ImageContentBlock.FromBytes(png, "image/png");
    }

    // ── AUDIO ─────────────────────────────────────────────────────────────
    [McpServerTool(ReadOnly = true),
     Description("Returns audio bytes as an AudioContentBlock")]
    public static AudioContentBlock GetAudio()
    {
        // Minimal WAV header — replace with real TTS or audio file bytes
        var wav = new byte[44];
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
        return AudioContentBlock.FromBytes(wav, "audio/wav");
    }

    // ── EMBEDDED RESOURCE — text ─────────────────────────────────────────
    [McpServerTool(ReadOnly = true),
     Description("Returns JSON config as an embedded text resource")]
    public static EmbeddedResourceBlock GetEmbeddedText()
        => new()
        {
            Resource = new TextResourceContents
            {
                Uri = "config://server/settings",
                MimeType = "application/json",
                Text = JsonSerializer.Serialize(
                    new { version = "1.0", darkMode = true },
                    new JsonSerializerOptions { WriteIndented = true })
            }
        };

    // ── EMBEDDED RESOURCE — binary blob ───────────────────────────────────
    [McpServerTool(ReadOnly = true),
     Description("Returns binary data as an embedded blob resource")]
    public static EmbeddedResourceBlock GetEmbeddedBlob()
    {
        byte[] data = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes as example
        return new EmbeddedResourceBlock
        {
            Resource = BlobResourceContents.FromBytes(
                data, "data://example/blob", "application/octet-stream")
        };
    }

    // ── MIXED — multiple content blocks in one result ─────────────────────
    [McpServerTool(ReadOnly = true),
     Description("Returns text + image + text in a single mixed result")]
    public static IEnumerable<ContentBlock> GetMixedContent()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAFklEQVQI12P8" +
            "z8BQDwADhAF/Qc2rNAAAAABJRU5ErkJggg==");
        return
        [
            new TextContentBlock
            {
                Text        = "Here is the image:",
                Annotations = new Annotations { Audience = [Role.User], Priority = 0.5f }
            },
            ImageContentBlock.FromBytes(png, "image/png"),
            new TextContentBlock { Text = "8×8 red PNG — demonstration only." }
        ];
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  TOOLS — ERROR HANDLING
//  Three distinct error tiers — behaviour differs on client:
//
//  McpException        → IsError=true in CallToolResult, MESSAGE is sent
//                         LLM can read it and recover (retry, try different args)
//
//  McpProtocolException → Propagates as JSON-RPC error (not IsError)
//                         Structural failure — client catches as McpProtocolException
//
//  Regular exception   → IsError=true, GENERIC message for security
//                         "An error occurred invoking 'tool_name'."
// ═══════════════════════════════════════════════════════════════════════════

[McpServerToolType]
public static class ErrorHandlingTools
{
    [McpServerTool(ReadOnly = true),
     Description("Demonstrates McpException: descriptive error reaches LLM in IsError result")]
    public static string Divide(
        [Description("Dividend")] double a,
        [Description("Divisor")] double b)
    {
        if (b == 0)
            throw new McpException("Division by zero is not allowed");
        return $"{a} / {b} = {a / b}";
    }

    [McpServerTool(ReadOnly = true),
     Description("Demonstrates McpProtocolException: propagates as JSON-RPC error -32602")]
    public static string ValidateInput(
        [Description("Input to validate")] string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new McpProtocolException("Input cannot be empty", McpErrorCode.InvalidParams);
        return $"Valid: '{input}'";
    }

    [McpServerTool(ReadOnly = true),
     Description("Demonstrates generic exception: secure — message is hidden from client")]
    public static string UnsafeOperation(
        [Description("Value")] double value)
    {
        if (value < 0)
            throw new ArgumentException("Negative values not supported internally");
        return $"Processed: {value}";
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  TOOLS — RUNTIME INJECTION
//  McpServer, IProgress<ProgressNotificationValue>, CancellationToken
//  are injected by the MCP runtime — not from DI, not from the caller.
//  They are INVISIBLE to the caller — not in the tool's JSON Schema.
// ═══════════════════════════════════════════════════════════════════════════

[McpServerToolType]
public static class RuntimeTools
{
    // ── McpServer — access the live session ──────────────────────────────
    [McpServerTool(ReadOnly = true, Idempotent = true),
     Description("Returns info about the connected client by inspecting the McpServer session")]
    public static string GetClientInfo(McpServer server)
    {
        var info = server.ClientInfo;
        var caps = server.ClientCapabilities;
        return $"Client:      {info?.Name} {info?.Version}\n" +
               $"Roots:       {caps?.Roots is not null}\n" +
               $"Sampling:    {caps?.Sampling is not null}\n" +
               $"Elicitation: {caps?.Elicitation is not null}";
    }

    // ── IProgress + CancellationToken ─────────────────────────────────────
    // Progress is sent only if the client included a progressToken in the request.
    // The SDK handles token matching — just call progress.Report().
    [McpServerTool(ReadOnly = true),
     Description("Long-running: reports progress per step. Client must send progressToken.")]
    public static async Task<string> RunWithProgress(
        [Description("Steps to simulate (1–10)")] int steps,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        steps = Math.Clamp(steps, 1, 10);
        for (int i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();       // respect client cancel
            await Task.Delay(500, ct);
            progress.Report(new ProgressNotificationValue
            {
                Progress = i,
                Total = steps,
                Message = $"Step {i} of {steps} complete"
            });
        }
        return $"Finished {steps} steps.";
    }

    // ── All three together ────────────────────────────────────────────────
    [McpServerTool(ReadOnly = true),
     Description("Processes items with progress, cancellation, and client identity")]
    public static async Task<string> ProcessItems(
        [Description("Comma-separated items")] string items,
        McpServer server,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        var list = items.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                       StringSplitOptions.TrimEntries);
        var results = new List<string>();

        for (int i = 0; i < list.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(300, ct);
            results.Add($"[{list[i].ToUpper()}]");
            progress.Report(new ProgressNotificationValue
            {
                Progress = i + 1,
                Total = list.Length,
                Message = $"Processed '{list[i]}' ({i + 1}/{list.Length})"
            });
        }

        return $"Done for {server.ClientInfo?.Name ?? "unknown"}:\n" +
               string.Join(", ", results);
    }

    // ── Server-to-client logging ──────────────────────────────────────────
    // server.AsClientLoggerProvider() wraps notifications/message into ILogger.
    // Client must register NotificationMethods.LoggingMessageNotification handler.
    // Client controls minimum level via SetLoggingLevelAsync().
    [McpServerTool(ReadOnly = true),
     Description("Streams structured log entries to the client over MCP notifications/message")]
    public static async Task<string> StreamLogs(
        McpServer server,
        [Description("Label for log category")] string label,
        CancellationToken ct)
    {
        var logger = server.AsClientLoggerProvider().CreateLogger(label);

        logger.LogInformation("Starting '{Label}'", label);
        await Task.Delay(150, ct);
        logger.LogDebug("Step 1 of 3 — debug detail");
        await Task.Delay(150, ct);
        logger.LogWarning("Step 2 produced a non-critical warning");
        await Task.Delay(150, ct);
        logger.LogInformation("'{Label}' complete", label);

        return $"Sent 4 log entries to client under logger '{label}'.";
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  TOOLS — ELICITATION (form mode)
//  Server pauses mid-execution, sends a JSON Schema to the client,
//  client renders prompts for the user, returns typed values, tool resumes.
//
//  Schema types:
//    BooleanSchema                  — true/false
//    StringSchema                   — free text, optional MinLength/MaxLength
//    NumberSchema                   — numeric
//    UntitledSingleSelectEnumSchema — value = display text
//    TitledSingleSelectEnumSchema   — value (const) ≠ display (title)
//
//  User actions: "accept" · "decline" · "cancel"
// ═══════════════════════════════════════════════════════════════════════════

[McpServerToolType]
public static class ElicitationTools
{
    [McpServerTool(ReadOnly = false, Destructive = true),
     Description("Deploys a service. Elicits environment + version, " +
                 "then requires production confirmation.")]
    public static async Task<string> DeployService(
        McpServer server,
        [Description("Service name")] string service,
        CancellationToken ct)
    {
        if (server.ClientCapabilities?.Elicitation is null)
            throw new McpException("Client does not support elicitation.");

        // Step 1: TitledSingleSelectEnumSchema — const ≠ display title
        var step1 = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = $"Configure deployment for '{service}'",
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
                            {
                                Const = "dev", Title = "Development"
                            },
                            new ()
                            {
                                Const = "stg", Title = "Staging"
                            },
                            new ()
                            {
                                Const = "prd", Title = "⚠️  Production"
                            }
                        ]
                    },
                    ["version"] = new ElicitRequestParams.StringSchema
                    {
                        Description = "Version tag (e.g. v1.2.3)",
                        MinLength   = 1
                    }
                }
            }
        }, ct);

        if (step1.Action == "cancel") return "Deployment cancelled.";
        if (step1.Action != "accept" || step1.Content is null)
            return $"Deployment declined ({step1.Action}).";

        var env = step1.Content["environment"].GetString() ?? "dev";
        var version = step1.Content.TryGetValue("version", out var v)
            ? v.GetString() ?? "latest" : "latest";

        // Step 2: extra boolean confirmation for production only
        if (env == "prd")
        {
            var step2 = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "⚠️  PRODUCTION deployment — confirm?",
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

            var ok = step2.Action == "accept" &&
                     step2.Content?.TryGetValue("confirmed", out var c) is true &&
                     c.ValueKind == System.Text.Json.JsonValueKind.True;

            if (!ok) return "Production deployment aborted.";
        }

        await Task.Delay(300, ct);
        return $"✓ Deployed '{service}' {version} → {env} at {DateTime.UtcNow:HH:mm:ss} UTC.";
    }

    // Simple single-step elicitation with UntitledSingleSelectEnum + Boolean
    [McpServerTool(ReadOnly = true),
     Description("Generates a report. Elicits format and options from user.")]
    public static async Task<string> GenerateReport(
        McpServer server,
        CancellationToken ct)
    {
        if (server.ClientCapabilities?.Elicitation is null)
            return $"Server OK at {DateTime.UtcNow:HH:mm:ss} (no elicitation support)";

        var result = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = "Configure your report",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties =
                {
                    ["format"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
                    {
                        Description = "Output format",
                        Enum        = ["summary", "detailed", "json"]
                    },
                    ["includeHistory"] = new ElicitRequestParams.BooleanSchema
                    {
                        Description = "Include historical trend data"
                    }
                }
            }
        }, ct);

        if (result.Action != "accept" || result.Content is null)
            return "Report cancelled.";

        var fmt = result.Content.TryGetValue("format", out var f) ? f.GetString() ?? "summary" : "summary";
        var history = result.Content.TryGetValue("includeHistory", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.True;

        return fmt switch
        {
            "json" => JsonSerializer.Serialize(new
            {
                generatedAt = DateTime.UtcNow,
                format = fmt,
                includeHistory = history,
                data = history ? "trend data would appear here" : null
            }, new JsonSerializerOptions { WriteIndented = true }),
            "detailed" => $"DETAILED REPORT — {DateTime.UtcNow:o}\n" +
                          (history ? "History: [trend data]\n" : ""),
            _ => $"Summary report at {DateTime.UtcNow:HH:mm:ss}"
        };
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  RESOURCES
// ═══════════════════════════════════════════════════════════════════════════

[McpServerResourceType]
public static class ServerResources
{
    // ── Direct resource — fixed URI, always appears in resources/list ─────
    [McpServerResource(UriTemplate = "status://server", Name = "Server Status", MimeType = "application/json")]
    [Description("Current server status — no parameters required")]
    public static string GetStatus()
        => JsonSerializer.Serialize(new
        {
            status = "running",
            time = DateTime.UtcNow.ToString("o"),
            dotnet = Environment.Version.ToString(),
            os = Environment.OSVersion.ToString()
        }, new JsonSerializerOptions { WriteIndented = true });

    // ── Template resource — parameterised, appears in resources/templates/list ──
    // {number} is extracted from the URI and passed as a typed method parameter
    [McpServerResource(UriTemplate = "math://times-table/{number}", Name = "Times Table", MimeType = "text/plain")]
    [Description("Multiplication table for a number. URI: math://times-table/7")]
    public static TextResourceContents GetTimesTable(int number)
        => new()
        {
            Uri = $"math://times-table/{number}",
            MimeType = "text/plain",
            Text = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"{number} x {i,2} = {number * i}"))
        };

    // ── Template resource with DI ─────────────────────────────────────────
    [McpServerResource(UriTemplate = "data://items/{id}", Name = "Data Item", MimeType = "text/plain")]
    [Description("A stored data item. URI: data://items/item-1")]
    public static TextResourceContents GetItem(IDataService data, string id)
        => new()
        {
            Uri = $"data://items/{id}",
            MimeType = "text/plain",
            Text = data.Get(id)
        };

    // ── Binary blob resource ──────────────────────────────────────────────
    [McpServerResource(UriTemplate = "binary://example/{name}", Name = "Binary Data", MimeType = "application/octet-stream")]
    [Description("Binary blob resource. URI: binary://example/test")]
    public static BlobResourceContents GetBinary(string name)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes($"Binary: {name}");
        return new BlobResourceContents
        {
            Uri = $"binary://example/{name}",
            MimeType = "application/octet-stream",
            Blob = data
        };
    }
}


// ═══════════════════════════════════════════════════════════════════════════
//  PROMPTS
// ═══════════════════════════════════════════════════════════════════════════

[McpServerPromptType]
public static class ServerPrompts
{
    // ── No arguments ──────────────────────────────────────────────────────
    [McpServerPrompt, Description("System init — grounds the LLM in its role and safety rules")]
    public static ChatMessage SystemInit()
        => new(ChatRole.User,
            "You are connected to McpServerStdio. " +
            "Use tools for real data. Never answer from memory. " +
            "Tools marked Destructive=true require explicit user confirmation.");

    // ── Required arguments, multi-turn ───────────────────────────────────
    [McpServerPrompt, Description("Code review conversation — language and code snippet required")]
    public static IEnumerable<ChatMessage> ReviewCode(
        [Description("Programming language")] string language,
        [Description("Code snippet to review")] string code)
        =>
        [
            new(ChatRole.User,
                $"Review this {language} code:\n\n```{language}\n{code}\n```"),
            new(ChatRole.Assistant,
                $"I'll review this {language} snippet for correctness, style, and improvements.")
        ];

    // ── Optional argument ─────────────────────────────────────────────────
    [McpServerPrompt, Description("Health check prompt, optionally scoped to a time window")]
    public static ChatMessage HealthCheck(
        [Description("Time window, e.g. '1h' (optional)")] string? window = null)
        => new(ChatRole.User,
            $"Check server health{(window is not null ? $" for the past {window}" : "")}. " +
            "Use run_with_progress and get_client_info tools.");
}