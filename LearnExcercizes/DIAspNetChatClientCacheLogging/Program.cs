using Microsoft.Extensions.AI;
using Serilog;
using Serilog.Events;
using OllamaSharp;
using AI.SystemPrompts;
using AI.VolatileTools;
using Scalar.AspNetCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;


Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.AI", LogEventLevel.Debug)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}          {Message:lj}{NewLine}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.AddOpenApi();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddKeyedChatClient("ollama-router", services =>
    new ChatClientBuilder(new OllamaApiClient(new Uri("http://localhost:11434"), "Qwen2.5"))
    .UseLogging()
    .UseDistributedCache()
    .Build(services));
builder.Services.AddKeyedChatClient("ollama-volatile-tools", services =>
    new ChatClientBuilder(new OllamaApiClient(new Uri("http://localhost:11434"), "Qwen2.5"))
    .UseLogging()
    .UseFunctionInvocation()
    .Build(services));
builder.Services.AddKeyedChatClient("ollama-cached", services =>
    new ChatClientBuilder(new OllamaApiClient(new Uri("http://localhost:11434"), "Qwen2.5"))
    .UseLogging()
    .UseDistributedCache()
    .Build(services));
builder.Services.AddScoped<IVolatileTools, VolatileTools>();

var app = builder.Build();
app.MapOpenApi();
app.MapScalarApiReference();

app.MapGet("/", () => "Hello World!");
app.MapPost("/chat", async (
    ChatRequest request,
    ILogger<Program> logger,
    IVolatileTools volatileTools,
    IDistributedCache cache,
    [FromKeyedServices("ollama-router")] IChatClient router,
    [FromKeyedServices("ollama-volatile-tools")] IChatClient volatileAgent,
    [FromKeyedServices("ollama-cached")] IChatClient cacheAgent
    ) =>
{
    var chatHistory = new List<ChatMessage>();
    if (request.ConversationId == null)
    {
        request = request with { ConversationId = Guid.NewGuid() };
    }
    else
    {
        logger.LogInformation("Cache miss for conversation {ConversationId}", request.ConversationId);
        var cachedHistory = await cache.GetStringAsync(request.ConversationId.ToString()!);
        if (cachedHistory != null)
        {
            chatHistory = JsonSerializer.Deserialize<List<ChatMessage>>(cachedHistory) ?? new List<ChatMessage>();
        }
    }

    var lastMessage = new ChatMessage(ChatRole.User, request.Text);
    chatHistory.Add(lastMessage);

    try
    {
        logger.LogInformation("Caching conversation {ConversationId} with {MessageCount} messages", request.ConversationId, chatHistory.Count);
        var serializedHistory = JsonSerializer.Serialize(chatHistory);
        await cache.SetStringAsync(request.ConversationId.ToString()!, serializedHistory, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error caching conversation {ConversationId}", request.ConversationId);
    }


    var clientToUse = await router.GetResponseAsync(
        new List<ChatMessage>()
        {
            new ChatMessage(ChatRole.System, SystemPrompts.Router),
            new ChatMessage(ChatRole.User, request.Text)
        });
    logger.LogInformation("Router response: {Client}", clientToUse.Text);

    var client = clientToUse.Text switch
    {
        "cache-agent" => cacheAgent,
        "volatile-agent" => volatileAgent,
        _ => throw new Exception("Unknown agent")
    };

    logger.LogInformation("Routing request '{Request}' to {Client}", request.Text, clientToUse.Text);

    List<AITool> tools = client == volatileAgent ?
        volatileTools.GetTools()
    : new List<AITool>();
    logger.LogInformation("Providing {ToolCount} tools to {Client}", tools.Count, clientToUse.Text);

    ChatOptions options = new ChatOptions()
    {
        Tools = tools
    };

    var systemPrompt = client == volatileAgent ?
    SystemPrompts.Volatile :
    SystemPrompts.Cached;

    var historyAndSystemPrompt = new List<ChatMessage>()
    {
        new ChatMessage(ChatRole.System, systemPrompt)
    };
    historyAndSystemPrompt.AddRange(chatHistory);

    var response = await client.GetResponseAsync(
        historyAndSystemPrompt,
        options
    );

    chatHistory.Add(new ChatMessage(ChatRole.Assistant, response.Text));
    try
    {
        logger.LogInformation("Caching conversation {ConversationId} with {MessageCount} messages", request.ConversationId, historyAndSystemPrompt.Count + 1);
        var serializedHistory = JsonSerializer.Serialize(chatHistory);
        await cache.SetStringAsync(request.ConversationId.ToString()!, serializedHistory, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error caching conversation {ConversationId}", request.ConversationId);
    }

    logger.LogInformation("Response from {Client}: {Response}", clientToUse.Text, response.Text);
    return new ChatResponse(response.Text, request.ConversationId.Value);
});

app.MapGet("/clear-session/{conversationId:guid}", async (Guid conversationId, IDistributedCache cache, ILogger<Program> logger) =>
{
    try
    {
        await cache.RemoveAsync(conversationId.ToString());
        logger.LogInformation("Cleared session for conversation {ConversationId}", conversationId);
        return Results.Ok($"Session cleared for conversation {conversationId}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error clearing session for conversation {ConversationId}", conversationId);
        return Results.Problem($"Error clearing session for conversation {conversationId}");
    }
});
app.MapGet("/session/{conversationId:guid}", async (Guid conversationId, IDistributedCache cache, ILogger<Program> logger) =>
{
    try
    {
        var cachedHistory = await cache.GetStringAsync(conversationId.ToString());
        if (cachedHistory != null)
        {
            var chatHistory = JsonSerializer.Deserialize<List<ChatMessage>>(cachedHistory) ?? new List<ChatMessage>();
            logger.LogInformation("Retrieved session for conversation {ConversationId} with {MessageCount} messages", conversationId, chatHistory.Count);
            return Results.Ok(chatHistory);
        }
        else
        {
            logger.LogInformation("No session found for conversation {ConversationId}", conversationId);
            return Results.NotFound($"No session found for conversation {conversationId}");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error retrieving session for conversation {ConversationId}", conversationId);
        return Results.Problem($"Error retrieving session for conversation {conversationId}");
    }
});

app.Run();

record SessionState(IList<ChatMessage> ChatHistory, DateTimeOffset LastActivity);
record ChatRequest(string Text, Guid? ConversationId);
record ChatResponse(string Text, Guid ConversationId);
