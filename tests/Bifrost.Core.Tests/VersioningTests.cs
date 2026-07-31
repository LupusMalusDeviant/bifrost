using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Core.Tests;

public class VersioningTests
{
    [Fact]
    public void Product_version_comes_from_the_shared_build_property()
    {
        BifrostProductInfo.Version.Should().Be("0.11.0");
        typeof(BifrostProductInfo).Assembly.GetName().Version!.ToString(3)
            .Should().Be(BifrostProductInfo.Version);
    }
}
