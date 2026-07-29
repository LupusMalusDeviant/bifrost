using AwesomeAssertions;
using McpMcp.Abstractions;
using Xunit;

namespace McpMcp.Core.Tests;

public class VersioningTests
{
    [Fact]
    public void Product_version_comes_from_the_shared_build_property()
    {
        McpMcpProductInfo.Version.Should().Be("0.6.2");
        typeof(McpMcpProductInfo).Assembly.GetName().Version!.ToString(3)
            .Should().Be(McpMcpProductInfo.Version);
    }
}
