using AwesomeAssertions;
using Bifrost.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Meta-Tools über einen <b>echten MCP-Client</b>, nicht über den Service dahinter.
/// <para>
/// Genau diese Lücke hat <c>read_skill</c> auf der ersten betriebenen Instanz zerlegt: Jeder Aufruf
/// endete in einer <c>JsonException</c>, während sämtliche Tests grün blieben — sie riefen
/// <c>MetaToolService.ExecuteAsync</c> direkt auf und sahen die Protokollschicht nie.
/// </para>
/// <para>
/// Die Ursache war eine Heuristik an der Stelle eines Wissens: „Nutzinhalt hat ein Feld
/// <c>content</c>" galt als Beweis dafür, dass ein Ergebnis von einem Upstream durchgereicht wurde.
/// <c>read_skill</c> führt legitim ein <c>content</c> — den Skill-Text als Zeichenkette —, das
/// Protokoll erwartet dort aber eine Liste von ContentBlocks.
/// </para>
/// </summary>
public sealed class MetaToolOverMcpTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public MetaToolOverMcpTests(GatewayFixture gw) => _gw = gw;

    private IAssetStore Assets => _gw.Services.GetRequiredService<IAssetStore>();

    /// <summary>
    /// Der Fall, der im Betrieb umfiel. <c>read_skill</c> liefert den Text in einem Feld namens
    /// <c>content</c> — das darf die Protokollschicht nicht mit ihrem eigenen <c>content</c>
    /// verwechseln.
    /// </summary>
    [Fact]
    public async Task Read_skill_survives_the_protocol_layer_although_its_payload_has_a_content_field()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"mcp-read-{Guid.NewGuid():N}";
        await Assets.CreateAsync(name, "Über MCP gelesen", "## Ablauf\nZuerst suchen.", null, ct);
        var (_, apiKey) = await _gw.SeedAdminAsync($"mcpread-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(apiKey);
        var result = await client.CallToolAsync(
            "read_skill", new Dictionary<string, object?> { ["name"] = name }, cancellationToken: ct);

        result.IsError.Should().NotBe(true, "der Aufruf endete im Betrieb in einer JsonException");
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("Zuerst suchen.").And.Contain(name);
    }

    [Fact]
    public async Task List_skills_comes_through_as_well()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"mcp-list-{Guid.NewGuid():N}";
        await Assets.CreateAsync(name, "Sichtbar", "Text", null, ct);
        var (_, apiKey) = await _gw.SeedAdminAsync($"mcplist-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(apiKey);
        var result = await client.CallToolAsync("list_skills", cancellationToken: ct);

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain(name);
    }

    /// <summary>
    /// Die anderen Meta-Tools über dieselbe Strecke — sie hatten den Fehler nie, aber ohne sie hier
    /// bliebe die Protokollschicht für den halben Meta-Tool-Satz ungeprüft.
    /// </summary>
    [Theory]
    [InlineData("search_tools")]
    [InlineData("describe_tool")]
    public async Task The_other_meta_tools_answer_over_the_protocol_too(string tool)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await _gw.SeedAdminAsync($"mcpmeta-{Guid.NewGuid():N}");

        await using var client = await _gw.ConnectClientAsync(apiKey);
        var arguments = tool == "search_tools"
            ? new Dictionary<string, object?> { ["query"] = "irgendwas" }
            : new Dictionary<string, object?> { ["name"] = "gibt__esnicht" };

        var result = await client.CallToolAsync(tool, arguments, cancellationToken: ct);

        // describe_tool auf ein unbekanntes Tool IST ein Fehler — aber ein sauberer, kein Absturz.
        result.Content.OfType<TextContentBlock>().Should().NotBeEmpty();
    }
}
