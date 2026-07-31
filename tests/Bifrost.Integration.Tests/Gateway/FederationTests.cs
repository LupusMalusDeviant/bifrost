using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// WP7.4-DoD (FR-05): Federation — der Gateway kann einen weiteren MCP-Server (stellvertretend
/// für einen zweiten Gateway) als Upstream einbinden — und Loop-Detection: der eigene MCP-Endpoint
/// weist Aufrufe ab, die die eigene Instanz-Kennung tragen.
/// </summary>
public sealed class FederationTests : IClassFixture<GatewayFixture>, IAsyncLifetime
{
    private static readonly ConcurrentBag<string> ReceivedInstanceHeaders = [];

    private readonly GatewayFixture _gw;
    private WebApplication? _peer;
    private int _port;

    public FederationTests(GatewayFixture gw) => _gw = gw;

    public async ValueTask InitializeAsync()
    {
        _port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<PeerTools>();
        _peer = builder.Build();
        _peer.Urls.Add($"http://127.0.0.1:{_port}");
        _peer.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue(GatewayIdentity.InstanceHeader, out var h))
            {
                ReceivedInstanceHeaders.Add(h.ToString());
            }

            await next(ctx);
        });
        _peer.MapMcp("/mcp");
        await _peer.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_peer is not null)
        {
            await _peer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Second_gateway_can_be_federated_as_upstream_and_instance_header_is_sent()
    {
        var id = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                "peer", "Föderierter Peer", UpstreamTransportKind.StreamableHttp, Enabled: true,
                Http: new HttpTransportOptions(new Uri($"http://127.0.0.1:{_port}/mcp"))),
            TestContext.Current.CancellationToken);

        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "der föderierte Upstream muss verbinden (FR-05)");

        _gw.Supervisor.GetInventory(id)!.Tools.Should().Contain(t => t.Name == "peer_echo",
            "die Tools des Peers erscheinen im Katalog");

        var gatewayInstance = _gw.Services.GetRequiredService<GatewayIdentity>();
        ReceivedInstanceHeaders.Should().Contain(gatewayInstance.InstanceId,
            "der Connector schickt die eigene Instanz-Kennung mit (Loop-Detection-Grundlage)");

        await _gw.Supervisor.RemoveAsync(id, DrainPolicy.Immediate, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Own_instance_header_on_mcp_endpoint_is_rejected_as_loop()
    {
        var gatewayInstance = _gw.Services.GetRequiredService<GatewayIdentity>();
        using var client = _gw.CreateDefaultClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(GatewayIdentity.InstanceHeader, gatewayInstance.InstanceId);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.LoopDetected,
            "der eigene Instanz-Header bedeutet einen Federations-Loop (FR-05)");
    }

    [Fact]
    public async Task Foreign_instance_header_is_not_treated_as_loop()
    {
        // Ein anderer Gateway (andere Instanz-Kennung) ist legitime Federation, kein Loop.
        using var client = _gw.CreateDefaultClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(GatewayIdentity.InstanceHeader, Guid.NewGuid().ToString("N"));
        request.Headers.Add("Authorization", "Bearer mcpk_ungueltig");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "fremde Instanz → kein Loop, sondern normale (hier fehlgeschlagene) AuthN");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [McpServerToolType]
    private sealed class PeerTools
    {
        [McpServerTool(Name = "peer_echo")]
        [Description("Echoes from the federated peer gateway.")]
        public static string Echo([Description("Message")] string message) => $"Peer: {message}";
    }
}
