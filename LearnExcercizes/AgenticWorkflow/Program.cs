// Program.cs — Content Analysis Pipeline
// Wave 3 consolidation: ChatClientAgent · Middleware · Workflow · Events

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// ── IChatClient ───────────────────────────────────────────────────────────────
IChatClient ollamaClient = new ChatClientBuilder(
    new OllamaApiClient(new Uri("http://localhost:11434"), "qwen2.5"))
    .Build();

// ── Middleware ────────────────────────────────────────────────────────────────

// Security — blocca input con tentativi di injection
async Task<AgentResponse> SecurityMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken ct)
{
    var last = messages.LastOrDefault()?.Text ?? string.Empty;
    if (last.Contains("ignore all", StringComparison.OrdinalIgnoreCase) ||
        last.Contains("jailbreak", StringComparison.OrdinalIgnoreCase))
    {
        Log.Warning("[Security] Request blocked — potential injection detected.");
        return new AgentResponse([new ChatMessage(ChatRole.Assistant, "Request blocked.")]);
    }
    return await innerAgent.RunAsync(messages, session, options, ct);
}

// Logging — logga input/output di ogni agente
async Task<AgentResponse> LoggingMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken ct)
{
    Log.Debug("[{Agent}] Invoked — {Count} messages in context", innerAgent.Name, messages.Count());
    var response = await innerAgent.RunAsync(messages, session, options, ct);
    Log.Information("[{Agent}] → {Text}", innerAgent.Name, response.Text);
    return response;
}

// ── Agents ────────────────────────────────────────────────────────────────────

AIAgent summarizerAgent = new ChatClientAgent(
    ollamaClient,
    name: "Summarizer",
    description: "Summarizes text content into a concise 2-3 sentence summary.",
    instructions:
        "You are a text summarizer. " +
        "Given any text, produce a concise 2-3 sentence summary. " +
        "Output only the summary, nothing else.")
    .AsBuilder()
    .Use(runFunc: SecurityMiddleware, runStreamingFunc: null)
    .Use(runFunc: LoggingMiddleware, runStreamingFunc: null)
    .Build();

AIAgent classifierAgent = new ChatClientAgent(
    ollamaClient,
    name: "Classifier",
    description: "Classifies text into a single category.",
    instructions:
        "You are a content classifier. " +
        "Given a text summary, classify it into exactly one of these categories: " +
        "[Technology, Science, Business, Politics, Entertainment, Sports, Other]. " +
        "Output only the category name, nothing else.")
    .AsBuilder()
    .Use(runFunc: LoggingMiddleware, runStreamingFunc: null)
    .Build();

// ── Workflow ──────────────────────────────────────────────────────────────────

// Sequential: Summarizer → Classifier
var workflow = new WorkflowBuilder(summarizerAgent)
    .AddEdge(summarizerAgent, classifierAgent)
    .Build();

// ── Main loop ─────────────────────────────────────────────────────────────────

Log.Information("Content Analysis Pipeline ready. Commands: 'quit' to exit.");

while (true)
{
    Console.Write("\nEnter text to analyze: ");
    string input = Console.ReadLine() ?? string.Empty;

    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

    Log.Information("Pipeline started — input length: {Length} chars", input.Length);

    try
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new ChatMessage(ChatRole.User, input));

        // TurnToken segnala agli agenti nel workflow di iniziare il processing.
        // Richiesto quando si usano AIAgent come executor.
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            switch (evt)
            {
                case SuperStepStartedEvent:
                    Log.Debug("Superstep started");
                    break;

                case ExecutorInvokedEvent invoked:
                    Log.Debug("Executor [{Id}] invoked", invoked.ExecutorId);
                    break;

                case AgentResponseUpdateEvent update:
                    if (update.Data is AgentResponseUpdate responseUpdate)
                        Console.Write(responseUpdate.Text);
                    break;

                case AgentResponseEvent response:
                    // Risposta completa di un agente
                    Console.WriteLine();
                    Log.Information("Executor [{Id}] completed", response.ExecutorId);
                    break;

                case WorkflowOutputEvent output:
                    // Output finale del workflow (se YieldOutputAsync viene chiamato)
                    Log.Information("Workflow output: {Data}", output.Data?.ToString() ?? "null");
                    break;

                case WorkflowErrorEvent error:
                    Log.Error("Pipeline error: {Message}", error.Exception?.Message);
                    goto nextIteration;
            }
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Unexpected error during pipeline execution");
    }

nextIteration:;
}

Log.Information("Shutting down.");
Log.CloseAndFlush();