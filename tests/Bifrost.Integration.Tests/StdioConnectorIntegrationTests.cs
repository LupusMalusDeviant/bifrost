using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream;
using Xunit;

namespace Bifrost.Integration.Tests;

public class StdioConnectorIntegrationTests
{
    [Fact]
    public async Task Connector_discovers_inventory_and_calls_tool()
    {
        var connector = new StdioUpstreamConnector();
        var id = ServerId.New();

        await using var connection = await connector.ConnectAsync(
            id, IntegrationSupport.StdioServer("echo", "EchoServer"), TestContext.Current.CancellationToken);

        connection.Id.Should().Be(id);

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);
        inventory.Tools.Should().ContainSingle(t => t.Name == "echo");
        inventory.Tools[0].InputSchema.ValueKind.Should().Be(JsonValueKind.Object, "Schema wird mitgeliefert");

        var args = JsonSerializer.SerializeToElement(new { message = "Hallo Konnektor" });
        var result = await connection.CallToolAsync("echo", args, TestContext.Current.CancellationToken);

        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Echo: Hallo Konnektor");

        await connection.PingAsync(TestContext.Current.CancellationToken);
    }
}
