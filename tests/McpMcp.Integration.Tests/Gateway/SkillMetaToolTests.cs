using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Skills als <b>Tools</b>, nicht nur als MCP-Prompts.
/// <para>
/// Der Grund: Ein Prompt ist in den meisten Clients nutzerinitiiert — der Mensch sieht die Liste,
/// das Modell nicht. Ein Tool ruft das Modell selbst auf. Erst damit kann ein Agent von sich aus
/// nachsehen, ob es für seine Aufgabe eine hinterlegte Anleitung gibt. Die Prompt- und
/// Resource-Auslieferung bleibt daneben bestehen; sie bedient den Menschen.
/// </para>
/// </summary>
public sealed class SkillMetaToolTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public SkillMetaToolTests(GatewayFixture gw) => _gw = gw;

    private MetaToolService MetaTools => _gw.Services.GetRequiredService<MetaToolService>();

    private static JsonElement Args(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>
    /// Der Kern des Token-Versprechens: Die Liste liefert Namen und Kurzbeschreibung, <b>nicht</b>
    /// den Text. Sonst kostete ein Blick in den Skill-Bestand mehr als der gepinnte Katalog.
    /// </summary>
    [Fact]
    public async Task List_skills_returns_names_without_content()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"deploy-{Guid.NewGuid():N}";
        const string Content = "## Ablauf\nErst Status prüfen, dann ausrollen.";
        await _gw.Services.GetRequiredService<IAssetStore>()
            .CreateAsync(name, "Wie hier ausgerollt wird", Content, metadata: null, ct);
        var (admin, _) = await _gw.SeedAdminAsync($"skill-{Guid.NewGuid():N}");

        var result = await MetaTools.ExecuteAsync(
            admin, CallOrigin.Mcp, MetaToolService.ListSkillsName, Args("{}"), ct);

        result.Status.Should().Be(InvocationStatus.Success);
        var raw = result.Content!.Value.GetRawText();
        raw.Should().Contain(name).And.Contain("Wie hier ausgerollt wird");
        raw.Should().NotContain("Erst Status prüfen",
            "die Liste traegt keinen Inhalt — dafuer ist read_skill da");
    }

    [Fact]
    public async Task Read_skill_returns_the_text()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"konvention-{Guid.NewGuid():N}";
        await _gw.Services.GetRequiredService<IAssetStore>()
            .CreateAsync(name, "Konventionen", "Immer erst suchen.", metadata: null, ct);
        var (admin, _) = await _gw.SeedAdminAsync($"skill-read-{Guid.NewGuid():N}");

        var result = await MetaTools.ExecuteAsync(
            admin, CallOrigin.Mcp, MetaToolService.ReadSkillName,
            Args($$"""{"name":"{{name}}"}"""), ct);

        result.Status.Should().Be(InvocationStatus.Success);
        result.Content!.Value.GetProperty("content").GetString().Should().Be("Immer erst suchen.");
    }

    [Fact]
    public async Task An_unknown_skill_is_reported_as_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, _) = await _gw.SeedAdminAsync($"skill-404-{Guid.NewGuid():N}");

        var result = await MetaTools.ExecuteAsync(
            admin, CallOrigin.Mcp, MetaToolService.ReadSkillName,
            Args("""{"name":"gibtesnicht"}"""), ct);

        result.Status.Should().Be(InvocationStatus.ToolNotFound);
    }

    /// <summary>
    /// Die Erkennung leitet sich aus den Definitionen ab. Stünde sie als zweite Namensliste da,
    /// erschiene ein neues Meta-Tool im Katalog und wäre trotzdem nicht aufrufbar.
    /// </summary>
    [Fact]
    public void Every_defined_meta_tool_is_recognised_as_one()
        => MetaToolService.Definitions.Should()
            .OnlyContain(d => MetaToolService.IsMetaTool(d.Name))
            .And.HaveCount(5);

    /// <summary>
    /// Ein Aufruf eines Skill-Tools steht im Audit — wie jeder andere. Ohne das wäre der
    /// Skill-Bestand der einzige Teil des Gateways, dessen Nutzung niemand sieht.
    /// </summary>
    [Fact]
    public async Task Skill_calls_are_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var (caller, _) = await _gw.SeedAdminAsync($"skill-audit-{Guid.NewGuid():N}");

        await MetaTools.ExecuteAsync(
            caller, CallOrigin.Mcp, MetaToolService.ListSkillsName, Args("{}"), ct);

        await IntegrationSupport.WaitUntilAsync(
            () => _gw.AuditQuery.QueryAsync(new AuditFilter(Caller: caller), ct)
                .GetAwaiter().GetResult().Items.Any(e => e.Tool == MetaToolService.ListSkillsName),
            because: "auch der Blick in den Skill-Bestand gehoert ins Audit");
    }
}
