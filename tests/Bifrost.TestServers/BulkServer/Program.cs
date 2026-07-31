using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// 100 programmatisch erzeugte Tools mit realistisch großen Schemas — Grundlage für die
// NFR-01-Messung von tools/list (100 Tools ≤ 200 ms p95).
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var tools = Enumerable.Range(0, 100).Select(i => McpServerTool.Create(
    (string target, bool dryRun) => $"tool_{i:D3} ran on {target} (dryRun={dryRun})",
    new McpServerToolCreateOptions
    {
        Name = $"tool_{i:D3}",
        Description =
            $"Performs operation {i} on the target resource, validating inputs and returning a structured result.",
    }));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools(tools);

await builder.Build().RunAsync();
