using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Core.Tests;

public class NamespacedToolNameTests
{
    [Fact]
    public void Create_joins_slug_and_tool_with_double_underscore()
    {
        var name = NamespacedToolName.Create("github", "create_issue");

        name.Value.Should().Be("github__create_issue");
    }

    [Fact]
    public void TrySplit_roundtrips_created_name()
    {
        var name = NamespacedToolName.Create("server-a", "do_thing");

        name.TrySplit(out var slug, out var tool).Should().BeTrue();
        slug.Should().Be("server-a");
        tool.Should().Be("do_thing");
    }

    [Fact]
    public void TrySplit_preserves_underscores_inside_tool_name()
    {
        var name = NamespacedToolName.Create("srv", "read__nested__thing");

        name.TrySplit(out var slug, out var tool).Should().BeTrue();
        slug.Should().Be("srv");
        tool.Should().Be("read__nested__thing");
    }

    [Theory]
    [InlineData("plain-name")]
    [InlineData("__leading")]
    [InlineData("trailing__")]
    public void TrySplit_rejects_values_without_valid_namespace(string raw)
    {
        var name = new NamespacedToolName(raw);

        name.TrySplit(out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Create_rejects_separator_in_server_slug()
    {
        var act = () => NamespacedToolName.Create("bad__slug", "tool");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_null_or_whitespace(string? raw)
    {
        var act = () => new NamespacedToolName(raw!);

        act.Should().Throw<ArgumentException>();
    }
}
