using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace McpServerDotNet;

[McpServerToolType]
public static class LongRunningTools
{
    // ── MCPSERVER CONTEXT ─────────────────────────────────────────
    // McpServer gives you the live session.
    // No caller arguments — purely introspection.

    [McpServerTool, Description("Returns information about the connected client")]
    public static string GetClientInfo(McpServer server)
    {
        var info = server.ClientInfo;
        var caps = server.ClientCapabilities;

        return $"Client name:    {info?.Name ?? "unknown"}\n" +
               $"Client version: {info?.Version ?? "unknown"}\n" +
               $"Supports roots: {caps?.Roots is not null}\n" +
               $"Supports sampling: {caps?.Sampling is not null}";
    }

    // ── PROGRESS + CANCELLATION ───────────────────────────────────
    // IProgress<ProgressNotificationValue> lets you push updates
    // to the client while the tool is still running.
    // CancellationToken lets the client abort mid-execution.
    // Both are injected by the MCP runtime — not from DI, not from the caller.

    [McpServerTool, Description(
        "Simulates a long-running operation with progress updates. " +
        "Runs the given number of steps, reporting progress after each one.")]
    public static async Task<string> RunWithProgress(
        [Description("Number of steps to simulate (1–10)")] int steps,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken)
    {
        // clamp to a sensible range
        steps = Math.Clamp(steps, 1, 10);

        for (int i = 1; i <= steps; i++)
        {
            // respect client cancellation — check before each unit of work
            cancellationToken.ThrowIfCancellationRequested();

            // simulate work
            await Task.Delay(500, cancellationToken);

            // report progress: current step / total steps + human label
            progress.Report(new ProgressNotificationValue
            {
                Progress = i,
                Total = steps,
                Message = $"Completed step {i} of {steps}"
            });
        }

        return $"Finished all {steps} steps successfully.";
    }

    // ── ALL THREE TOGETHER ────────────────────────────────────────
    // McpServer + IProgress + CancellationToken in one method.
    // This is the full pattern for any real long-running agentic tool.

    [McpServerTool, Description(
        "Processes a list of items with progress reporting and cancellation support. " +
        "Each item name is processed as a separate step.")]
    public static async Task<string> ProcessItems(
        [Description("Comma-separated list of item names to process")] string items,
        McpServer server,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken)
    {
        var itemList = items
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<string>();
        int total = itemList.Length;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = itemList[i];

            // simulate per-item work
            await Task.Delay(400, cancellationToken);

            var processed = $"[{item.ToUpper()}]";
            results.Add(processed);

            progress.Report(new ProgressNotificationValue
            {
                Progress = i + 1,
                Total = total,
                Message = $"Processed '{item}' ({i + 1}/{total})"
            });
        }

        // McpServer.ClientInfo is available anywhere in the tool
        var clientName = server.ClientInfo?.Name ?? "unknown client";

        return $"Processed {total} item(s) for {clientName}:\n" +
               string.Join(", ", results);
    }
}