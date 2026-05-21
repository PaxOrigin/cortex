// Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File("logs/mcpserver-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = Host.CreateEmptyApplicationBuilder(settings: null);
builder.Services.AddSerilog();
builder.Services.AddSingleton<INotebookRepository>(
    new NotebookJsonRepository("notebook.json"));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();