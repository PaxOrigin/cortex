using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

public static class CortexMcpClient
{
    public static async Task<(McpClient Client, IList<McpClientTool> Tools)> CreateAsync(
        ILogger? logger = null)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "CortexServer",
            Command = "dotnet",
            Arguments = ["run", "--project", "/Users/pax/Mark/Books/Ai_Agents_With_MCP/Cortex.McpServer/Cortex.McpServer.csproj"]
        });

        var client = await McpClient.CreateAsync(transport);
        IList<McpClientTool> tools = await client.ListToolsAsync();

        logger?.LogInformation("MCP server connected. Tools: {Count}", tools.Count);
        foreach (var tool in tools)
            logger?.LogInformation("  Tool: {Name} — {Description}", tool.Name, tool.Description);

        return (client, tools);
    }
}