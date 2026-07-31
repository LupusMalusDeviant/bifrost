using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SlowTools>();

await builder.Build().RunAsync();

[McpServerToolType]
internal sealed class SlowTools
{
    [McpServerTool(Name = "sleep")]
    [Description("Waits the given number of milliseconds, then returns.")]
    public static async Task<string> Sleep(
        [Description("Delay in milliseconds.")] int milliseconds,
        CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken);
        return $"Slept {milliseconds} ms";
    }
}
