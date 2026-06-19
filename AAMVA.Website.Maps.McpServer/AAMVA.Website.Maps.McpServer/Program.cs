using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Send all logs to stderr so stdout stays clean for MCP stdio transport
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace; // keep stdout clean for MCP JSON
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Start the local HTTP server that serves wwwroot files to the interactive
// map preview. Must start before MCP tool calls arrive so render_map_preview
// can reference the port immediately.
var wwwRoot = Path.Combine(
    Environment.GetEnvironmentVariable("MAPS_REPO_ROOT")
        ?? throw new InvalidOperationException("MAPS_REPO_ROOT not set."),
    "AAMVA.Website.Maps", "AAMVA.Website.Maps", "wwwroot");

LocalServer.Initialize(wwwRoot);

await app.RunAsync();
