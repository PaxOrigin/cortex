# MCP Book — Chapter 7 Addendum
### Testing, Securing, and Sharing Your MCP Server
#### .NET / C# — verified against ModelContextProtocol SDK v1.2

> **SDK reference:** https://csharp.sdk.modelcontextprotocol.io  
> Packages: `ModelContextProtocol` · `ModelContextProtocol.AspNetCore` · `ModelContextProtocol.Core`

---

## Table of Contents

1. [Testing Your Server](#testing-your-server)
   - [Unit Testing](#unit-testing)
   - [Integration Testing — InMemoryTransport](#integration-testing--inmemory-transport)
   - [Integration Testing — HTTP (WebApplicationFactory)](#integration-testing--http-webapplicationfactory)
   - [End-to-End with MCP Inspector](#end-to-end-with-mcp-inspector)
2. [Evaluations](#evaluations)
   - [Tool Choice Accuracy Script](#tool-choice-accuracy-script)
3. [Server Security](#server-security)
   - [Injection Vulnerabilities](#injection-vulnerabilities)
   - [OAuth — ASP.NET Core Auth Middleware](#oauth--aspnet-core-auth-middleware)
   - [RBAC — ASP.NET Core Authorization Policies](#rbac--aspnet-core-authorization-policies)
   - [Sandboxing — Docker](#sandboxing--docker)
   - [Observability — OpenTelemetry](#observability--opentelemetry)
4. [Sharing Your Server](#sharing-your-server)
   - [Local Stdio Distribution](#local-stdio-distribution)
   - [Remote HTTP Deployment](#remote-http-deployment)
   - [Dockerfile](#dockerfile)
   - [MCP Registry](#mcp-registry)
5. [Security Frameworks — .NET Lens](#security-frameworks--net-lens)
6. [GitHub Project Notes](#github-project-notes)

---

## Testing Your Server

### Unit Testing

Because `[McpServerTool]` methods are **ordinary C# static or instance methods**,
unit testing them requires no MCP-specific infrastructure. Test the logic directly.

```csharp
// MyTools.cs — the tool
[McpServerToolType]
public static class CalculatorTools
{
    [McpServerTool, Description("Adds two numbers.")]
    public static double Add(double a, double b) => a + b;

    [McpServerTool, Description("Divides a by b. Throws on zero.")]
    public static double Divide(double a, double b)
    {
        if (b == 0) throw new McpException("Division by zero is not allowed.");
        return a / b;
    }
}
```

```csharp
// CalculatorToolsTests.cs — xUnit
public class CalculatorToolsTests
{
    [Fact]
    public void Add_ReturnsCorrectSum()
    {
        var result = CalculatorTools.Add(3, 4);
        Assert.Equal(7, result);
    }

    [Fact]
    public void Divide_ByZero_ThrowsMcpException()
    {
        var ex = Assert.Throws<McpException>(() =>
            CalculatorTools.Divide(10, 0));
        Assert.Contains("zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(10, 2, 5)]
    [InlineData(-6, 3, -2)]
    [InlineData(7, 1, 7)]
    public void Divide_ReturnsCorrectQuotient(double a, double b, double expected)
    {
        Assert.Equal(expected, CalculatorTools.Divide(a, b));
    }
}
```

**Tools with injected services** — test by passing mock dependencies directly:

```csharp
[McpServerToolType]
public static class KnowledgeTools
{
    [McpServerTool, Description("Searches the knowledge base.")]
    public static string Search(string query, IKnowledgeBase kb) =>
        kb.Find(query) ?? "No results found.";
}

// Test
public class KnowledgeToolsTests
{
    [Fact]
    public void Search_ReturnsResult_WhenFound()
    {
        var mockKb = Substitute.For<IKnowledgeBase>(); // NSubstitute
        mockKb.Find("MCP").Returns("Model Context Protocol");

        var result = KnowledgeTools.Search("MCP", mockKb);

        Assert.Equal("Model Context Protocol", result);
    }

    [Fact]
    public void Search_ReturnsFallback_WhenNotFound()
    {
        var mockKb = Substitute.For<IKnowledgeBase>();
        mockKb.Find(Arg.Any<string>()).Returns((string?)null);

        var result = KnowledgeTools.Search("unknown", mockKb);

        Assert.Equal("No results found.", result);
    }
}
```

> **Recommended test packages:** `xunit`, `xunit.runner.visualstudio`, `NSubstitute`
> (or `Moq`), `coverlet.collector` for coverage.

---

### Integration Testing — InMemory Transport

The SDK ships with an `InMemoryTransport` sample and supports in-process
client-server testing via `StreamClientTransport` + `MemoryStream`.
This is the fastest way to test the full MCP protocol without any process or network overhead.

```xml
<!-- MyServer.Tests.csproj -->
<PackageReference Include="ModelContextProtocol" Version="1.2.*" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.*" />
<PackageReference Include="xunit" Version="2.*" />
```

```csharp
// McpIntegrationTestFixture.cs
public sealed class McpIntegrationTestFixture : IAsyncDisposable
{
    private readonly IHost _host;
    public McpClient Client { get; }

    private McpIntegrationTestFixture(IHost host, McpClient client)
    {
        _host = host;
        Client = client;
    }

    public static async Task<McpIntegrationTestFixture> CreateAsync(
        Action<IMcpServerBuilder>? configure = null)
    {
        // Two linked streams: server reads from one, client reads from the other
        var serverToClient = new BlockingStream();
        var clientToServer = new BlockingStream();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(o =>
            o.LogToStandardErrorThreshold = LogLevel.Warning);

        var mcpBuilder = builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer, serverToClient)
            .WithTools<CalculatorTools>();   // register your tools/prompts/resources

        configure?.Invoke(mcpBuilder);

        var host = builder.Build();
        await host.StartAsync();

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(serverToClient, clientToServer),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "TestClient", Version = "1.0" }
            });

        return new McpIntegrationTestFixture(host, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }
}
```

```csharp
// CalculatorIntegrationTests.cs
public class CalculatorIntegrationTests : IAsyncLifetime
{
    private McpIntegrationTestFixture _fixture = null!;

    public async Task InitializeAsync() =>
        _fixture = await McpIntegrationTestFixture.CreateAsync();

    public async Task DisposeAsync() =>
        await _fixture.DisposeAsync();

    [Fact]
    public async Task ListTools_ReturnsCalculatorTools()
    {
        var tools = await _fixture.Client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "add");
        Assert.Contains(tools, t => t.Name == "divide");
    }

    [Fact]
    public async Task CallTool_Add_ReturnsCorrectResult()
    {
        var result = await _fixture.Client.CallToolAsync(
            "add",
            new Dictionary<string, object?> { ["a"] = 3.0, ["b"] = 4.0 });

        Assert.False(result.IsError);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.Equal("7", text);
    }

    [Fact]
    public async Task CallTool_DivideByZero_ReturnsError()
    {
        var result = await _fixture.Client.CallToolAsync(
            "divide",
            new Dictionary<string, object?> { ["a"] = 10.0, ["b"] = 0.0 });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task CallTool_Progress_ReportsUpdates()
    {
        var progressUpdates = new List<ProgressNotificationValue>();

        await _fixture.Client.CallToolAsync(
            "slow_operation",
            new Dictionary<string, object?> { ["steps"] = 4 },
            progress: new Progress<ProgressNotificationValue>(v =>
                progressUpdates.Add(v)));

        Assert.Equal(4, progressUpdates.Count);
        Assert.Equal(4, progressUpdates.Last().Progress);
    }
}
```

> **Note on `BlockingStream`:** the SDK's `InMemoryTransport` sample shows a custom
> blocking stream implementation. A simpler approach for most tests is using
> `System.IO.Pipelines.Pipe` — each `Pipe` gives you a `PipeReader` + `PipeWriter`
> that can be wrapped in `PipeReaderStream` / `PipeWriterStream`.

---

### Integration Testing — HTTP (WebApplicationFactory)

For HTTP servers, use `WebApplicationFactory<TProgram>` from
`Microsoft.AspNetCore.Mvc.Testing`. No real port is opened — all traffic is in-process.

```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="9.*" />
```

```csharp
public class HttpServerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HttpServerIntegrationTests(WebApplicationFactory<Program> factory)
        => _factory = factory;

    [Fact]
    public async Task McpServer_ListsTools_OverHttp()
    {
        var httpClient = _factory.CreateClient();

        // Point an McpClient at the in-process test server
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint      = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, httpClient);  // inject the test HttpClient

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();

        Assert.NotEmpty(tools);
    }
}
```

**Testing authenticated endpoints:**
```csharp
var factory = _factory.WithWebHostBuilder(builder =>
    builder.ConfigureTestServices(services =>
    {
        // Replace real token verifier with a stub
        services.AddSingleton<ITokenVerifier, AlwaysValidTokenVerifier>();
    }));
```

---

### End-to-End with MCP Inspector

The book's development workflow translates directly:

| Book (Python) | .NET equivalent |
|---|---|
| `uv run python server.py` | `dotnet run --project src/MyServer` |
| Restart button in Inspector | Restart button (reconnect) |
| Print to `stderr` | `ILogger` with `LogToStandardErrorThreshold = LogLevel.Trace` |
| `raise NotImplementedError` | `throw new NotImplementedException()` |

**Workflow:**
1. `dotnet run` — server starts, Inspector connects via stdio or HTTP.
2. Implement one tool/prompt/resource → restart → test in Inspector.
3. Check History pane (raw JSON-RPC) and Server Notifications pane (logs, progress).
4. Move to Sampling / Elicitations / Roots views for client-capability testing.

---

## Evaluations

### Tool Choice Accuracy Script

The Python eval script from the book (Example 7-1) translates to .NET using
`Microsoft.Extensions.AI` and the Anthropic SDK.

```csharp
// ToolEvalRunner.cs
using Anthropic.SDK;
using Microsoft.Extensions.AI;

// Prompt → expected tool name pairs
var testCases = new[]
{
    ("What is 15 plus 27?",                "add"),
    ("Divide 100 by 4",                    "divide"),
    ("Search for information about MCP",   "search_knowledge_base"),
    ("Add these numbers: 8 and 12",        "add"),
};

// Build the IChatClient pointing at your MCP server's tools
// In a real eval loop, you would connect to your actual server
var tools = new List<AIFunction>
{
    AIFunctionFactory.Create(
        ([Description("First number")] double a,
         [Description("Second number")] double b) => a + b,
        "add", "Adds two numbers"),
    AIFunctionFactory.Create(
        ([Description("First number")] double a,
         [Description("Second number")] double b) => a / b,
        "divide", "Divides a by b"),
    AIFunctionFactory.Create(
        ([Description("Search query")] string query) => "results",
        "search_knowledge_base", "Searches the knowledge base"),
};

IChatClient chatClient = new AnthropicClient(apiKey)
    .Messages
    .AsIChatClient("claude-sonnet-4-5-20250929")
    .AsBuilder()
    .UseFunctionInvocation()    // auto-invokes tools
    .Build();

int correct = 0;

foreach (var (prompt, expectedTool) in testCases)
{
    var response = await chatClient.GetResponseAsync(
        prompt,
        new ChatOptions { Tools = tools, ToolMode = ChatToolMode.Auto });

    // Find the first function call in the response
    var toolCall = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<FunctionCallContent>()
        .FirstOrDefault();

    bool hit = toolCall?.Name == expectedTool;
    if (hit) correct++;

    Console.WriteLine($"[{(hit ? "✓" : "✗")}] '{prompt}'");
    Console.WriteLine($"    Expected: {expectedTool}  Got: {toolCall?.Name ?? "none"}");
}

double accuracy = (double)correct / testCases.Length * 100;
Console.WriteLine($"\nTool choice accuracy: {correct}/{testCases.Length} ({accuracy:F1}%)");
```

**More end-to-end with real MCP server:**
```csharp
// Connect to your actual MCP server and use its tools with an LLM
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = ["run", "--project", "../MyServer"]
});

await using var mcpClient = await McpClient.CreateAsync(transport);
var mcpTools = await mcpClient.ListToolsAsync();  // List<McpClientTool>

// McpClientTool extends AIFunction — plug directly into IChatClient
var llmResponse = await chatClient.GetResponseAsync(
    prompt,
    new ChatOptions { Tools = [.. mcpTools] });
```

> `McpClientTool` extends `AIFunction` — this is the real power of the .NET SDK's
> MEAI integration. No adapter layer needed between MCP and the LLM client.

**Prompt evaluation loop:**

```csharp
// Evaluate a prompt exposed by your server across multiple inputs
var promptResult = await mcpClient.GetPromptAsync("summarize", new()
{
    ["userText"] = inputText,
    ["count"]    = "3"
});

// Feed prompt messages to the LLM
var messages = promptResult.Messages
    .Select(m => new ChatMessage(
        m.Role == Role.User ? ChatRole.User : ChatRole.Assistant,
        m.Content is TextContentBlock t ? t.Text : ""))
    .ToList();

var llmResult = await chatClient.GetResponseAsync(messages);

// Simple assertion: response contains at least 3 numbered items
bool passesBasicCheck = Enumerable.Range(1, 3)
    .All(i => llmResult.Text.Contains($"{i}."));
```

---

## Server Security

### Injection Vulnerabilities

**SQL Injection — EF Core (already safe by default):**
```csharp
// ✅ Safe — EF Core uses parameterized queries automatically
var results = await db.Articles
    .Where(a => a.Title.Contains(searchQuery))
    .ToListAsync(ct);

// ✅ Safe — raw SQL with parameters
var results = await db.Articles
    .FromSqlRaw("SELECT * FROM Articles WHERE Title LIKE {0}", $"%{searchQuery}%")
    .ToListAsync(ct);

// ❌ Never do this — string interpolation bypasses parameterization
var results = await db.Articles
    .FromSqlRaw($"SELECT * FROM Articles WHERE Title LIKE '%{searchQuery}%'")
    .ToListAsync(ct);
```

**Prompt Injection — defense patterns:**
```csharp
// Pattern 1: Allowlist for tool arguments that determine code paths
private static readonly HashSet<string> AllowedOperations =
    new(["add", "subtract", "multiply", "divide"], StringComparer.OrdinalIgnoreCase);

[McpServerTool, Description("Performs a math operation.")]
public static double Calculate(string operation, double a, double b)
{
    if (!AllowedOperations.Contains(operation))
        throw new McpException($"Unknown operation: {operation}");
    // ...
}

// Pattern 2: Sanitize content fetched from untrusted URLs
[McpServerTool, Description("Fetches a webpage.")]
public static async Task<string> FetchPage(string url, HttpClient http)
{
    var uri = new Uri(url);  // validates format
    if (uri.Scheme is not ("http" or "https"))
        throw new McpException("Only http/https URLs are allowed.");

    var html = await http.GetStringAsync(uri);

    // Strip scripts, remove hidden elements, target only visible text
    var doc = new HtmlDocument();  // HtmlAgilityPack
    doc.LoadHtml(html);
    doc.DocumentNode.Descendants()
        .Where(n => n.Name is "script" or "style")
        .ToList()
        .ForEach(n => n.Remove());

    return doc.DocumentNode.InnerText;
}
```

**File path traversal — denylist for sensitive directories:**
```csharp
private static readonly string[] DeniedPaths =
[
    "/etc", "/root", "~/.ssh", "~/.aws",
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.ssh"
];

[McpServerTool, Description("Reads a file.")]
public static async Task<string> ReadFile(string path, CancellationToken ct)
{
    var abs = Path.GetFullPath(path);
    if (DeniedPaths.Any(d => abs.StartsWith(Path.GetFullPath(d))))
        throw new McpException($"Access denied: {path}");

    return await File.ReadAllTextAsync(abs, ct);
}
```

---

### OAuth — ASP.NET Core Auth Middleware

The Python SDK requires implementing `TokenVerifier` manually.
In .NET, standard **ASP.NET Core authentication middleware** handles this —
no MCP-specific auth infrastructure needed.

```csharp
// Program.cs — HTTP server with JWT bearer auth
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://your-auth-server.com";
        options.Audience  = "mcp-server";
        // Token validation is automatic
    });

builder.Services
    .AddAuthorization(options =>
    {
        options.AddPolicy("McpAccess", policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim("scope", "mcp:read"));
    });

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<MyTools>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Require auth on all MCP endpoints
app.MapMcp().RequireAuthorization("McpAccess");

app.Run();
```

**Access the current user inside a tool:**
```csharp
[McpServerTool, Description("Returns data for the authenticated user.")]
public static string GetMyData(IHttpContextAccessor httpContextAccessor)
{
    var user = httpContextAccessor.HttpContext?.User;
    var userId = user?.FindFirst("sub")?.Value ?? "anonymous";
    return $"Data for user: {userId}";
}
```

> **vs Python SDK:** Python requires implementing a `TokenVerifier` class.
> In .NET you compose standard ASP.NET Core middleware — same auth pipeline
> you use for REST APIs, no MCP-specific learning curve.

---

### RBAC — ASP.NET Core Authorization Policies

```csharp
// Define roles and policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly",  p => p.RequireRole("admin"));
    options.AddPolicy("ReadOnly",   p => p.RequireClaim("scope", "mcp:read"));
    options.AddPolicy("ReadWrite",  p => p.RequireClaim("scope", "mcp:write"));
});

// Enforce per-tool via filter or within the tool
[McpServerTool, Description("Deletes a record. Admin only.")]
public static async Task DeleteRecord(
    string id,
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authz,
    CancellationToken ct)
{
    var user   = httpContextAccessor.HttpContext!.User;
    var result = await authz.AuthorizeAsync(user, "AdminOnly");

    if (!result.Succeeded)
        throw new McpException("Insufficient permissions.");

    // proceed with deletion...
}
```

**Or via MCP handler filters** (cleaner for cross-cutting concerns):
```csharp
// McpServerHandlerFilter — applied to all tool calls
public class AuthorizationFilter : IMcpServerHandlerFilter
{
    public async ValueTask<CallToolResult> OnCallToolAsync(
        RequestContext<CallToolRequestParams> context,
        Func<RequestContext<CallToolRequestParams>, ValueTask<CallToolResult>> next,
        CancellationToken ct)
    {
        // Check auth before any tool executes
        var httpContext = context.Services?.GetService<IHttpContextAccessor>()
                                          ?.HttpContext;
        if (httpContext?.User.Identity?.IsAuthenticated != true)
            throw new McpException("Authentication required.");

        return await next(context);
    }
}

// Registration
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<MyTools>()
    .WithHandlerFilter<AuthorizationFilter>();
```

---

### Sandboxing — Docker

**Stdio server (local):**
```dockerfile
# Dockerfile — local stdio MCP server
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS base
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-self-contained

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Run server over stdio (client manages the process)
ENTRYPOINT ["dotnet", "MyServer.dll"]
```

Client connecting to the containerized server:
```csharp
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command          = "docker",
    Arguments        = ["run", "-i", "--rm",
                        "-e", "MY_API_KEY",
                        "my-mcp-server"],
    EnvironmentVariables = new() { ["MY_API_KEY"] = apiKey }
});
```

**HTTP server (remote):**
```dockerfile
# Dockerfile — remote HTTP MCP server
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "MyServer.dll"]
```

---

### Observability — OpenTelemetry

```csharp
// Program.cs
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("MyMcpServer")           // custom spans
            .AddOtlpExporter();                 // → Grafana, Jaeger, etc.
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter("MyMcpServer")
            .AddOtlpExporter();
    });

// Structured logging
builder.Logging
    .AddOpenTelemetry(o => o.AddOtlpExporter());
```

**Instrument a tool with a custom span:**
```csharp
private static readonly ActivitySource Source = new("MyMcpServer");

[McpServerTool, Description("Processes a record.")]
public static async Task<string> ProcessRecord(
    string id,
    ILogger<MyTools> logger,
    CancellationToken ct)
{
    using var activity = Source.StartActivity("ProcessRecord");
    activity?.SetTag("record.id", id);

    logger.LogInformation("Processing record {Id}", id);

    var result = await DoWork(id, ct);

    activity?.SetTag("record.status", "ok");
    return result;
}
```

> **Recommended packages:** `OpenTelemetry.Extensions.Hosting`,
> `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`,
> `Serilog.AspNetCore` (optional structured logging).

---

## Sharing Your Server

### Local Stdio Distribution

Share a GitHub repo with a `README.md` showing how to add the server
to different clients:

**Claude Desktop (`claude_desktop_config.json`):**
```json
{
  "mcpServers": {
    "my-server": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/MyServer"],
      "env": { "MY_API_KEY": "your-key" }
    }
  }
}
```

**Published binary via NuGet tool:**
```json
{
  "mcpServers": {
    "my-server": {
      "command": "my-mcp-server",
      "args": []
    }
  }
}
```

Publish as a dotnet tool:
```xml
<!-- MyServer.csproj -->
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>my-mcp-server</ToolCommandName>
  <PackageId>MyMcpServer</PackageId>
</PropertyGroup>
```
```bash
dotnet pack
dotnet tool install --global MyMcpServer
```

> **Equivalent of PyPI + `uv run mcp install`:** `dotnet tool install --global`
> is the idiomatic .NET equivalent. Users need only .NET runtime installed.

---

### Remote HTTP Deployment

**Stateless HTTP server** (recommended for most remote deployments):

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        // Stateless = no session affinity required
        // Disables sampling/elicitation/roots (no server-to-client requests)
        options.Stateless = true;
    })
    .WithTools<MyTools>();

var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
```

**Stateful HTTP server** (required for sampling, elicitation, roots):
```csharp
builder.Services
    .AddMcpServer()
    .WithHttpTransport()   // stateful by default
    .WithTools<MyTools>();

app.MapMcp("/mcp");
```

> **vs Python/Starlette:** Python needs Starlette/FastAPI as the ASGI framework.
> In .NET, ASP.NET Core IS the production framework. `MapMcp()` is your Starlette
> `Mount`. No additional framework layer needed.

**Multiple servers on one host:**
```csharp
app.MapMcp("/mcp/calculator");
app.MapMcp("/mcp/knowledge");
// each MapMcp maps a separate server registered in DI
```

---

### Dockerfile

```dockerfile
# Dockerfile — production remote HTTP MCP server (.NET 10, Alpine)
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/MyServer/MyServer.csproj \
    -c Release \
    -o /app/publish \
    --no-self-contained \
    -r linux-musl-x64

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# Non-root user for security
RUN adduser -D mcpuser
USER mcpuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "MyServer.dll"]
```

```bash
# Build and run locally
docker build -t my-mcp-server .
docker run -p 8080:8080 -e MY_API_KEY=secret my-mcp-server

# Client connects to:
# http://localhost:8080/mcp
```

**docker-compose for local development with dependencies:**
```yaml
services:
  mcp-server:
    build: .
    ports:
      - "8080:8080"
    environment:
      - ConnectionStrings__Default=Server=db;Database=mcp;...
      - MY_API_KEY=${MY_API_KEY}
    depends_on:
      - db

  db:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: mcp
      POSTGRES_PASSWORD: dev
```

---

### MCP Registry

The MCP server registry works the same regardless of language: register the package
(NuGet instead of PyPI) and use the `mcp-publisher` CLI.

```bash
# 1. Publish to NuGet
dotnet pack src/MyServer/MyServer.csproj -c Release
dotnet nuget push bin/Release/MyServer.*.nupkg --api-key $NUGET_API_KEY

# 2. Install mcp-publisher
npm install -g @modelcontextprotocol/mcp-publisher

# 3. Init and configure server.json
mcp-publisher init

# 4. Edit server.json for NuGet
# Set registryType: "nuget", packageName, version, etc.

# 5. Add ownership proof to README
# <!-- mcp-name: my-mcp-server -->

# 6. Login and publish
mcp-publisher login github
mcp-publisher publish

# 7. Verify
curl "https://registry.modelcontextprotocol.io/v0.1/servers?search=my-mcp-server"
```

---

## Security Frameworks — .NET Lens

### The Lethal Trifecta

When designing your tools, check for this dangerous combination:

| Capability | Example in .NET | Risk |
|---|---|---|
| Access to private data | EF Core queries, file reads, user-scoped APIs | Data exfiltration |
| Untrusted content exposure | `HttpClient.GetStringAsync(url)`, email parsing | Injection vector |
| Third-party communication | `HttpClient` outbound calls, email sending | Exfiltration channel |

**Mitigation checklist for tool design:**
```csharp
// If your tool reads from the web AND can send data out:
// → Sanitize fetched content (strip scripts, hidden text)
// → Validate target URLs against an allowlist
// → Scope OAuth tokens to single repos/resources (not broad PATs)
// → Run server in Docker with network egress rules
// → Use per-user OAuth scopes instead of a single static client ID
```

### MCP Colors in Practice

Before releasing each tool, label it:
- 🔴 **Red** — handles untrusted content (web scraping, email reading, file parsing)
- 🔵 **Blue** — performs critical actions (sending email, deleting records, updating code)
- ⚪ **Neither** — safe standalone (math, formatting, static lookups)

**Design goal: never mix 🔴 and 🔵 in the same server.**

If unavoidable, use structural mitigations:
```csharp
// Instead of one server with both capabilities:
// Server A (Red): reads web content → returns sanitized text
// Server B (Blue): sends notifications → requires explicit user confirmation

// Or: add a gateway-level filter that detects cross-resource access
// (Docker MCP Gateway pattern)
```

---

## GitHub Project Notes

### New folder: `src/07-testing/`

```
src/07-testing/
├── README.md
├── MyServer.Tests/
│   ├── Unit/
│   │   ├── CalculatorToolsTests.cs    (direct method tests)
│   │   └── KnowledgeToolsTests.cs     (mocked dependencies)
│   ├── Integration/
│   │   ├── McpIntegrationTestFixture.cs  (InMemory transport fixture)
│   │   ├── CalculatorIntegrationTests.cs (full protocol tests)
│   │   └── HttpServerTests.cs            (WebApplicationFactory)
│   └── Eval/
│       └── ToolChoiceEvalRunner.cs    (accuracy scoring script)
```

### New folder: `src/07-security/`

```
src/07-security/
├── README.md
├── AuthenticatedServer/
│   ├── Program.cs           (JWT Bearer + MapMcp().RequireAuthorization())
│   └── AuthorizationFilter.cs (IMcpServerHandlerFilter example)
├── Dockerfile               (production multi-stage build)
└── docker-compose.yml       (local dev stack)
```

### README themes

Each README should answer:
1. **Why test MCP servers this way?** (explain InMemory vs HTTP vs unit)
2. **Security layer this covers** (infrastructure / access control / operational)
3. **How to run** (`dotnet test`, `docker build`, etc.)
4. **Python → .NET delta** (brief table: Starlette → ASP.NET Core, PyPI → NuGet, etc.)

---

*Addendum covers: Ch7 Testing · Evaluations · Security · Sharing*  
*Stack: .NET 10 · C# · xUnit · ModelContextProtocol SDK v1.2 · ASP.NET Core · OpenTelemetry*
