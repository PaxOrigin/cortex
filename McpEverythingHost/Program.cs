using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// ─────────────────────────────────────────────
//  HOST + CLIENT SETUP
// ─────────────────────────────────────────────

Console.WriteLine("Starting up Host...\n");

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "everything-server",
    Command = "npx",
    Arguments = ["-y", "@modelcontextprotocol/server-everything"]
});

await using var client = await McpClient.CreateAsync(transport);
Console.WriteLine("Connected!\n");

// ─────────────────────────────────────────────
//  CAPABILITY CHECK — always do this first
//  The server declares what it supports during
//  the handshake. Never assume.
// ─────────────────────────────────────────────

Console.WriteLine("=== Server Capabilities ===");
Console.WriteLine($"  Tools:     {client.ServerCapabilities?.Tools is not null}");
Console.WriteLine($"  Resources: {client.ServerCapabilities?.Resources is not null}");
Console.WriteLine($"  Prompts:   {client.ServerCapabilities?.Prompts is not null}");
Console.WriteLine();

// ─────────────────────────────────────────────
//  SPRINT 2 — TOOLS
//  Discover all tools, call one, handle result,
//  handle error gracefully.
// ─────────────────────────────────────────────

if (client.ServerCapabilities?.Tools is not null)
{
    Console.WriteLine("══════════════════════════════════════");
    Console.WriteLine("  TOOLS");
    Console.WriteLine("══════════════════════════════════════\n");

    // List
    var tools = await client.ListToolsAsync();
    Console.WriteLine($"Available tools ({tools.Count}):");
    foreach (var t in tools)
        Console.WriteLine($"  • {t.Name} — {t.Description}");

    // Call: echo (always present on server-everything)
    Console.WriteLine("\n[calling 'echo']");
    var echoResult = await client.CallToolAsync(
        "echo",
        new Dictionary<string, object?> { ["message"] = "Hello from Sprint 2!" }
    );

    foreach (var content in echoResult.Content)
        if (content is TextContentBlock text)
            Console.WriteLine($"  → {text.Text}");

    // Call: intentional error — see IsError in action
    Console.WriteLine("\n[calling a tool with bad args to demonstrate IsError]");
    var badResult = await client.CallToolAsync(
        "echo",
        new Dictionary<string, object?> { }   // missing required arg
    );

    if (badResult.IsError is true)
    {
        var errorText = badResult.Content
            .OfType<TextContentBlock>()
            .FirstOrDefault()?.Text;
        Console.WriteLine($"  ✗ Tool error (expected): {errorText}");
    }
    else
    {
        foreach (var content in badResult.Content)
            if (content is TextContentBlock text)
                Console.WriteLine($"  → {text.Text}");
    }
}
else
{
    Console.WriteLine("⚠  Server does not support Tools — skipping.\n");
}

// ─────────────────────────────────────────────
//  SPRINT 3 — RESOURCES
//  Discover direct resources + URI templates,
//  read one, handle both text and binary.
// ─────────────────────────────────────────────

Console.WriteLine();

if (client.ServerCapabilities?.Resources is not null)
{
    Console.WriteLine("══════════════════════════════════════");
    Console.WriteLine("  RESOURCES");
    Console.WriteLine("══════════════════════════════════════\n");

    // List direct resources
    var resources = await client.ListResourcesAsync();
    Console.WriteLine($"Direct resources ({resources.Count}):");
    foreach (var r in resources)
        Console.WriteLine($"  • {r.Name}  [{r.MimeType}]  {r.Uri}");

    // List URI templates (parameterized resources)
    var templates = await client.ListResourceTemplatesAsync();
    Console.WriteLine($"\nResource templates ({templates.Count}):");
    foreach (var t in templates)
        Console.WriteLine($"  • {t.Name}  →  {t.UriTemplate}");

    // Read the first direct resource if any exist
    if (resources.Count > 0)
    {
        var first = resources[0];
        Console.WriteLine($"\n[reading resource '{first.Name}' at {first.Uri}]");

        var result = await client.ReadResourceAsync(first.Uri);

        foreach (var content in result.Contents)
        {
            if (content is TextResourceContents text)
            {
                Console.WriteLine($"  URI:      {text.Uri}");
                Console.WriteLine($"  MimeType: {text.MimeType}");
                // trim long content for readability
                var preview = text.Text?.Length > 200
                    ? text.Text[..200] + "..."
                    : text.Text;
                Console.WriteLine($"  Content:  {preview}");
            }
            else if (content is BlobResourceContents blob)
            {
                Console.WriteLine($"  URI:      {blob.Uri}");
                Console.WriteLine($"  MimeType: {blob.MimeType}");
                Console.WriteLine($"  Size:     {blob.Blob.Length} bytes (binary)");
            }
        }
    }
    else
    {
        Console.WriteLine("\n  (no direct resources to read)");
    }
}
else
{
    Console.WriteLine("⚠  Server does not support Resources — skipping.\n");
}

// ─────────────────────────────────────────────
//  SPRINT 4 — PROMPTS
//  Discover prompts, inspect their arguments,
//  get one with parameters and print the
//  returned messages.
// ─────────────────────────────────────────────

Console.WriteLine();

if (client.ServerCapabilities?.Prompts is not null)
{
    Console.WriteLine("══════════════════════════════════════");
    Console.WriteLine("  PROMPTS");
    Console.WriteLine("══════════════════════════════════════\n");

    // List
    var prompts = await client.ListPromptsAsync();
    Console.WriteLine($"Available prompts ({prompts.Count}):");
    foreach (var p in prompts)
    {
        Console.WriteLine($"  • {p.Name} — {p.Description}");

        if (p.ProtocolPrompt.Arguments is { Count: > 0 })
        {
            foreach (var arg in p.ProtocolPrompt.Arguments)
            {
                var required = arg.Required == true ? " (required)" : " (optional)";
                Console.WriteLine($"      arg: {arg.Name}{required} — {arg.Description}");
            }
        }
    }

    // Get the first prompt — pass empty args if it takes none
    if (prompts.Count > 0)
    {
        var first = prompts[0];
        Console.WriteLine($"\n[getting prompt '{first.Name}']");

        // Build args: required args get a placeholder value so the call succeeds
        var promptArgs = new Dictionary<string, object?>();
        if (first.ProtocolPrompt.Arguments is { Count: > 0 })
        {
            foreach (var arg in first.ProtocolPrompt.Arguments)
                if (arg.Required == true)
                    promptArgs[arg.Name] = "example";
        }

        var result = await client.GetPromptAsync(first.Name, promptArgs);

        Console.WriteLine($"  Returned {result.Messages.Count} message(s):\n");

        foreach (var message in result.Messages)
        {
            Console.WriteLine($"  [{message.Role.ToString().ToUpper()}]");

            switch (message.Content)
            {
                case TextContentBlock text:
                    var preview = text.Text?.Length > 300
                        ? text.Text[..300] + "..."
                        : text.Text;
                    Console.WriteLine($"    {preview}");
                    break;

                case ImageContentBlock image:
                    Console.WriteLine($"    [image/{image.MimeType} — {image.DecodedData.Length} bytes]");
                    break;

                case EmbeddedResourceBlock resource:
                    Console.WriteLine($"    [embedded resource: {resource.Resource.Uri}]");
                    break;
            }

            Console.WriteLine();
        }
    }
}
else
{
    Console.WriteLine("⚠  Server does not support Prompts — skipping.\n");
}

// ─────────────────────────────────────────────
//  DONE
// ─────────────────────────────────────────────

Console.WriteLine("══════════════════════════════════════");
Console.WriteLine("  Sprints 1–4 complete. Client shutting down.");
Console.WriteLine("══════════════════════════════════════");