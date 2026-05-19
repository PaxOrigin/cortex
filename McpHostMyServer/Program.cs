using ModelContextProtocol.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol;
using System.Text.Json;

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "my-dotnet-server",
    Command = "dotnet",
    Arguments = ["run", "--project", "../McpServerDotNet/McpServerDotNet.csproj"],
});

Console.WriteLine("Starting connection to our MCP server...");
await using var client = await McpClient.CreateAsync(transport);
Console.WriteLine("Connected!");

// ─────────────────────────────────────────────
//  SERVER MANIFESTO
// ─────────────────────────────────────────────

Console.WriteLine($"\n=== Server Manifesto ===");
Console.WriteLine($"  Name:      {client.ServerInfo?.Name}");
Console.WriteLine($"  Version:   {client.ServerInfo?.Version}");
Console.WriteLine();
Console.WriteLine($"  Tools:     {(client.ServerCapabilities?.Tools is not null ? "✓" : "✗")}");
Console.WriteLine($"  Resources: {(client.ServerCapabilities?.Resources is not null ? "✓" : "✗")}");
Console.WriteLine($"    └ Subscribe: {(client.ServerCapabilities?.Resources?.Subscribe is true ? "✓" : "✗")}");
Console.WriteLine($"  Prompts:   {(client.ServerCapabilities?.Prompts is not null ? "✓" : "✗")}");
Console.WriteLine($"  Logging:   {(client.ServerCapabilities?.Logging is not null ? "✓" : "✗")}");
Console.WriteLine($"  Experimental: {(client.ServerCapabilities?.Experimental is not null ? "✓" : "✗")}");

// ─────────────────────────────────────────────
//  TOOLS — list + basic calls + DI + content types
// ─────────────────────────────────────────────

if (client.ServerCapabilities?.Tools is not null)
{
    // ── LIST ──────────────────────────────────
    var toolList = await client.ListToolsAsync();
    Console.WriteLine($"\n=== Tools ===");
    foreach (var tool in toolList)
        Console.WriteLine($"  - {tool.Name}: {tool.Description}");

    // ── BASIC TOOL CALLS ──────────────────────
    Console.WriteLine("\n=== Tool Calls ===");

    var addResult = await client.CallToolAsync(
        "add",
        new Dictionary<string, object?> { ["a"] = 12, ["b"] = 30 }
    );
    Console.WriteLine($"  Add(12, 30)             = {addResult.Content.OfType<TextContentBlock>().First().Text}");

    var mulResult = await client.CallToolAsync(
        "multiply",
        new Dictionary<string, object?> { ["a"] = 6, ["b"] = 7 }
    );
    Console.WriteLine($"  Multiply(6, 7)          = {mulResult.Content.OfType<TextContentBlock>().First().Text}");

    var revResult = await client.CallToolAsync(
        "reverse",
        new Dictionary<string, object?> { ["input"] = "MCP Sprint 5" }
    );
    Console.WriteLine($"  Reverse('MCP Sprint 5') = {revResult.Content.OfType<TextContentBlock>().First().Text}");

    var wrdResult = await client.CallToolAsync(
        "count_words",
        new Dictionary<string, object?> { ["text"] = "the model context protocol is powerful" }
    );
    Console.WriteLine($"  CountWords(...)         = {wrdResult.Content.OfType<TextContentBlock>().First().Text} words");

    // ── DI TOOL CALLS ─────────────────────────
    Console.WriteLine("\n=== DI Tools ===");

    var timeResult = await client.CallToolAsync("get_current_time", new Dictionary<string, object?>());
    Console.WriteLine($"  get_current_time:\n    {timeResult.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n    ")}");

    var fmtResult = await client.CallToolAsync(
        "format_time",
        new Dictionary<string, object?> { ["format"] = "dddd, MMMM dd yyyy — HH:mm:ss" }
    );
    Console.WriteLine($"  format_time:      {fmtResult.Content.OfType<TextContentBlock>().First().Text}");

    var infoResult = await client.CallToolAsync("get_server_info", new Dictionary<string, object?>());
    Console.WriteLine($"  get_server_info:\n    {infoResult.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n    ")}");

    // ── CONTENT TYPE CALLS ────────────────────
    Console.WriteLine("\n=== Content Types ===");

    var mdResult = await client.CallToolAsync("get_markdown_report", new Dictionary<string, object?>());
    Console.WriteLine("\n[get_markdown_report]");
    PrintContentBlocks(mdResult.Content);

    var imgResult = await client.CallToolAsync("get_tiny_image", new Dictionary<string, object?>());
    Console.WriteLine("\n[get_tiny_image]");
    PrintContentBlocks(imgResult.Content);

    var embedResult = await client.CallToolAsync("get_embedded_config", new Dictionary<string, object?>());
    Console.WriteLine("\n[get_embedded_config]");
    PrintContentBlocks(embedResult.Content);

    var mixedResult = await client.CallToolAsync("get_mixed_content", new Dictionary<string, object?>());
    Console.WriteLine("\n[get_mixed_content]");
    PrintContentBlocks(mixedResult.Content);

    // ── PROGRESS + CANCELLATION + MCPSERVER CONTEXT ──────────────
    Console.WriteLine("\n=== Long Running Tools ===");

    // Register the progress notification handler BEFORE calling tools
    // that report progress. Notifications arrive asynchronously.
    // Since we are sending a call level handler we do not need this global one that
    // can be used when global progress updates are needed outside of a specific call context.
    // client.RegisterNotificationHandler(
    // NotificationMethods.ProgressNotification,
    // (notification, ct) =>
    // {
    //     if (notification.Params is not null)
    //     {
    //         var p = JsonSerializer.Deserialize<ProgressNotificationParams>(
    //             notification.Params,
    //             McpJsonUtilities.DefaultOptions);

    //         if (p is not null)
    //         {
    //             var val = p.Progress;  // ← this is the ProgressNotificationValue
    //             var pct = val.Total.HasValue && val.Total > 0
    //                 ? $"{(int)(val.Progress / val.Total.Value * 100),3}%"
    //                 : $"{val.Progress}";

    //             Console.WriteLine($"  [progress] {pct} — {val.Message}");
    //         }
    //     }
    //     return ValueTask.CompletedTask;
    // });

    // GetClientInfo — no progress needed
    Console.WriteLine("\n[get_client_info]");
    var clientInfoResult = await client.CallToolAsync(
        "get_client_info",
        new Dictionary<string, object?>()
    );
    Console.WriteLine($"  {clientInfoResult.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n  ")}");

    // RunWithProgress — pass Progress<T> directly, SDK wires the token automatically
    Console.WriteLine("\n[run_with_progress — 4 steps]");
    var progressResult = await client.CallToolAsync(
        "run_with_progress",
        new Dictionary<string, object?> { ["steps"] = 4 },
        progress: new Progress<ProgressNotificationValue>(value =>
            Console.WriteLine($"  [progress] {(int)(value.Progress / (value.Total ?? 1) * 100),3}% — {value.Message}"))
    );
    Console.WriteLine($"  → {progressResult.Content.OfType<TextContentBlock>().First().Text}");

    // ProcessItems — same pattern
    Console.WriteLine("\n[process_items — McpServer + progress + cancellation]");
    var itemsResult = await client.CallToolAsync(
        "process_items",
        new Dictionary<string, object?> { ["items"] = "alpha, beta, gamma, delta" },
        progress: new Progress<ProgressNotificationValue>(value =>
            Console.WriteLine($"  [progress] {(int)(value.Progress / (value.Total ?? 1) * 100),3}% — {value.Message}"))
    );
    Console.WriteLine($"  → {itemsResult.Content.OfType<TextContentBlock>().First().Text.Replace("\n", "\n  ")}");

    // Cancellation — progress + token combined
    Console.WriteLine("\n[run_with_progress — cancelled after 800ms]");
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
    try
    {
        var cancelledResult = await client.CallToolAsync(
            "run_with_progress",
            new Dictionary<string, object?> { ["steps"] = 10 },
            progress: new Progress<ProgressNotificationValue>(value =>
                Console.WriteLine($"  [progress] {(int)(value.Progress / (value.Total ?? 1) * 100),3}% — {value.Message}")),
            cancellationToken: cts.Token
        );
        Console.WriteLine($"  → {cancelledResult.Content.OfType<TextContentBlock>().First().Text}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("  → Cancelled by client after 800ms — OperationCanceledException caught.");
    }
}
else
{
    Console.WriteLine("\n⚠  Server does not support Tools.");
}

// ─────────────────────────────────────────────
//  RESOURCES
// ─────────────────────────────────────────────

if (client.ServerCapabilities?.Resources is not null)
{
    Console.WriteLine($"\n=== Resources ===");

    var resources = await client.ListResourcesAsync();
    Console.WriteLine($"Direct ({resources.Count}):");
    foreach (var r in resources)
        Console.WriteLine($"  - {r.Name}  {r.Uri}");

    var templates = await client.ListResourceTemplatesAsync();
    Console.WriteLine($"Templates ({templates.Count}):");
    foreach (var t in templates)
        Console.WriteLine($"  - {t.Name}  →  {t.UriTemplate}");

    Console.WriteLine("\n[reading info://server/status]");
    var statusResult = await client.ReadResourceAsync("info://server/status");
    foreach (var content in statusResult.Contents)
        if (content is TextResourceContents text)
            Console.WriteLine(text.Text);

    Console.WriteLine("\n[reading math://times-table/7]");
    var tableResult = await client.ReadResourceAsync("math://times-table/7");
    foreach (var content in tableResult.Contents)
        if (content is TextResourceContents text)
            Console.WriteLine(text.Text);
}
else
{
    Console.WriteLine("\n⚠  Server does not support Resources.");
}

// ─────────────────────────────────────────────
//  PROMPTS
// ─────────────────────────────────────────────

if (client.ServerCapabilities?.Prompts is not null)
{
    Console.WriteLine($"\n=== Prompts ===");

    var prompts = await client.ListPromptsAsync();
    foreach (var p in prompts)
    {
        Console.WriteLine($"  - {p.Name}: {p.Description}");
        if (p.ProtocolPrompt.Arguments is { Count: > 0 })
            foreach (var arg in p.ProtocolPrompt.Arguments)
                Console.WriteLine($"      arg: {arg.Name} — {arg.Description}");
    }

    Console.WriteLine("\n[getting 'introduce']");
    var intro = await client.GetPromptAsync("introduce");
    foreach (var msg in intro.Messages)
        if (msg.Content is TextContentBlock text)
            Console.WriteLine($"  [{msg.Role}]: {text.Text}");

    Console.WriteLine("\n[getting 'review_code']");
    var review = await client.GetPromptAsync(
        "review_code",
        new Dictionary<string, object?>
        {
            ["language"] = "csharp",
            ["code"] = "public static int Add(int a, int b) => a + b;"
        }
    );
    foreach (var msg in review.Messages)
        if (msg.Content is TextContentBlock text)
            Console.WriteLine($"  [{msg.Role}]: {text.Text}");
}
else
{
    Console.WriteLine("\n⚠  Server does not support Prompts.");
}

// ─────────────────────────────────────────────
//  HELPER — PrintContentBlocks
//  Canonical switch over every content block type.
//  Use this pattern in every real host you build.
// ─────────────────────────────────────────────

static void PrintContentBlocks(IEnumerable<ContentBlock> blocks)
{
    foreach (var block in blocks)
    {
        switch (block)
        {
            case TextContentBlock text:
                Console.WriteLine($"    [TEXT]");
                Console.WriteLine($"      {text.Text?.Replace("\n", "\n      ")}");
                break;

            case ImageContentBlock image:
                Console.WriteLine($"    [IMAGE]");
                Console.WriteLine($"      MIME:           {image.MimeType}");
                Console.WriteLine($"      Bytes:          {image.DecodedData.Length}");
                var preview = Convert.ToBase64String(image.DecodedData.ToArray());
                Console.WriteLine($"      Base64 preview: {preview[..20]}...");
                break;

            case AudioContentBlock audio:
                Console.WriteLine($"    [AUDIO]");
                Console.WriteLine($"      MIME:  {audio.MimeType}");
                Console.WriteLine($"      Bytes: {audio.DecodedData.Length}");
                break;

            case EmbeddedResourceBlock embedded:
                Console.WriteLine($"    [EMBEDDED RESOURCE]");
                if (embedded.Resource is TextResourceContents textRes)
                {
                    Console.WriteLine($"      URI:     {textRes.Uri}");
                    Console.WriteLine($"      MIME:    {textRes.MimeType}");
                    Console.WriteLine($"      Content: {textRes.Text?.Replace("\n", "\n               ")}");
                }
                else if (embedded.Resource is BlobResourceContents blobRes)
                {
                    Console.WriteLine($"      URI:     {blobRes.Uri}");
                    Console.WriteLine($"      MIME:    {blobRes.MimeType}");
                    Console.WriteLine($"      Bytes:   {blobRes.Blob.Length}");
                }
                break;

            default:
                Console.WriteLine($"    [UNKNOWN: {block.GetType().Name}]");
                break;
        }
    }
}