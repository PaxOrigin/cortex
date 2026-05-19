using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Scalar.AspNetCore;
using Serilog;

IChatClient client = new OllamaApiClient("http://localhost:11434", "Qwen2.5");

var builder = WebApplication.CreateBuilder(args);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();
// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddLogging(p => p.AddSerilog());
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSingleton<IChatClient>(p => new ChatClientBuilder(client)
    .UseLogging()
    .UseDistributedCache()
    .UseFunctionInvocation()
    .Build(p));
builder.Services.AddSingleton<IChatHistoryService, ChatHistoryService>();

var app = builder.Build();
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.MapGet("/chat/stream/", ([FromQuery] string prompt, IChatClient chatClient,
    ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    logger.LogInformation("Received request for chat stream: {Prompt}", prompt);

    async IAsyncEnumerable<string> StreamTokens(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in chatClient.GetStreamingResponseAsync(
            [
                new ChatMessage(ChatRole.System, "You are a helpful assistant."),
                new ChatMessage(ChatRole.User, prompt)
            ], cancellationToken: ct))
        {
            yield return update.Text ?? string.Empty;
        }
    }

    return TypedResults.ServerSentEvents(StreamTokens(cancellationToken), eventType: "chat");
});


app.MapGet("/chat/history/stream", ([FromQuery] string prompt, IChatClient chatClient,
    IChatHistoryService historyService, ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    historyService.AddMessage(new ChatMessage(ChatRole.User, prompt));
    string response = string.Empty;

    async IAsyncEnumerable<string> StreamTokens(
        [EnumeratorCancellation] CancellationToken ct)
    {

        await foreach (var update in chatClient.GetStreamingResponseAsync(
            historyService.GetHistory(), cancellationToken: ct))
        {
            response += update.Text;
            yield return update.Text ?? string.Empty;
        }
        historyService.AddMessage(new ChatMessage(ChatRole.Assistant, response));
    }
    return TypedResults.ServerSentEvents(StreamTokens(cancellationToken), eventType: "chat");
});

app.Run();

