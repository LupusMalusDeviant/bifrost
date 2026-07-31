using AwesomeAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Integration.Tests;

/// <summary>
/// WP0-DoD-Nachweis: Der EchoServer beantwortet initialize, tools/list und einen Echo-Call
/// über einen echten stdio-Prozess mit dem offiziellen SDK-Client.
/// </summary>
public class EchoServerSmokeTests
{
    private static StdioClientTransport CreateTransport() => new(new StdioClientTransportOptions
    {
        Name = "echo-server",
        Command = TestPaths.EchoServerExecutable,
    });

    [Fact]
    public async Task Initialize_and_list_tools_exposes_echo()
    {
        await using var client = await McpClient.CreateAsync(CreateTransport());

        var tools = await client.ListToolsAsync();

        tools.Should().ContainSingle(t => t.Name == "echo");
    }

    [Fact]
    public async Task Echo_call_roundtrips_message()
    {
        await using var client = await McpClient.CreateAsync(CreateTransport());

        var result = await client.CallToolAsync(
            "echo",
            new Dictionary<string, object?> { ["message"] = "Hallo B.I.F.R.O.S.T" });

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Should().ContainSingle()
            .Which.Text.Should().Be("Echo: Hallo B.I.F.R.O.S.T");
    }
}
