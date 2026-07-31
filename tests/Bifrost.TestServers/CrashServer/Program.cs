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
    .WithTools<CrashTools>();

await builder.Build().RunAsync();

[McpServerToolType]
internal sealed class CrashTools
{
    [McpServerTool(Name = "echo")]
    [Description("Echoes the message back to the client.")]
    public static string Echo([Description("The message to echo.")] string message) => $"Echo: {message}";

    [McpServerTool(Name = "crash")]
    [Description("Terminates the server process immediately without sending a response.")]
    public static string Crash()
    {
        Environment.Exit(1);
        return "unreachable";
    }
}
