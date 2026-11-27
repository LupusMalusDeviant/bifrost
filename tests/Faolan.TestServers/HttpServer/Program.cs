using System.ComponentModel;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<HttpEchoTools>();

var app = builder.Build();
app.MapMcp();
app.Run();

[McpServerToolType]
internal sealed class HttpEchoTools
{
    [McpServerTool(Name = "echo")]
    [Description("Echoes the message back to the client.")]
    public static string Echo([Description("The message to echo.")] string message) => $"Echo: {message}";
}
