using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

Console.WriteLine("Starting up Host...\n");

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "filesystem-server",
    Command = "npx",
    Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]
});

var client = await McpClient.CreateAsync(transport);
Console.WriteLine("Connected to server!\n");

// inspect what this server actually declared it supports
Console.WriteLine("=== Server Capabilities ===");
Console.WriteLine($"  Tools:     {client.ServerCapabilities?.Tools is not null}");
Console.WriteLine($"  Resources: {client.ServerCapabilities?.Resources is not null}");
Console.WriteLine($"  Prompts:   {client.ServerCapabilities?.Prompts is not null}");
Console.WriteLine();

// --- SPRINT 2: call a tool ---

// list the directory
var listResult = await client.CallToolAsync(
    "list_directory",
    new Dictionary<string, object?> { ["path"] = "/private/tmp" }
);

Console.WriteLine("=== Directory ===");
foreach (var content in listResult.Content)
    if (content is TextContentBlock text)
        Console.WriteLine(text.Text);

// read a specific file
var readResult = await client.CallToolAsync(
    "read_file",
    new Dictionary<string, object?> { ["path"] = "/private/tmp/mcp-test.txt" }
);

Console.WriteLine("\n=== File Contents ===");
foreach (var content in readResult.Content)
    if (content is TextContentBlock text)
        Console.WriteLine(text.Text);

var errorResult = await client.CallToolAsync(
    "read_file",
    new Dictionary<string, object?> { ["path"] = "/private/tmp/does-not-exist.txt" }
);

if (errorResult.IsError is true)
{
    var errorText = errorResult.Content
        .OfType<TextContentBlock>()
        .FirstOrDefault()?.Text;

    Console.WriteLine($"Tool returned an error: {errorText}");
}
else
{
    foreach (var content in errorResult.Content)
        if (content is TextContentBlock text)
            Console.WriteLine(text.Text);
}

// --- SPRINT 3: resources ---
// These functions are not implemented in the filesystem, we will therefore move to the 
// everything server
// Step 1: list direct resources
// var resources = await client.ListResourcesAsync();
// Console.WriteLine($"=== Direct Resources ({resources.Count}) ===");
// foreach (var r in resources)
//     Console.WriteLine($"  {r.Name}  [{r.MimeType}]  {r.Uri}");

// // Step 2: list resource templates (parameterized URIs)
// var templates = await client.ListResourceTemplatesAsync();
// Console.WriteLine($"\n=== Resource Templates ({templates.Count}) ===");
// foreach (var t in templates)
//     Console.WriteLine($"  {t.Name}  →  {t.UriTemplate}");

await client.DisposeAsync();