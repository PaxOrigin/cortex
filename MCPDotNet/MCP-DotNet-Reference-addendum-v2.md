# MCP Book — Chapters 5 & 6 Addendum (v2 — SDK-verified)
### Building MCP Servers: Primitives, Utilities & Client Capabilities
#### .NET / C# — verified against ModelContextProtocol SDK docs

> **SDK reference:** https://csharp.sdk.modelcontextprotocol.io  
> All snippets verified against the official API documentation.

---

## Table of Contents

1. [Chapter 5 — Prompts](#chapter-5--prompts)
   - [Prompt Return Types](#prompt-return-types)
   - [Multi-Turn Prompts & Prefilling](#multi-turn-prompts--prefilling)
   - [Prompts that Force Tool Use](#prompts-that-force-tool-use)
   - [ResourceLink in Prompts](#resourcelink-in-prompts)
2. [Chapter 5 — Resources](#chapter-5--resources)
3. [Chapter 6 — Server Utilities](#chapter-6--server-utilities)
   - [Completions](#completions)
   - [The Context Object (McpServer)](#the-context-object-mcpserver)
   - [Logging](#logging)
   - [Progress Notifications](#progress-notifications)
   - [Manual Primitive Notifications](#manual-primitive-notifications)
   - [Pagination](#pagination)
4. [Chapter 6 — Client Capabilities](#chapter-6--client-capabilities)
   - [Detecting Client Capabilities](#detecting-client-capabilities)
   - [Elicitations](#elicitations)
   - [Sampling](#sampling)
   - [Roots](#roots)
   - [Request Cancellation](#request-cancellation)
5. [SDK Quick Reference](#sdk-quick-reference)

---

## Chapter 5 — Prompts

### Prompt Engineering Principles

The book references *Prompt Engineering for Generative AI* (O'Reilly) and its **Five Principles**:

| # | Principle | Server Design Implication |
|---|-----------|--------------------------|
| 1 | **Give Direction** | Explicit: persona, task, constraints. Never leave the LLM guessing. |
| 2 | **Specify Format** | Use `AssistantMessage` prefill to enforce output format. |
| 3 | **Provide Examples** | Few-shot via multi-turn: `[User(example), Assistant(response), User(real)]`. |
| 4 | **Evaluate Quality** | Test across models — your server is model-agnostic. |
| 5 | **Divide Labor** | Split large prompts. Chain via Sampling (Ch.6). |

Additional rules:
- Never phrase things negatively. State what the model *should* do.
- Use precise quantities: not "fairly long" → "2 paragraphs of 3–5 sentences each".
- Use separators: `###` for OpenAI, `<xml-tags>` for Anthropic. XML degrades gracefully on other models.

---

### Prompt Return Types

Prompts in the .NET SDK return either `ChatMessage` / `IEnumerable<ChatMessage>` (simple text/image)
or `PromptMessage` / `IEnumerable<PromptMessage>` (when protocol-specific blocks like
`EmbeddedResourceBlock` are needed). **Do not wrap in `GetPromptResult`** — return the messages directly.

```csharp
[McpServerPromptType]
public static class MyPrompts
{
    // 1. Single ChatMessage — simplest form
    [McpServerPrompt, Description("Greets the user.")]
    public static ChatMessage Hello() =>
        new(ChatRole.User, "Say hello to the user.");

    // 2. Parametrized
    [McpServerPrompt, Description("Greets user by name.")]
    public static ChatMessage Greet(string username) =>
        new(ChatRole.User, $"Say hello to {username}");

    // 3. With XML tags (Anthropic-style, degrades gracefully elsewhere)
    [McpServerPrompt, Description("Summarizes text into 3 main ideas.")]
    public static ChatMessage Summarize(string userText) =>
        new(ChatRole.User, $"""
            <instruction>
            Create a list of 3 main ideas from the following text:
            </instruction>
            <text>
            {userText}
            </text>
            """);
}
```

**Registration:**
```csharp
builder.Services
    .AddMcpServer()
    .WithPrompts<MyPrompts>();
```

> **Note on SystemMessage:** Neither the Python SDK nor the .NET SDK has a `SystemMessage`/`ChatRole.System`
> in `PromptMessage`. To serve a system prompt, document the intent in the `Description` field.

---

### Multi-Turn Prompts & Prefilling

Return `IEnumerable<ChatMessage>` to create multi-turn prompts. The last message can be
from `ChatRole.Assistant` to prefill the model's response (forces format, reduces verbosity).

```csharp
[McpServerPrompt, Description("Returns a prefilled numbered-list prompt.")]
public static IEnumerable<ChatMessage> MultiTurnSummarize(int count, string userText) =>
[
    new(ChatRole.User, $"""
        <instruction>Create a list of {count} main ideas:</instruction>
        <text>{userText}</text>
        """),

    // Prefill: model continues from here, enforcing the numbered-list format
    new(ChatRole.Assistant, $"Here are {count} main ideas from the text:\n1.")
];
```

**Why prefilling matters:**
- Enforces output format without lengthy instructions.
- Especially effective with Anthropic models (they continue from the `assistant` turn directly).
- Combine with XML tags for maximum control.

---

### Prompts that Force Tool Use

| Strategy | Mechanism | Control Level |
|----------|-----------|---------------|
| `request_tool_use` | Append instruction in UserMessage | Soft — model may ignore |
| `force_tool_use` | Call tool logic directly, prefill AssistantMessage | Hard — bypasses model |

```csharp
// Soft — ask the model to use a tool
[McpServerPrompt, Description("Asks model to use the analyze_sentiment tool.")]
public static ChatMessage RequestToolUse(string userRequest) =>
    new(ChatRole.User, $"""
        <user_request>{userRequest}</user_request>
        <tool_instruction>
        Use the analyze_sentiment tool to evaluate the request's sentiment
        and tailor your response to move it toward neutral.
        </tool_instruction>
        """);

// Hard — call tool logic directly, prefill result into assistant turn
[McpServerPrompt, Description("Forces tool result into assistant turn via prefill.")]
public static IEnumerable<ChatMessage> ForceToolUse(string userRequest)
{
    var sentiment = SentimentAnalyzer.Analyze(); // same logic as the [McpServerTool]

    return
    [
        new(ChatRole.User, userRequest),
        new(ChatRole.Assistant, $"Your request was {sentiment}, let's ")
        // Model continues from here
    ];
}
```

---

### ResourceLink in Prompts

Use `EmbeddedResourceBlock` inside a `PromptMessage` (not `ChatMessage`) to embed a
resource reference. The client is expected to resolve and inject the resource content.

```csharp
[McpServerPrompt, Description("Answers a user request using the knowledge base.")]
public static IEnumerable<PromptMessage> KnowledgeBasePrompt(string userRequest)
{
    // Load content to embed (or let the client resolve it via URI)
    string content = File.ReadAllText("knowledge.txt");

    return
    [
        new PromptMessage
        {
            Role = Role.User,
            Content = new TextContentBlock { Text = userRequest }
        },
        new PromptMessage
        {
            Role = Role.User,
            Content = new TextContentBlock
            {
                Text = "Use the following knowledge base to answer the request:"
            }
        },
        new PromptMessage
        {
            Role = Role.User,
            Content = new EmbeddedResourceBlock
            {
                Resource = new TextResourceContents
                {
                    Uri = "file://knowledge.txt",
                    MimeType = "text/plain",
                    Text = content
                }
            }
        }
    ];
}
```

For binary resources (e.g., PDF, image):
```csharp
new EmbeddedResourceBlock
{
    Resource = BlobResourceContents.FromBytes(pdfBytes, "data://report.pdf", "application/pdf")
}
```

> **Rule of thumb:** Use `ChatMessage` when content is text or image (simple).
> Use `PromptMessage` when you need `EmbeddedResourceBlock` or other protocol-specific content blocks.

---

## Chapter 5 — Resources

### Decorators & URI Templates

```csharp
[McpServerResourceType]
public static class MyResources
{
    // Direct resource (fixed URI)
    [McpServerResource(UriTemplate = "config://app/settings",
        Name = "App Settings", MimeType = "application/json")]
    [Description("Returns application configuration settings.")]
    public static string GetSettings() =>
        JsonSerializer.Serialize(new { theme = "dark", language = "en" });

    // Template resource (parametrized URI)
    [McpServerResource(UriTemplate = "docs://articles/{id}", Name = "Article")]
    [Description("Returns an article by its ID.")]
    public static ResourceContents GetArticle(string id)
    {
        string? content = LoadArticle(id);
        if (content is null) throw new McpException($"Article not found: {id}");

        return new TextResourceContents
        {
            Uri = $"docs://articles/{id}",
            MimeType = "text/plain",
            Text = content
        };
    }

    // Binary resource
    [McpServerResource(UriTemplate = "images://photos/{id}", Name = "Photo")]
    [Description("Returns a photo by ID.")]
    public static BlobResourceContents GetPhoto(int id)
    {
        byte[] imageData = LoadPhoto(id);
        return BlobResourceContents.FromBytes(imageData, $"images://photos/{id}", "image/png");
    }
}
```

### Resource Subscriptions

The SDK exposes `WithSubscribeToResourcesHandler` and `WithUnsubscribeFromResourcesHandler`
on the builder. After a client subscribes, send `notifications/resources/updated` when the
resource changes. The `McpClient` side provides `SubscribeToResourceAsync` which returns
an `IAsyncDisposable` that manages both subscription and notification handler.

```csharp
// Server: register subscription handlers
builder.Services.AddMcpServer()
    .WithSubscribeToResourcesHandler(async (request, ct) =>
    {
        subscriptionStore.Add(request.Params!.Uri);
        return new EmptyResult();
    })
    .WithUnsubscribeFromResourcesHandler(async (request, ct) =>
    {
        subscriptionStore.Remove(request.Params!.Uri);
        return new EmptyResult();
    });

// Server: notify when resource changes
[McpServerTool(Name = "add_fact")]
public static async Task AddFact(string fact, McpServer server, CancellationToken ct)
{
    await File.AppendAllTextAsync("knowledge.txt", fact + "\n", ct);
    await server.SendNotificationAsync(
        NotificationMethods.ResourceUpdatedNotification,
        new ResourceUpdatedNotificationParams { Uri = "file://knowledge.txt" },
        ct);
}
```

---

## Chapter 6 — Server Utilities

### Completions

> ✅ **This is NOT a gap.** The SDK exposes `WithCompleteHandler` directly on the builder.

Completions provide autocomplete suggestions for prompt arguments and resource template
parameters as the user types. The handler receives `CompleteRequestParams` and returns
`CompleteResult`.

```csharp
builder.Services.AddMcpServer()
    .WithPrompts<MyPrompts>()
    .WithCompleteHandler(async (request, ct) =>
    {
        var suggestions = new List<string>();
        var partial = request.Params?.Argument?.Value ?? "";

        // Route by ref type: prompt vs resource template
        if (request.Params?.Ref is PromptReference promptRef)
        {
            if (promptRef.Name == "greet")
            {
                var defaults = new[] { "alice", "bob", "user" };
                suggestions = defaults
                    .Where(s => s.Contains(partial, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }
        else if (request.Params?.Ref is ResourceTemplateReference resourceRef)
        {
            if (resourceRef.Uri == "docs://articles/{id}")
            {
                var knownIds = new[] { "intro", "quickstart", "advanced" };
                suggestions = knownIds
                    .Where(s => s.Contains(partial, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        return new CompleteResult
        {
            Completion = new CompletionValues
            {
                Values = suggestions.Take(100).ToList(), // max 100
                Total = suggestions.Count,
                HasMore = suggestions.Count > 100
            }
        };
    });
```

**Best practices:**
- Return the most relevant suggestions first.
- Validate partial input against prompt injection.
- Never return sensitive data (credentials, API keys) as suggestions.
- Consider rate limiting if you expect heavy usage.

---

### The Context Object (McpServer)

In Python, `ctx: Context` is a single object injected into handlers.
In .NET, capabilities are split into separate injectable parameters — no single "context object".
The primary one is `McpServer` (the concrete type, not an interface).

```csharp
[McpServerTool, Description("Returns info about the connected client.")]
public static string GetClientInfo(McpServer server)
{
    var info = server.ClientInfo;
    var caps = server.ClientCapabilities;

    return $"""
        Client:      {info?.Name} {info?.Version}
        Roots:       {caps?.Roots       is not null}
        Sampling:    {caps?.Sampling    is not null}
        Elicitation: {caps?.Elicitation is not null}
        """;
}
```

**Context parameter injection map:**

| Python `ctx.*` | .NET Injectable Parameter |
|----------------|--------------------------|
| `ctx.fastmcp.name` | `McpServer.ServerOptions.ServerInfo?.Name` |
| `ctx.session.client_params.capabilities` | `McpServer.ClientCapabilities` |
| `ctx.fastmcp` server info | `McpServer.ClientInfo` |
| Logging | `ILogger<T>` + `McpServer` log notifications |
| `ctx.report_progress(...)` | `IProgress<ProgressNotificationValue>` |
| `ctx.session.elicit(...)` | `McpServer.ElicitAsync(...)` |
| `ctx.session.create_message(...)` | `McpServer.SampleAsync(...)` |
| `ctx.session.list_roots()` | `McpServer.RequestRootsAsync()` |
| Lifespan context | DI singletons (`IServiceCollection`) |

**Lifespan management — .NET idiom:**

Python uses `@asynccontextmanager` yielding a dict. In .NET, use **DI singletons** for
the same effect. Expensive resources (DB connections, caches) live as singletons registered
before `AddMcpServer()`:

```csharp
// Program.cs
builder.Services.AddSingleton<KnowledgeBaseCache>();
builder.Services.AddMcpServer().WithTools<MyTools>();

// Tool: receives it via DI (equivalent of lifespan context)
[McpServerTool]
public static string QueryKb(string query, KnowledgeBaseCache cache) =>
    cache.Search(query);
```

---

### Logging

Two distinct mechanisms:

**Option A — Standard `ILogger<T>` (server-side only):**
```csharp
[McpServerTool]
public static string MyTool(string input, ILogger<MyTools> logger)
{
    logger.LogInformation("Tool called with {Input}", input);
    return $"processed: {input}";
}
```

**Option B — MCP log notifications to client via `McpServer.AsClientLoggerProvider()`:**

The SDK provides `AsClientLoggerProvider()` as the idiomatic way to send
`notifications/message` to the connected client. The client controls the minimum
log level via `SetLoggingLevelAsync()`.

```csharp
[McpServerTool]
public static async Task<string> MyTool(
    string input,
    McpServer server,
    CancellationToken ct)
{
    // Create a logger that sends notifications to the client
    var loggerFactory = server.AsClientLoggerProvider();
    var logger = loggerFactory.CreateLogger("MyTools");

    logger.LogInformation("Tool called at {Time}", DateTime.UtcNow);

    // ... do work ...

    logger.LogDebug("Result computed");
    return "done";
}
```

> **Recommendation:** Use `ILogger<T>` for local observability (OpenTelemetry, file sinks).
> Use `AsClientLoggerProvider()` when the client needs to display logs (e.g., MCP Inspector).

---

### Progress Notifications

> ✅ **Already implemented correctly.** This is the SDK v1.x idiomatic pattern.

Declare `IProgress<ProgressNotificationValue>` as a parameter — the MCP runtime injects it.
It only sends notifications if the client included a `progressToken` in the request.

**Server side:**
```csharp
[McpServerTool, Description("Long-running operation with progress reporting.")]
public static async Task<string> SlowOperation(
    int steps,
    IProgress<ProgressNotificationValue> progress,  // injected by MCP runtime
    CancellationToken ct)
{
    steps = Math.Clamp(steps, 1, 10);
    int reportEvery = Math.Max(1, steps / 10);

    for (int i = 1; i <= steps; i++)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Delay(500, ct);

        if (i % reportEvery == 0)
        {
            progress.Report(new ProgressNotificationValue
            {
                Progress = i,
                Total    = steps,
                Message  = $"Step {i} of {steps} complete"
            });
        }
    }

    return $"Finished {steps} steps.";
}
```

**Client side:**
```csharp
// SDK manages progressToken automatically when you pass Progress<T>
await client.CallToolAsync(
    "slow_operation",
    new Dictionary<string, object?> { ["steps"] = 8 },
    progress: new Progress<ProgressNotificationValue>(v =>
        Console.WriteLine($"  [{v.Progress}/{v.Total}] {v.Message}")));
```

**Why this is better than the Python approach:**

| | Python SDK | .NET SDK |
|---|---|---|
| Token management | Manual | Automatic |
| Type safety | `dict` | `ProgressNotificationValue` (typed) |
| Scoping | Global handler | Per-call `Progress<T>` |
| Boilerplate | Medium | Zero |

---

### Manual Primitive Notifications

Use `McpServer.SendNotificationAsync()` with the appropriate notification method constant
and params type. All method strings are available as constants in `NotificationMethods`.

```csharp
// Tool list changed
await server.SendNotificationAsync(
    NotificationMethods.ToolListChangedNotification, ct: ct);

// Prompt list changed
await server.SendNotificationAsync(
    NotificationMethods.PromptListChangedNotification, ct: ct);

// Resource list changed
await server.SendNotificationAsync(
    NotificationMethods.ResourceListChangedNotification, ct: ct);

// Specific resource updated
await server.SendNotificationAsync(
    NotificationMethods.ResourceUpdatedNotification,
    new ResourceUpdatedNotificationParams { Uri = "file://knowledge.txt" },
    ct);
```

**Dynamic primitive removal pattern:**
```csharp
// Maintain a runtime registry as a singleton
public class DynamicToolRegistry(IEnumerable<McpServerTool> initial)
{
    private readonly Dictionary<string, McpServerTool> _tools =
        initial.ToDictionary(t => t.ProtocolTool.Name);

    public bool Remove(string name) => _tools.Remove(name);
}

[McpServerTool(Name = "remove_tool")]
public static async Task RemoveTool(
    string toolName,
    DynamicToolRegistry registry,
    McpServer server,
    CancellationToken ct)
{
    if (!registry.Remove(toolName))
        throw new McpException($"Tool '{toolName}' not found.");

    await server.SendNotificationAsync(
        NotificationMethods.ToolListChangedNotification, ct: ct);
}
```

---

### Pagination

The SDK exposes `WithListResourcesHandler`, `WithListToolsHandler`, `WithListPromptsHandler`
directly on the builder, all supporting cursor-based pagination via `NextCursor`.

```csharp
const int PageSize = 100;
static readonly List<Resource> AllResources =
    Enumerable.Range(0, 1000)
        .Select(i => new Resource
        {
            Uri  = $"resource://{i}",
            Name = $"Resource {i}",
            MimeType = "text/plain"
        })
        .ToList();

builder.Services.AddMcpServer()
    .WithListResourcesHandler((request, ct) =>
    {
        int start = 0;
        if (request.Params?.Cursor is string cursor
            && int.TryParse(cursor, out int parsed))
            start = parsed;

        int end = Math.Min(start + PageSize, AllResources.Count);
        string? nextCursor = end < AllResources.Count ? end.ToString() : null;

        return ValueTask.FromResult(new ListResourcesResult
        {
            Resources  = AllResources.Skip(start).Take(PageSize).ToList(),
            NextCursor = nextCursor
        });
    });
```

> **When to paginate:** Rarely needed until hundreds/thousands of items.
> Returning many tools degrades LLM precision — design lean servers.

---

## Chapter 6 — Client Capabilities

### Detecting Client Capabilities

```csharp
// Via McpServer.ClientCapabilities (populated during Initialize handshake)
[McpServerTool]
public static async Task<string> MyTool(McpServer server, CancellationToken ct)
{
    if (server.ClientCapabilities?.Sampling is null)
        return "Error: this client does not support sampling.";

    // proceed...
}

// Defensive try/catch for cases where you don't pre-check
try
{
    var result = await server.SampleAsync(request, ct);
    return result.Content?.Text ?? "No content";
}
catch (McpException ex) when (ex.ErrorCode == McpErrorCode.InvalidRequest)
{
    return "Sampling not available. Fallback: " + ComputeLocally();
}
```

---

### Elicitations

Already implemented. Key recap:

```csharp
var result = await server.ElicitAsync(
    new ElicitRequestParams
    {
        Message         = "Please provide your information:",
        RequestedSchema = formSchema  // JSON Schema object
    }, ct);

return result.Action switch
{
    "accept"  => $"Thanks, {result.Content?["name"]}!",
    "decline" => "No problem, you declined.",
    "cancel"  => "Signup cancelled.",
    _         => "Unexpected response."
};
```

**Schema constraints (primitive JSON types only):**

| Type | Extra properties |
|------|-----------------|
| `string` | `minLength`, `maxLength`, `pattern`, `format` |
| `number` | `minimum`, `maximum` |
| `boolean` | — |
| `enum` | `enum` (values list), `enumNames` (display labels) |

**URL-mode elicitation** (new in SDK — for OAuth flows, payments):
```csharp
// Throw when stateless mode prevents server-to-client requests
throw new UrlElicitationRequiredException(
    "Authorization required.",
    [new ElicitRequestParams
    {
        Mode         = "url",
        ElicitationId = Guid.NewGuid().ToString(),
        Url          = "https://auth.example.com/connect?id=...",
        Message      = "Please authorize access."
    }]);
```

---

### Sampling

`McpServer.SampleAsync()` lets the server call the client's LLM.

```csharp
[McpServerTool, Description("Uses LLM sampling to explain a math operation.")]
public static async Task<string> ExplainMath(
    string operation,
    McpServer server,
    CancellationToken ct)
{
    if (server.ClientCapabilities?.Sampling is null)
        return "Sampling not supported by this client.";

    try
    {
        var result = await server.SampleAsync(
            new CreateMessageRequestParams
            {
                Messages =
                [
                    new SamplingMessage
                    {
                        Role    = Role.User,
                        Content = new TextContentBlock
                        {
                            Text = $"""
                                Explain how this math operation works.
                                Break into discrete steps.
                                Operation: {operation}
                                Voice: patient but eccentric math professor.
                                """
                        }
                    }
                ],
                MaxTokens        = 500,
                ModelPreferences = new ModelPreferences
                {
                    Hints =
                    [
                        new ModelHint { Name = "claude-haiku" },
                        new ModelHint { Name = "gpt-4o-mini" }
                    ],
                    CostPriority         = 1.0f,
                    SpeedPriority        = 0.8f,
                    IntelligencePriority = 0.3f
                }
            }, ct);

        return result.Content switch
        {
            TextContentBlock text  => text.Text,
            ImageContentBlock img  => $"[image: {img.MimeType}]",
            _                      => "Unexpected content type."
        };
    }
    catch (Exception ex)
    {
        return $"Sampling failed: {ex.Message}";
    }
}
```

**Client-side sampling handler** (bridge to an `IChatClient`):
```csharp
// The SDK provides a convenience extension that wires up IChatClient → SamplingHandler
var clientOptions = new McpClientOptions
{
    Capabilities = new ClientCapabilities
    {
        Sampling = new SamplingCapability()
    }
};

var handlers = new McpClientHandlers
{
    // AIContentExtensions.CreateSamplingHandler bridges IChatClient to MCP sampling
    SamplingHandler = chatClient.CreateSamplingHandler()
};
```

**Model Preferences:**

| Property | Range | Meaning |
|----------|-------|---------|
| `CostPriority` | 0.0–1.0 | Higher = prefer cheaper |
| `SpeedPriority` | 0.0–1.0 | Higher = prefer faster |
| `IntelligencePriority` | 0.0–1.0 | Higher = prefer smarter |

Hints are ordered suggestions. The client is not required to respect them.

> **Security:** Always validate and sanitize inputs interpolated into sampling prompts.
> Prompt injection via user-supplied parameters is a real attack surface.

---

### Roots

Roots define which filesystem paths the client makes available. They are a **coordination
mechanism**, not a hard security boundary — but your server should respect them.

**Method name:** `McpServer.RequestRootsAsync()` (not `ListRootsAsync`).

```csharp
// Singleton cache — invalidated on roots/list_changed notification
public class RootsCache
{
    private List<Root> _roots = [];
    public bool IsEmpty    => _roots.Count == 0;
    public void Clear()    => _roots.Clear();
    public void Set(IEnumerable<Root> roots) => _roots = roots.ToList();
    public IReadOnlyList<Root> All => _roots;
}

[McpServerTool, Description("Counts files in a directory (must be within allowed roots).")]
public static async Task<string> CountFiles(
    string filePath,
    McpServer server,
    RootsCache cache,
    CancellationToken ct)
{
    // Populate cache on first call
    if (cache.IsEmpty)
    {
        var result = await server.RequestRootsAsync(cancellationToken: ct);
        cache.Set(result.Roots);
    }

    // Validate path is within a root
    var absPath = Path.GetFullPath(filePath);
    bool isAllowed = cache.All.Any(root =>
    {
        var rootPath = Path.GetFullPath(new Uri(root.Uri).LocalPath);
        return absPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
    });

    if (!isAllowed)
        throw new UnauthorizedAccessException($"{filePath} is outside allowed roots.");

    if (!Directory.Exists(absPath))
        throw new DirectoryNotFoundException($"{filePath} is not a directory.");

    var count = Directory.GetFiles(absPath).Length;
    return $"There are {count} files in {filePath}";
}
```

**Handle roots/list_changed — invalidate cache:**
```csharp
// In Program.cs or server setup
builder.Services.AddMcpServer()
    .WithNotificationHandler(
        NotificationMethods.RootsListChangedNotification,
        (notification, ct) =>
        {
            app.Services.GetRequiredService<RootsCache>().Clear();
            return ValueTask.CompletedTask;
        });
```

---

### Request Cancellation

In .NET, `CancellationToken` is the idiomatic mechanism. The SDK links the MCP
`notifications/cancelled` message to the token passed to tool methods.

**Receiving cancellations (automatic via `CancellationToken`):**
```csharp
[McpServerTool]
public static async Task<string> SlowOperation(int steps, CancellationToken ct)
{
    for (int i = 0; i < steps; i++)
    {
        ct.ThrowIfCancellationRequested(); // exits cleanly on client cancel
        await Task.Delay(500, ct);
    }
    return "Done.";
}
```

**Sending cancellation to client (e.g., after sampling timeout):**
```csharp
[McpServerTool]
public static async Task<string> SamplingWithTimeout(McpServer server, CancellationToken ct)
{
    if (server.ClientCapabilities?.Sampling is null)
        return "Sampling not supported.";

    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

    try
    {
        var result = await server.SampleAsync(/* ... */, linked.Token);
        return result.Content is TextContentBlock t ? t.Text : "No text content";
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
    {
        await server.SendNotificationAsync(
            NotificationMethods.CancelledNotification,
            new CancelledNotificationParams
            {
                RequestId = "sampling-request-id",
                Reason    = "Server timeout: sampling took longer than 5 seconds."
            }, ct);

        return "Request timed out. Cancellation sent to client.";
    }
}
```

**Log received cancellations:**
```csharp
builder.Services.AddMcpServer()
    .WithNotificationHandler(
        NotificationMethods.CancelledNotification,
        (notification, ct) =>
        {
            logger.LogInformation(
                "Cancellation received: RequestId={Id} Reason={Reason}",
                notification.Params?.RequestId,
                notification.Params?.Reason);
            // SDK handles the actual cancellation
            return ValueTask.CompletedTask;
        });
```

---

## SDK Quick Reference

### Type Names

| Concept | .NET Type |
|---------|-----------|
| Prompt method decorator | `[McpServerPrompt]` |
| Prompt class decorator | `[McpServerPromptType]` |
| Resource method decorator | `[McpServerResource(UriTemplate = "...")]` |
| Resource class decorator | `[McpServerResourceType]` |
| Tool method decorator | `[McpServerTool]` |
| Tool class decorator | `[McpServerToolType]` |
| Live session object | `McpServer` (injected parameter) |
| Progress | `IProgress<ProgressNotificationValue>` |
| Progress value | `ProgressNotificationValue { Progress, Total, Message }` |
| Sampling request | `CreateMessageRequestParams` |
| Sampling message | `SamplingMessage { Role, Content }` |
| Model preferences | `ModelPreferences { Hints, CostPriority, SpeedPriority, IntelligencePriority }` |
| Elicitation result | `ElicitResult { Action, Content }` |
| Text prompt content | `TextContentBlock { Text }` |
| Embedded resource | `EmbeddedResourceBlock { Resource }` |
| Text resource | `TextResourceContents { Uri, MimeType, Text }` |
| Binary resource | `BlobResourceContents.FromBytes(bytes, uri, mimeType)` |
| Notification methods | `NotificationMethods.*` (constants) |

### Builder Extensions

```csharp
builder.Services.AddMcpServer()
    .WithTools<MyTools>()
    .WithPrompts<MyPrompts>()
    .WithResources<MyResources>()
    .WithCompleteHandler(handler)
    .WithListResourcesHandler(handler)   // with pagination support
    .WithListToolsHandler(handler)       // with pagination support
    .WithListPromptsHandler(handler)     // with pagination support
    .WithSubscribeToResourcesHandler(handler)
    .WithUnsubscribeFromResourcesHandler(handler)
    .WithNotificationHandler(method, handler);
```

---

*Addendum v2 — SDK-verified against https://csharp.sdk.modelcontextprotocol.io*  
*Stack: .NET 10 · C# · ModelContextProtocol SDK · Microsoft.Extensions.AI*
