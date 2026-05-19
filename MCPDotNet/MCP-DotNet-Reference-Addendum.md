# MCP + .NET — Chapters 3 & 4 Supplement
**Client Best Practices · Sampling · Roots · Elicitation**
> Companion to `MCP-DotNet-Reference.md`. Covers topics introduced in Chapters 3–4 of *AI Agents with MCP* — translated to idiomatic .NET 10 / ModelContextProtocol 1.2.

---

## Table of Contents

1. [Pagination](#1-pagination)
2. [Connection Resumption](#2-connection-resumption)
3. [Security — OAuth and Authentication](#3-security--oauth-and-authentication)
4. [Roots](#4-roots)
5. [Sampling](#5-sampling)
6. [Human-in-the-Loop (HITL)](#6-human-in-the-loop-hitl)
7. [Elicitation](#7-elicitation)
8. [Model Agnosticism](#8-model-agnosticism)
9. [Multi-Server Orchestration](#9-multi-server-orchestration)

---

## 1. Pagination

MCP list operations (`tools/list`, `resources/list`, `prompts/list`, `resources/templates/list`) support cursor-based pagination. The server returns a `nextCursor` when more results are available. The client passes that cursor back to get the next page.

**When to use:** When connecting to servers that expose large numbers of tools or resources. Always paginate defensively — you do not know how many items the server will return.

```csharp
// Generic pagination helper — reuse for any list operation
static async Task<List<T>> PaginateAsync<T>(
    Func<string?, Task<(IList<T> Items, string? NextCursor)>> fetchPage)
{
    var all = new List<T>();
    string? cursor = null;

    do
    {
        var (items, next) = await fetchPage(cursor);
        all.AddRange(items);
        cursor = next;
    } while (cursor is not null);

    return all;
}

// Usage
var allTools = await PaginateAsync(async cursor =>
{
    var page = await client.ListToolsAsync(cursor);
    return (page.Tools, page.NextCursor);
});

var allResources = await PaginateAsync(async cursor =>
{
    var page = await client.ListResourcesAsync(cursor);
    return (page.Resources, page.NextCursor);
});
```

**Protocol note:** The cursor is opaque — treat it as a string token, never parse or construct it manually. Its format is server-defined.

---

## 2. Connection Resumption

Streamable HTTP sessions can be resumed after a connection drop without repeating the initialization handshake. The server assigns a `SessionId` at connection time. The client saves it and passes it back to reconnect.

**When to use:** Any production HTTP client where network reliability cannot be guaranteed.

```csharp
// --- First connection ---
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("https://my-mcp-server.example.com/mcp")
});

await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
{
    ClientInfo = new Implementation { Name = "MyHost", Version = "1.0.0" }
});

// Save session state — persist these if you need cross-process resumption
string savedSessionId       = client.SessionId;
ServerCapabilities savedCaps = client.ServerCapabilities;
Implementation savedInfo     = client.ServerInfo;

// --- Later: connection dropped, reconnect ---
var resumeTransport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint       = new Uri("https://my-mcp-server.example.com/mcp"),
    KnownSessionId = savedSessionId   // SDK sends Last-Event-ID automatically
});

await using var resumed = await McpClient.ResumeSessionAsync(
    resumeTransport,
    new ResumeClientSessionOptions
    {
        ServerCapabilities = savedCaps,
        ServerInfo         = savedInfo
    });

// resumed is a fully operational client — no re-initialization needed
```

**What the SDK handles for you:** The Python book shows manually setting the `Last-Event-ID` header. In .NET, `ResumeSessionAsync` + `KnownSessionId` handles this entirely — no manual header management.

**Persistence pattern:** For cross-process resumption (e.g. after app restart), serialize `SessionId` to a file or database. Deserialize `ServerCapabilities` and `ServerInfo` from their JSON representation.

```csharp
// Persist
File.WriteAllText("session.json", JsonSerializer.Serialize(new
{
    SessionId   = client.SessionId,
    Capabilities = client.ServerCapabilities,
    ServerInfo   = client.ServerInfo
}));

// Restore
var saved = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText("session.json"));
```

---

## 3. Security — OAuth and Authentication

### stdio: credentials via environment variables

For stdio servers, credentials are passed through environment variables — never hardcoded.

```csharp
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command   = "dotnet",
    Arguments = ["run", "--project", "MyServer"],
    EnvironmentVariables = new Dictionary<string, string>
    {
        // Pull from host environment — never embed secrets in source
        ["API_KEY"]      = Environment.GetEnvironmentVariable("MY_API_KEY")!,
        ["DB_PASSWORD"]  = Environment.GetEnvironmentVariable("DB_PASSWORD")!
    }
    // Do NOT pass the entire host environment — minimise exposure
});
```

### Streamable HTTP: Bearer token

```csharp
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint          = new Uri("https://my-mcp-server.example.com/mcp"),
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = $"Bearer {await GetAccessTokenAsync()}"
    }
});
```

### Streamable HTTP: OAuth 2.1 with HttpClient

For servers requiring full OAuth 2.1 flows, configure the underlying `HttpClient` with a delegating handler:

```csharp
public class OAuthHandler(TokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}

// Wire it in
var httpClient = new HttpClient(new OAuthHandler(tokenProvider));
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint   = new Uri("https://my-mcp-server.example.com/mcp"),
    HttpClient = httpClient
});
```

The token provider handles refresh transparently — the MCP client never sees expired tokens.

### Validating tool arguments

All arguments produced by an LLM must be treated as untrusted input:

```csharp
[McpServerTool, Description("Reads a file from the project directory")]
public static async Task<string> ReadFile(
    [Description("Relative path within the project")] string relativePath)
{
    // Path traversal prevention — never trust LLM-generated paths
    var safePath = Path.GetFullPath(
        Path.Combine("/safe/base/dir", relativePath));

    if (!safePath.StartsWith("/safe/base/dir"))
        throw new McpException("Path traversal attempt detected");

    return await File.ReadAllTextAsync(safePath);
}
```

---

## 4. Roots

Roots tell a connected MCP server which filesystem locations the client is making available. This is an informational hint — **not a security boundary**. Servers can ignore roots. Access control must be enforced in the tool implementation itself (see §3 path traversal above).

**When to use:** Code assistants, file editors, any server that needs to know where to look for files. Configured by the user, not hardcoded.

```csharp
// Mutable roots list — can change at runtime (e.g. user adds a folder)
var currentRoots = new List<Root>
{
    new Root { Uri = "file:///home/user/myproject", Name = "My Project" }
};

var options = new McpClientOptions
{
    Capabilities = new ClientCapabilities
    {
        Roots = new RootsCapability
        {
            // Called by the server when it wants the current list
            RootsHandler = (request, ct) =>
            {
                // Validate every URI — prevent path traversal in roots themselves
                foreach (var root in currentRoots)
                {
                    if (!root.Uri.StartsWith("file://"))
                        throw new InvalidOperationException($"Invalid root URI: {root.Uri}");
                }

                return ValueTask.FromResult(new ListRootsResult { Roots = currentRoots });
            },

            // true = client will send notifications/roots/list_changed when list changes
            ListChanged = true
        }
    }
};

await using var client = await McpClient.CreateAsync(transport, options);

// When the user adds a root at runtime:
currentRoots.Add(new Root { Uri = "file:///home/user/otherproject", Name = "Other Project" });

// Notify the server the list has changed
await client.SendNotificationAsync(NotificationMethods.RootsListChangedNotification);
```

### Security checklist (from MCP spec)

| Requirement | Level | Implementation |
|---|---|---|
| Only expose roots with appropriate permissions | MUST | Filter `currentRoots` before returning |
| Validate root URIs to prevent path traversal | MUST | Check `file://` scheme + canonical path |
| Implement access controls | MUST | Enforce in tool handlers, not just roots |
| Monitor root accessibility | MUST | Verify paths exist before adding to list |
| Get user consent before providing roots | SHOULD | Prompt user in host UI |
| Provide UI for managing roots | SHOULD | Add/remove UI in host application |

**Key design point:** The `RootsHandler` is called by the server on demand. The host application owns the `currentRoots` list and is responsible for populating it from user configuration.

---

## 5. Sampling

Sampling allows an MCP **server** to request an LLM completion from the **client** during a tool call. The data flow is:

```
Host (has LLM access)
  └─ Client ←── sampling request ── Server (inside a tool)
       └─ calls LLM
       └─ returns result ──────────→ Server continues tool execution
```

**Why it exists:** Servers are LLM-agnostic and do not have their own LLM access. Sampling lets them leverage the host's LLM without needing API keys.

**Why it is sensitive:** You are giving a third-party server the ability to consume your LLM quota. Always implement HITL (see §6).

```csharp
// Dedicated LLM client for sampling — MUST NOT have UseFunctionInvocation
// (would cause infinite loop: tool → sampling → tool → ...)
IChatClient samplingLlm = new OllamaChatClient("llama3.2", new Uri("http://localhost:11434"));

var options = new McpClientOptions
{
    Capabilities = new ClientCapabilities
    {
        Sampling = new SamplingCapability
        {
            SamplingHandler = async (request, ct) =>
            {
                // Translate MCP sampling request → Microsoft.Extensions.AI messages
                var messages = request.Messages
                    .Select(m => new ChatMessage(
                        m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                        m.Content.Text ?? string.Empty))
                    .ToList();

                var response = await samplingLlm.CompleteAsync(messages, cancellationToken: ct);

                return new CreateMessageResult
                {
                    Role    = "assistant",
                    Content = new SamplingContent { Text = response.Message.Text ?? string.Empty },
                    Model   = request.ModelPreferences?.Hints?.FirstOrDefault()?.Name ?? "default"
                };
            }
        }
    }
};
```

### Model preference hints

The server can express model preferences. The client should respect them but is not required to:

```csharp
SamplingHandler = async (request, ct) =>
{
    // Server may suggest a model — use it if you support it, fall back otherwise
    var preferredModel = request.ModelPreferences?.Hints?
        .FirstOrDefault()?.Name ?? "llama3.2";

    var llm = GetLlmForModel(preferredModel) ?? defaultLlm;
    // ...
}
```

### Rate limiting

Protect against runaway server-initiated calls:

```csharp
private readonly SemaphoreSlim _samplingThrottle = new(maxConcurrent: 3);
private int _samplingCallsThisMinute = 0;

SamplingHandler = async (request, ct) =>
{
    if (Interlocked.Increment(ref _samplingCallsThisMinute) > 10)
        throw new McpException("Sampling rate limit exceeded");

    await _samplingThrottle.WaitAsync(ct);
    try { /* call LLM */ }
    finally { _samplingThrottle.Release(); }
}
```

---

## 6. Human-in-the-Loop (HITL)

Both Anthropic and the book strongly recommend HITL before allowing a server to trigger sampling or before returning sampling results. The protocol defines these as **SHOULDs**:

- Get user approval before sending messages to the LLM
- Get user approval before returning LLM responses to the server
- Respect model preference hints
- Implement rate limiting

```csharp
SamplingHandler = async (request, ct) =>
{
    // Show the outgoing request to the user
    var preview = request.Messages.Last().Content.Text ?? "(no text)";
    Console.WriteLine($"\n[SAMPLING REQUEST from server]");
    Console.WriteLine($"Message: {preview}");
    Console.Write("Approve LLM call? (y/n): ");

    if (Console.ReadLine()?.Trim().ToLower() != "y")
        throw new McpException("User rejected sampling request");

    // Call LLM
    var response = await samplingLlm.CompleteAsync(/* ... */, ct);

    // Show the result before returning it to the server
    Console.WriteLine($"\n[SAMPLING RESPONSE]");
    Console.WriteLine($"Result: {response.Message.Text}");
    Console.Write("Return this to the server? (y/n): ");

    if (Console.ReadLine()?.Trim().ToLower() != "y")
        throw new McpException("User rejected sampling response");

    return new CreateMessageResult { /* ... */ };
}
```

**In production:** Replace `Console.ReadLine()` with whatever your host UI provides — a web dialog, a Blazor component, a desktop notification. The pattern is the same; only the I/O mechanism changes.

---

## 7. Elicitation

Elicitation is the inverse direction from sampling: the **server** asks the **client** for structured user input mid-tool-execution, without ending the tool call.

```
Tool is running on server
  └─ Server → elicitation request → Client
                                      └─ shows UI to user
                                      └─ user fills form
       Server continues ← result ────┘
```

**When to use (server-side):** When a tool needs a decision or credential it cannot determine from context — e.g. "which environment?" or "enter your database password".

### Server side — requesting elicitation

```csharp
[McpServerTool, Description("Deploys the application to the selected environment")]
public static async Task<string> Deploy(IMcpServer server, CancellationToken ct)
{
    var response = await server.ElicitAsync(
        new ElicitRequestParams
        {
            Message = "Select deployment target",
            RequestedSchema = new JsonSchemaObject
            {
                Properties = new Dictionary<string, JsonSchemaObject>
                {
                    ["environment"] = new JsonSchemaObject
                    {
                        Type  = "string",
                        Title = "Environment",
                        Enum  = ["staging", "production"]
                    }
                },
                Required = ["environment"]
            }
        }, ct);

    if (response.Action != ElicitationAction.Accept)
        return "Deployment cancelled by user.";

    var env = response.Content?["environment"]?.GetValue<string>();
    return $"Deployed to {env} successfully.";
}
```

### Client side — handling elicitation

If you do not register a handler, the SDK returns a default rejection. Register a handler to present the request to the user:

```csharp
var options = new McpClientOptions
{
    Capabilities = new ClientCapabilities
    {
        Elicitation = new ElicitationCapability
        {
            ElicitationHandler = async (request, ct) =>
            {
                Console.WriteLine($"\n[SERVER INPUT REQUEST]");
                Console.WriteLine($"{request.Message}");

                // Read schema fields and prompt for each
                var result = new Dictionary<string, object?>();
                foreach (var (key, schema) in request.RequestedSchema.Properties ?? [])
                {
                    Console.Write($"{schema.Title ?? key}: ");
                    result[key] = Console.ReadLine();
                }

                Console.Write("Submit? (y/n): ");
                var action = Console.ReadLine()?.Trim().ToLower() == "y"
                    ? ElicitationAction.Accept
                    : ElicitationAction.Cancel;

                return new ElicitResult { Action = action, Content = result };
            }
        }
    }
};
```

---

## 8. Model Agnosticism

The book's chapter 3 ends with a discussion of building model-agnostic clients. In .NET this is solved structurally by `Microsoft.Extensions.AI` — `IChatClient` is the abstraction.

```csharp
// Swap the provider without changing any agent logic
IChatClient GetLlm(string provider) => provider switch
{
    "ollama"    => new OllamaChatClient("llama3.2", new Uri("http://localhost:11434")),
    "openai"    => new OpenAIChatClient(new OpenAIClient(apiKey), "gpt-4o"),
    "anthropic" => new AnthropicChatClient(apiKey, "claude-sonnet-4-5"),
    _           => throw new ArgumentException($"Unknown provider: {provider}")
};

// All agent code works against IChatClient — provider is configuration, not code
IChatClient llm = new ChatClientBuilder(GetLlm(config["Provider"]))
    .UseLogging(loggerFactory)
    .UseFunctionInvocation()
    .Build();
```

**Practical note:** The sampling client must be a different `IChatClient` instance from the agent's main client — and must not have `UseFunctionInvocation()` in its pipeline.

```csharp
// Main agent client — has function invocation
IChatClient agentLlm = new ChatClientBuilder(GetLlm("ollama"))
    .UseFunctionInvocation()
    .Build();

// Sampling client — raw, no function invocation
IChatClient samplingLlm = GetLlm("ollama");
```

---

## 9. Multi-Server Orchestration

The book's chapter 3 ends with multi-server patterns. Connecting to multiple servers and merging their tools:

```csharp
// Connect to multiple servers
await using var filesystemClient = await McpClient.CreateAsync(
    new StdioClientTransport(new StdioClientTransportOptions
    {
        Command   = "dotnet",
        Arguments = ["run", "--project", "FilesystemServer"]
    }));

await using var databaseClient = await McpClient.CreateAsync(
    new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri("https://db-server.example.com/mcp")
    }));

// Collect tools from all servers
var allTools = new List<AITool>();
allTools.AddRange(await filesystemClient.GetAIFunctionsAsync());
allTools.AddRange(await databaseClient.GetAIFunctionsAsync());

// Single LLM client with all tools
IChatClient llm = new ChatClientBuilder(innerProvider)
    .UseFunctionInvocation()
    .Build();

var options = new ChatOptions { Tools = allTools };
var response = await llm.CompleteAsync(messages, options);
```

### Tool disambiguation

When multiple servers expose tools with the same name, prefix them:

```csharp
// Namespace tools by server name to avoid collisions
static IEnumerable<AITool> PrefixTools(
    IEnumerable<AITool> tools, string serverName)
    => tools.Select(t => new PrefixedTool(t, serverName));

allTools.AddRange(PrefixTools(await filesystemClient.GetAIFunctionsAsync(), "fs"));
allTools.AddRange(PrefixTools(await databaseClient.GetAIFunctionsAsync(), "db"));
// LLM sees: fs_read_file, db_query_table — no ambiguity
```

### Failure isolation

One server failing should not kill the entire agent:

```csharp
static async Task<IList<AITool>> TryGetToolsAsync(McpClient client, string serverName)
{
    try
    {
        return await client.GetAIFunctionsAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{serverName}] tool discovery failed: {ex.Message}");
        return [];  // continue without this server's tools
    }
}
```

---

## Key Differences: Python Book vs .NET SDK

| Python book | .NET equivalent | Note |
|---|---|---|
| `MCPClient` class with `connect()` | `McpClient.CreateAsync(transport)` | SDK provides the class |
| `AsyncExitStack` | `await using` | Compiler-managed |
| `Last-Event-ID` header manual | `ResumeSessionAsync` + `KnownSessionId` | SDK handles header |
| `_get_session_id` callback | `client.SessionId` property | Direct access |
| `list_roots_callback` in session | `RootsCapability.RootsHandler` in `McpClientOptions` | Same pattern |
| `SamplingFnT` callback | `SamplingCapability.SamplingHandler` | Same pattern |
| Manual tool loop | `UseFunctionInvocation()` middleware | **Preferred in .NET** |
| Separate client class per transport | Same `McpClient`, different transport object | Transport is a parameter |

---

## References

- **MCP Specification — Roots**: https://modelcontextprotocol.io/specification/2025-11-25/client/roots
- **MCP Specification — Sampling**: https://modelcontextprotocol.io/specification/2025-11-25/client/sampling
- **MCP Specification — Elicitation**: https://modelcontextprotocol.io/specification/2025-11-25/server/utilities/elicitation
- **C# SDK — Elicitation concepts**: https://csharp.sdk.modelcontextprotocol.io/concepts/elicitation/elicitation.html
- **Microsoft.Extensions.AI — IChatClient**: https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai
