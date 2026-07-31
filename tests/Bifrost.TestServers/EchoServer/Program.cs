using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// stdout gehört dem MCP-Protokoll — Logs strikt auf stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<EchoTools>();

await builder.Build().RunAsync();

[McpServerToolType]
internal sealed class EchoTools
{
    [McpServerTool(Name = "echo")]
    [Description("Echoes the message back to the client.")]
    public static string Echo([Description("The message to echo.")] string message) => $"Echo: {message}";
}
