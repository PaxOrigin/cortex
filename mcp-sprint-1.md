# MCP Sprint 1 — Architecture, Protocol & SDK Deep Dive
### Microsoft ModelContextProtocol 1.2 · .NET · Didactic Reference

---

## Table of Contents

1. [Sprint 1 Recap — What We Built](#sprint-1-recap)
2. [What Is MCP — The Mental Model](#what-is-mcp)
3. [The Four Components in Depth](#the-four-components)
4. [The Protocol Itself — How Messages Flow](#the-protocol)
5. [Transports — How Bytes Travel](#transports)
6. [ModelContextProtocol 1.2 SDK — Full Tutorial](#sdk-tutorial)
   - [Package Architecture](#package-architecture)
   - [The Client API](#the-client-api)
   - [Tools — Calling Server Functions](#tools)
   - [Resources — Reading Server Data](#resources)
   - [Prompts — Reusable Message Templates](#prompts)
   - [The Server API](#the-server-api)
   - [Error Handling](#error-handling)
   - [Notifications — Live Updates](#notifications)
7. [What Comes Next](#whats-next)

---

## 1. Sprint 1 Recap — What We Built {#sprint-1-recap}

### The milestone

We built a working MCP host application in .NET that:

- Starts as a console process (the **host**)
- Creates an **MCP client** using `McpClient.CreateAsync`
- Establishes a **stdio transport** to a child server process
- Completes the **MCP protocol handshake** automatically
- Issues a `tools/list` request and receives 14 tools from the filesystem server

The output that confirmed success:

```
Starting up Host...
Connected to server!
Tool: read_file
Tool: read_text_file
...
Tool: list_allowed_directories
```

### The working code (corrected from v1.0 to v1.2 API)

```csharp
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name    = "filesystem-server",
    Command = "npx",
    Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]
});

var client = await McpClient.CreateAsync(transport);

foreach (var tool in await client.ListToolsAsync())
    Console.WriteLine($"Tool: {tool.Name}");
```

### Key corrections discovered

| What I wrote first | What v1.2 actually uses |
|---|---|
| `using ModelContextProtocol.Protocol.Transport` | `using ModelContextProtocol.Protocol` |
| `McpClientFactory.CreateAsync(...)` | `McpClient.CreateAsync(...)` |
| `tools.Tools.Count` + wrapper object | `ListToolsAsync()` returns `IList<McpClientTool>` directly |
| Separate transport package needed | Everything is in `ModelContextProtocol` |

---

## 2. What Is MCP — The Mental Model {#what-is-mcp}

MCP stands for **Model Context Protocol**. It is an open standard that defines how AI applications (hosts) give language models access to external tools and data through a clean, consistent interface.

### The problem MCP solves

Before MCP, every AI application that wanted to connect to external systems had to invent its own integration layer. Want your LLM to read files? Roll your own. Want it to call a database? Roll your own again. These integrations were bespoke, brittle, and not reusable across different AI systems.

MCP solves this with a **universal protocol**. A server that exposes tools via MCP can be used by any MCP-compatible host — Claude Desktop, VS Code Copilot, your custom .NET app, anything.

### The analogy

Think of MCP like **USB**. USB defines a standard physical and digital interface. A USB keyboard works with any computer that has a USB port, regardless of manufacturer or operating system. Similarly, an MCP server works with any host that speaks MCP, regardless of what LLM or application framework is behind it.

The MCP server is the **peripheral**. The host is the **computer**. The protocol is the **USB specification**.

### What "context" means here

An LLM on its own knows only what it was trained on. MCP gives it **live context** by letting it:

- **Call tools** — execute functions (read a file, query a database, call an API)
- **Read resources** — access structured data by URI (a config file, a database record)
- **Use prompts** — retrieve reusable message templates defined by the server

All three mechanisms exist to inject real, up-to-date, specific information into an LLM conversation that the model could not have otherwise.

---

## 3. The Four Components in Depth {#the-four-components}

```
┌─────────────────────────────────┐           ┌──────────────────────┐
│         Host Application        │           │      MCP Server      │
│                                 │           │                      │
│  ┌──────────────────────────┐   │ Transport │  ┌────────────────┐  │
│  │       MCP Client         │◄──┼──────────►│  │ Tools          │  │
│  │  Manages the connection  │   │           │  │ Resources      │  │
│  │  Sends/receives messages │   │           │  │ Prompts        │  │
│  └──────────────────────────┘   │           │  └────────────────┘  │
│                                 │           │                      │
└─────────────────────────────────┘           └──────────────────────┘
```

### The Host Application

The host is **your application** — the process that the user interacts with. In our case it is a console app. In a real product it might be an IDE extension, a chat UI, a backend service, or an agent orchestrator.

The host is responsible for:
- Owning the lifecycle of one or more MCP clients
- Deciding which servers to connect to
- Passing tool results to an LLM and feeding LLM decisions back to the client
- Managing user sessions and application logic

The host never speaks the MCP protocol directly. It delegates that entirely to the client.

### The MCP Client

The client is the **protocol implementation** that lives inside your host. One client = one connection to one server. If you want to connect to three servers, you create three clients.

The client is responsible for:
- Opening and maintaining the transport connection
- Performing the initialization handshake
- Serializing outgoing requests to JSON-RPC 2.0
- Deserializing incoming responses and notifications
- Exposing typed .NET APIs (like `ListToolsAsync()`) so your host code never has to think about wire format

In the SDK, the client is represented by `IMcpClient`, created via `McpClient.CreateAsync`.

### The Transport

The transport is the **communication channel** between client and server. It is a pure plumbing concern — the protocol runs on top of it, unchanged regardless of which transport you pick.

Three transports exist in v1.2:

| Transport | How it works | Best for |
|---|---|---|
| **stdio** | Client spawns server as a child process, communicates over stdin/stdout | Local servers, development |
| **Streamable HTTP** | HTTP with bidirectional streaming; server runs independently | Remote servers, production |
| **SSE** (legacy) | Server-Sent Events + separate POST endpoint | Legacy compatibility only |

> **Rule of thumb:** use stdio while developing (simple, no networking), use Streamable HTTP when you deploy or need a server that multiple clients can reach.

### The MCP Server

The server is the **capability provider**. It exposes three categories of things:

- **Tools** — callable functions with typed parameters. `read_file`, `search_web`, `query_database`.
- **Resources** — addressable data identified by URI. `file:///readme.md`, `db://customers/42`.
- **Prompts** — reusable message templates that clients can retrieve and inject into LLM conversations.

The server is a completely separate process (or remote service). It has no knowledge of your host application, your LLM, or your users. It just responds to MCP requests.

---

## 4. The Protocol Itself — How Messages Flow {#the-protocol}

MCP is built on top of **JSON-RPC 2.0**. Every message is a JSON object sent over whatever transport you chose.

### Message types

**Request** — expects a response:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/list",
  "params": {}
}
```

**Response** — answers a request:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "tools": [
      { "name": "read_file", "description": "Read a file", "inputSchema": { ... } }
    ]
  }
}
```

**Notification** — one-way, no response expected:
```json
{
  "jsonrpc": "2.0",
  "method": "notifications/tools/list_changed",
  "params": {}
}
```

### The initialization handshake

This happens automatically when you call `McpClient.CreateAsync`. You never write this code — but understanding it matters.

```
Client                              Server
  │                                   │
  │── initialize ──────────────────►  │   Client announces its name, version, capabilities
  │                                   │
  │  ◄─────────────────── initialize ─│   Server responds with its name, version, capabilities
  │                                   │
  │── notifications/initialized ────► │   Client confirms handshake complete
  │                                   │
  │            READY                  │
```

The `initialize` request carries a `ClientInfo` block (name + version of your host app) and a `capabilities` block listing what the client supports. The server responds with its own info and capabilities.

This is why `McpClientOptions.ClientInfo` matters — it is your application's identity in the MCP ecosystem.

### The core request methods

| Method | Direction | What it does |
|---|---|---|
| `tools/list` | client → server | Get all available tools |
| `tools/call` | client → server | Execute a tool |
| `resources/list` | client → server | Get all available resources |
| `resources/read` | client → server | Read a resource by URI |
| `resources/templates/list` | client → server | Get URI templates |
| `prompts/list` | client → server | Get all available prompts |
| `prompts/get` | client → server | Get a prompt with arguments |
| `notifications/tools/list_changed` | server → client | Tool list has changed |
| `notifications/resources/list_changed` | server → client | Resource list has changed |
| `notifications/resources/updated` | server → client | A subscribed resource changed |
| `notifications/prompts/list_changed` | server → client | Prompt list has changed |

Every one of these is what the .NET SDK wraps into typed methods like `ListToolsAsync()` and `CallToolAsync()`.

---

## 5. Transports — How Bytes Travel {#transports}

### stdio — child process model

```
Host process
├── McpClient
│   └── StdioClientTransport
│       ├── stdin  ──────────────────► Server process (npx, dotnet, python...)
│       └── stdout ◄──────────────────
```

The client **spawns the server** as a child process. All communication is through stdin/stdout. The server's stderr is captured separately (useful for server-side logs).

```csharp
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command          = "npx",
    Arguments        = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
    Name             = "filesystem",           // for logging
    WorkingDirectory = "/my/working/dir",       // optional
    ShutdownTimeout  = TimeSpan.FromSeconds(10), // graceful shutdown wait
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["MY_API_KEY"] = "secret",             // inject env vars
        ["UNWANTED_VAR"] = null                // null = remove a variable
    },
    StandardErrorLines = line =>               // capture server stderr
        Console.Error.WriteLine($"[server] {line}")
});
```

### Streamable HTTP — remote server model

The client connects to an already-running HTTP server. This is for production scenarios where the server is a separate service.

```csharp
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint          = new Uri("https://my-mcp-server.example.com/mcp"),
    TransportMode     = HttpTransportMode.StreamableHttp,  // or AutoDetect
    ConnectionTimeout = TimeSpan.FromSeconds(30),
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = "Bearer my-token"
    }
});

await using var client = await McpClient.CreateAsync(transport);
```

`HttpTransportMode.AutoDetect` (the default) tries Streamable HTTP first and falls back to SSE automatically — useful when you don't know what the remote server supports.

### Session resumption (Streamable HTTP only)

If the connection drops, you can reconnect without losing the session:

```csharp
// Save these from the original session
string savedSessionId           = client.SessionId;
ServerCapabilities savedCaps    = client.ServerCapabilities;
Implementation savedServerInfo  = client.ServerInfo;

// Later: resume
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint        = new Uri("https://my-mcp-server.example.com/mcp"),
    KnownSessionId  = savedSessionId
});

await using var resumed = await McpClient.ResumeSessionAsync(transport, new ResumeClientSessionOptions
{
    ServerCapabilities = savedCaps,
    ServerInfo         = savedServerInfo
});
```

---

## 6. ModelContextProtocol 1.2 SDK — Full Tutorial {#sdk-tutorial}

### Package Architecture {#package-architecture}

The SDK ships as three NuGet packages. Choose based on what you are building:

| Package | Use when |
|---|---|
| `ModelContextProtocol.Core` | You only need the client or raw server APIs; minimum dependencies |
| `ModelContextProtocol` | You need DI, hosting, attribute-based discovery — **right choice for most projects** |
| `ModelContextProtocol.AspNetCore` | You're building an HTTP server with ASP.NET Core |

For our learning project: `ModelContextProtocol` is the right package. It references `Core` automatically.

```bash
dotnet add package ModelContextProtocol
dotnet add package Microsoft.Extensions.Hosting   # if you use IHost
```

---

### The Client API {#the-client-api}

Everything client-side lives in two namespaces:

```csharp
using ModelContextProtocol.Client;    // McpClient, transports, options
using ModelContextProtocol.Protocol;  // message types, content blocks
```

#### Creating a client

```csharp
// Minimal — just a transport
var client = await McpClient.CreateAsync(transport);

// With identity and options
var client = await McpClient.CreateAsync(transport, new McpClientOptions
{
    ClientInfo = new Implementation
    {
        Name    = "MyHostApp",
        Version = "1.0.0"
    }
});
```

`McpClient.CreateAsync` performs the handshake synchronously before returning. When it returns, the connection is live and the client is ready to use.

#### Disposing correctly

`IMcpClient` implements `IAsyncDisposable`. Always dispose it:

```csharp
await using var client = await McpClient.CreateAsync(transport);
// client is disposed automatically at end of scope
```

Or manually:

```csharp
var client = await McpClient.CreateAsync(transport);
// ... use client ...
await client.DisposeAsync();
```

#### What the client exposes after connection

```csharp
client.ServerInfo         // Implementation: { Name, Version } from the server
client.ServerCapabilities // what features the server declared it supports
client.SessionId          // session identifier (HTTP transport only)
```

---

### Tools — Calling Server Functions {#tools}

Tools are the primary mechanism for an LLM to take action. From the client's perspective:

#### Discovering tools

```csharp
IList<McpClientTool> tools = await client.ListToolsAsync();

foreach (var tool in tools)
{
    Console.WriteLine($"{tool.Name}: {tool.Description}");
}
```

`McpClientTool` also exposes the JSON Schema for its parameters via `tool.JsonSchema` — this is what you hand to an LLM so it knows how to call the tool.

#### Calling a tool by name (direct approach)

```csharp
CallToolResult result = await client.CallToolAsync(
    "read_file",
    new Dictionary<string, object?> { ["path"] = "/tmp/hello.txt" },
    cancellationToken: CancellationToken.None
);
```

#### Calling a tool from the discovered list (cleaner approach)

```csharp
var tools = await client.ListToolsAsync();
var readFile = tools.First(t => t.Name == "read_file");

CallToolResult result = await readFile.CallAsync(
    new Dictionary<string, object?> { ["path"] = "/tmp/hello.txt" }
);
```

#### Processing the result

Tool results contain a list of content blocks. Always switch on type:

```csharp
foreach (var content in result.Content)
{
    switch (content)
    {
        case TextContentBlock text:
            Console.WriteLine(text.Text);
            break;

        case ImageContentBlock image:
            // image.DecodedData is the raw bytes
            File.WriteAllBytes("output.png", image.DecodedData.ToArray());
            break;

        case AudioContentBlock audio:
            File.WriteAllBytes("output.wav", audio.DecodedData.ToArray());
            break;

        case EmbeddedResourceBlock resource:
            if (resource.Resource is TextResourceContents textRes)
                Console.WriteLine(textRes.Text);
            else if (resource.Resource is BlobResourceContents blobRes)
                Console.WriteLine($"Binary: {blobRes.Blob.Length} bytes");
            break;
    }
}
```

#### Checking for tool errors

Tool errors are **not** exceptions. They are returned inside the result with `IsError = true`. This is intentional — it lets the LLM see the error and potentially recover.

```csharp
if (result.IsError is true)
{
    var errorText = result.Content
        .OfType<TextContentBlock>()
        .FirstOrDefault()?.Text;

    Console.WriteLine($"Tool error: {errorText}");
}
```

#### Handing tools to an LLM (Microsoft.Extensions.AI integration)

`McpClientTool` inherits from `AIFunction`, so the tool list can be handed directly to any `IChatClient`:

```csharp
IList<McpClientTool> tools = await client.ListToolsAsync();

IChatClient chatClient = /* your AI client (Ollama, Azure OpenAI, etc.) */;
var response = await chatClient.GetResponseAsync(
    "List all files in /tmp and tell me what you find",
    new ChatOptions { Tools = [.. tools] }
);
```

The LLM will automatically know about all available tools and can invoke them during the conversation.

---

### Resources — Reading Server Data {#resources}

Resources are addressable data identified by URI. Think of them as read-only files or database rows that the server makes available.

#### Listing direct resources

```csharp
IList<McpClientResource> resources = await client.ListResourcesAsync();

foreach (var resource in resources)
{
    Console.WriteLine($"{resource.Name}");
    Console.WriteLine($"  URI:  {resource.Uri}");
    Console.WriteLine($"  MIME: {resource.MimeType}");
    Console.WriteLine($"  Desc: {resource.Description}");
}
```

#### Listing URI templates (parameterized resources)

Some servers expose templates — URIs with placeholders like `docs://articles/{id}`:

```csharp
IList<McpClientResourceTemplate> templates = await client.ListResourceTemplatesAsync();

foreach (var template in templates)
    Console.WriteLine($"{template.Name}: {template.UriTemplate}");
```

#### Reading a resource by URI

```csharp
ReadResourceResult result = await client.ReadResourceAsync("config://app/settings");

foreach (var content in result.Contents)
{
    if (content is TextResourceContents text)
        Console.WriteLine($"[{text.MimeType}] {text.Text}");
    else if (content is BlobResourceContents blob)
        Console.WriteLine($"[{blob.MimeType}] {blob.Blob.Length} bytes");
}
```

#### Reading a template resource

Pass the URI template and a dictionary of parameter values:

```csharp
ReadResourceResult result = await client.ReadResourceAsync(
    "docs://articles/{id}",
    new Dictionary<string, object?> { ["id"] = "getting-started" }
);
```

#### Subscribing to resource updates

When a resource can change over time, you can subscribe to receive notifications when it does:

```csharp
IAsyncDisposable subscription = await client.SubscribeToResourceAsync(
    "config://app/settings",
    async (notification, ct) =>
    {
        Console.WriteLine($"Resource changed: {notification.Uri}");

        // Re-read to get fresh content
        var updated = await client.ReadResourceAsync(notification.Uri, cancellationToken: ct);
        // process updated content...
    }
);

// When done, unsubscribe
await subscription.DisposeAsync();
```

---

### Prompts — Reusable Message Templates {#prompts}

Prompts are server-defined message templates. Rather than hardcoding "how to ask an LLM to review code", you can retrieve a well-crafted, parameterized prompt from the server.

#### Listing available prompts

```csharp
IList<McpClientPrompt> prompts = await client.ListPromptsAsync();

foreach (var prompt in prompts)
{
    Console.WriteLine($"{prompt.Name}: {prompt.Description}");

    // Show what arguments it accepts
    if (prompt.ProtocolPrompt.Arguments is { Count: > 0 })
    {
        foreach (var arg in prompt.ProtocolPrompt.Arguments)
        {
            var required = arg.Required == true ? " (required)" : "";
            Console.WriteLine($"  arg: {arg.Name}{required} — {arg.Description}");
        }
    }
}
```

#### Getting a prompt with arguments

```csharp
GetPromptResult result = await client.GetPromptAsync(
    "code_review",
    new Dictionary<string, object?>
    {
        ["language"] = "csharp",
        ["code"]     = "public static int Add(int a, int b) => a + b;"
    }
);

// result.Messages is a list of PromptMessage — each has Role and Content
foreach (var message in result.Messages)
{
    Console.WriteLine($"[{message.Role}]:");

    switch (message.Content)
    {
        case TextContentBlock text:
            Console.WriteLine($"  {text.Text}");
            break;
        case ImageContentBlock image:
            Console.WriteLine($"  [image/{image.MimeType}]");
            break;
        case EmbeddedResourceBlock resource:
            Console.WriteLine($"  Resource: {resource.Resource.Uri}");
            break;
    }
}
```

The returned messages are typically injected into your LLM conversation as the opening context.

---

### The Server API {#the-server-api}

We have not built a server yet, but understanding the server side helps you reason about what the client is talking to. Here is the complete picture.

#### Minimal stdio server

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);

// Send all logs to stderr — stdout is reserved for MCP messages
builder.Logging.AddConsole(opts =>
    opts.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();   // discovers [McpServerToolType] classes

await builder.Build().RunAsync();

[McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echoes the message back to the client")]
    public static string Echo(
        [Description("The message to echo")] string message)
        => $"Echo: {message}";
}
```

#### Defining tools on a server

Attribute-based discovery is the standard approach:

```csharp
[McpServerToolType]
public static class MyTools
{
    // Returns a plain string → automatically wrapped in TextContentBlock
    [McpServerTool, Description("Greets the user by name")]
    public static string Greet(
        [Description("The person's name")] string name)
        => $"Hello, {name}!";

    // Returns an image
    [McpServerTool, Description("Returns a chart as a PNG")]
    public static ImageContentBlock GenerateChart(string data)
    {
        byte[] pngBytes = RenderChart(data);
        return ImageContentBlock.FromBytes(pngBytes, "image/png");
    }

    // Returns mixed content
    [McpServerTool, Description("Returns text and an image together")]
    public static IEnumerable<ContentBlock> Describe()
    {
        return
        [
            new TextContentBlock { Text = "Here is the diagram:" },
            ImageContentBlock.FromBytes(GetDiagram(), "image/png"),
            new TextContentBlock { Text = "The diagram shows the system layout." }
        ];
    }

    // With DI — services are injected automatically
    [McpServerTool, Description("Queries the database")]
    public static async Task<string> QueryDb(
        MyDbContext db,    // injected from DI
        [Description("The SQL query")] string sql)
    {
        var result = await db.RunQueryAsync(sql);
        return JsonSerializer.Serialize(result);
    }
}
```

#### Defining resources on a server

```csharp
[McpServerResourceType]
public class MyResources
{
    // Direct resource — fixed URI, appears in resources/list
    [McpServerResource(UriTemplate = "config://app/settings", Name = "App Settings", MimeType = "application/json")]
    [Description("Returns current application configuration")]
    public static string GetSettings()
        => JsonSerializer.Serialize(new { theme = "dark", language = "en" });

    // Template resource — parameterized URI, appears in resources/templates/list
    [McpServerResource(UriTemplate = "docs://articles/{id}", Name = "Article")]
    [Description("Returns an article by its ID")]
    public static TextResourceContents GetArticle(string id)
    {
        string content = File.ReadAllText($"/articles/{id}.md");
        return new TextResourceContents
        {
            Uri      = $"docs://articles/{id}",
            MimeType = "text/markdown",
            Text     = content
        };
    }
}
```

#### Defining prompts on a server

```csharp
[McpServerPromptType]
public class MyPrompts
{
    // Simple prompt — no arguments
    [McpServerPrompt, Description("A standard greeting opener")]
    public static ChatMessage Greeting()
        => new(ChatRole.User, "Hello! What would you like help with today?");

    // Multi-message prompt with arguments
    [McpServerPrompt, Description("Generates a code review conversation")]
    public static IEnumerable<ChatMessage> CodeReview(
        [Description("The programming language")] string language,
        [Description("The code to review")]        string code)
        =>
        [
            new(ChatRole.User,
                $"Please review this {language} code:\n\n```{language}\n{code}\n```"),
            new(ChatRole.Assistant,
                "I'll review for correctness, style, and potential improvements.")
        ];
}
```

#### Registering everything with the server builder

```csharp
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()      // or .WithHttpTransport()
    .WithToolsFromAssembly()         // discovers all [McpServerToolType]
    .WithTools<MyExplicitTools>()    // or register specific types
    .WithResources<MyResources>()
    .WithPrompts<MyPrompts>();
```

#### HTTP server (ASP.NET Core)

```csharp
// dotnet add package ModelContextProtocol.AspNetCore

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(opts => opts.Stateless = true)  // stateless = simpler, recommended
    .WithToolsFromAssembly();

var app = builder.Build();
app.MapMcp("/mcp");   // all MCP endpoints mounted at /mcp
app.Run("http://localhost:3001");
```

---

### Error Handling {#error-handling}

MCP distinguishes between two categories of error.

#### Tool errors (non-fatal — LLM can recover)

When a tool method throws a normal exception, the SDK catches it and returns `IsError = true` inside the `CallToolResult`. The LLM sees the error message and can choose to retry or try something else.

```csharp
// Server side
[McpServerTool, Description("Divides two numbers")]
public static double Divide(double a, double b)
{
    if (b == 0)
        throw new ArgumentException("Cannot divide by zero");
        // Client receives IsError=true, generic message: "An error occurred invoking 'Divide'."

    return a / b;
}
```

Use `McpException` when you want the error message to reach the client:

```csharp
if (b == 0)
    throw new McpException("Division by zero is not allowed");
    // Client receives IsError=true, message: "Division by zero is not allowed"
```

```csharp
// Client side
var result = await client.CallToolAsync("divide", new Dictionary<string, object?>
{
    ["a"] = 10.0,
    ["b"] = 0.0
});

if (result.IsError is true)
{
    var errorText = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
    Console.WriteLine($"Tool error: {errorText}");
}
```

#### Protocol errors (fatal — propagate as exceptions)

`McpProtocolException` signals a protocol-level violation. It propagates as a JSON-RPC error response, not a tool result. Use it for invalid inputs or unknown operations.

```csharp
[McpServerTool, Description("Processes input")]
public static string Process(string input)
{
    if (string.IsNullOrEmpty(input))
        throw new McpProtocolException("Missing required input", McpErrorCode.InvalidParams);
        // Propagates as JSON-RPC error -32602

    return $"Processed: {input}";
}
```

On the client, this surfaces as an `McpProtocolException` being thrown from `CallToolAsync`.

---

### Notifications — Live Updates {#notifications}

Servers can push unsolicited notifications to clients. This requires stdio or stateful HTTP transport (stateless HTTP cannot push).

#### Registering a notification handler on the client

```csharp
// Tool list changed
client.RegisterNotificationHandler(
    NotificationMethods.ToolListChangedNotification,
    async (notification, ct) =>
    {
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        Console.WriteLine($"Tool list refreshed: {tools.Count} tools");
    });

// Resource list changed
client.RegisterNotificationHandler(
    NotificationMethods.ResourceListChangedNotification,
    async (notification, ct) =>
    {
        var resources = await client.ListResourcesAsync(cancellationToken: ct);
        Console.WriteLine($"Resource list refreshed: {resources.Count} resources");
    });

// Prompt list changed
client.RegisterNotificationHandler(
    NotificationMethods.PromptListChangedNotification,
    async (notification, ct) =>
    {
        var prompts = await client.ListPromptsAsync(cancellationToken: ct);
        Console.WriteLine($"Prompt list refreshed: {prompts.Count} prompts");
    });
```

#### Sending notifications from the server

```csharp
// After dynamically adding or removing a tool
await server.SendNotificationAsync(
    NotificationMethods.ToolListChangedNotification,
    new ToolListChangedNotificationParams());

// After a specific resource's content changes
await server.SendNotificationAsync(
    NotificationMethods.ResourceUpdatedNotification,
    new ResourceUpdatedNotificationParams { Uri = "config://app/settings" });
```

---

## 7. What Comes Next {#whats-next}

The roadmap from here, in order:

```
Sprint 1 ✓   Host + Client configured
             Protocol handshake working
             tools/list discovered

Sprint 2 →   tools/call — invoke a tool and handle the response
             Handle TextContentBlock, ImageContentBlock, error cases

Sprint 3 →   resources/list + resources/read
             Subscribe to resource change notifications

Sprint 4 →   prompts/list + prompts/get
             Inject prompt messages into an LLM conversation

Sprint 5 →   Build your first .NET MCP server
             Define tools with [McpServerToolType] / [McpServerTool]
             Wire it up over stdio, then test with our client

Sprint 6 →   DI in the server — inject services into tool methods
             Logging, configuration, DbContext

Sprint 7 →   HTTP transport — server as a standalone web service
             ASP.NET Core + ModelContextProtocol.AspNetCore
             Authentication with headers or OAuth

Sprint 8 →   LLM integration
             Hand tools to IChatClient (Microsoft.Extensions.AI)
             Full agentic loop: LLM decides → client calls → result fed back
```

Each sprint is one complete, runnable thing before moving on. No skipping ahead.

---

*Generated during Sprint 1 · ModelContextProtocol 1.2 · May 2026*
*Official SDK docs: https://csharp.sdk.modelcontextprotocol.io*
*Official GitHub: https://github.com/modelcontextprotocol/csharp-sdk*
