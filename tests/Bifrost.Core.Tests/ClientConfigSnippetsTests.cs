using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Core;
using Xunit;

namespace Bifrost.Core.Tests;

/// <summary>
/// FR-41: Die Snippets sollen ohne Nacharbeit funktionieren. Was hier schiefgeht, kommt beim
/// Nutzer als „Gateway antwortet nicht" an — mit dem Key als erstem Verdächtigen.
/// </summary>
public class ClientConfigSnippetsTests
{
    private static readonly Uri Base = new("https://gateway.example.com");

    [Fact]
    public void Cli_snippet_points_at_the_mcp_endpoint_with_bearer_header()
    {
        var cli = ClientConfigSnippets.Build(Base, "ci-agent", "mcpk_abc_def").Single(s => s.Language == "bash");

        cli.Content.Should().Contain("https://gateway.example.com/mcp")
            .And.Contain("Authorization: Bearer mcpk_abc_def")
            .And.Contain("--transport http");
    }

    [Fact]
    public void Json_snippet_is_valid_json_with_the_expected_shape()
    {
        var json = ClientConfigSnippets.Build(Base, "ci-agent", "mcpk_abc_def").Single(s => s.Language == "json");

        var parsed = JsonDocument.Parse(json.Content);
        var server = parsed.RootElement.GetProperty("mcpServers").GetProperty("ci-agent");
        server.GetProperty("url").GetString().Should().Be("https://gateway.example.com/mcp");
        server.GetProperty("headers").GetProperty("Authorization").GetString()
            .Should().Be("Bearer mcpk_abc_def");
    }

    [Theory]
    [InlineData("CI Agent", "ci-agent")]
    [InlineData("  Build/Deploy  ", "build-deploy")]
    [InlineData("Ümläute!", "ml-ute")]
    [InlineData("---", "bifrost")]
    public void Names_are_sanitized_because_clients_use_them_as_keys(string input, string expected)
    {
        // Ein Leerzeichen im Schlüssel führt je nach Client zu einem stillen Fehler statt zu
        // einer Fehlermeldung — deshalb wird hier normalisiert statt durchgereicht.
        var json = ClientConfigSnippets.Build(Base, input, "k_1234").Single(s => s.Language == "json");

        JsonDocument.Parse(json.Content).RootElement.GetProperty("mcpServers")
            .EnumerateObject().Single().Name.Should().Be(expected);
    }

    [Fact]
    public void Base_address_with_trailing_path_still_resolves_to_the_mcp_endpoint()
    {
        // Nav.BaseUri endet in Blazor auf "/" und kann bei Pfad-Hosting tiefer liegen.
        var snippets = ClientConfigSnippets.Build(new Uri("https://host/ui/"), "a", "k_1234");

        snippets.Should().OnlyContain(s => s.Content.Contains("https://host/mcp"));
    }

    [Fact]
    public void Empty_key_is_rejected_rather_than_producing_a_broken_snippet()
    {
        var act = () => ClientConfigSnippets.Build(Base, "a", "  ");

        act.Should().Throw<ArgumentException>();
    }
}
