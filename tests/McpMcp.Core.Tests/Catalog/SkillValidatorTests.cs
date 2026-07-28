using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Catalog;
using McpMcp.Core.Rbac;
using McpMcp.Core.Tests.Upstreams;
using Xunit;

namespace McpMcp.Core.Tests.Catalog;

/// <summary>
/// FR-40: Die deklarierten Angaben eines Skills werden gegen die Wirklichkeit geprüft. Der Teil, den
/// nur der Gateway kann, ist die Tool-Prüfung — er kennt den Katalog, ein Datei-Editor nicht.
/// </summary>
public class SkillValidatorTests
{
    private readonly FakeAssetStore _assets = new();
    private readonly FakeSupervisor _supervisor = new();
    private readonly InMemoryRbacDirectory _directory = new();

    private SkillValidator CreateValidator()
    {
        _supervisor.SetServer("github", TestData.InventoryWithTools("create_issue"));
        var catalog = new ToolCatalog(_supervisor, new AuthorizationService(_directory), _directory);
        return new SkillValidator(_assets, catalog);
    }

    private Task<IReadOnlyList<SkillFinding>> ValidateAsync(string name, SkillMetadata metadata)
        => CreateValidator().ValidateAsync(name, metadata, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Nothing_declared_reports_nothing()
    {
        var findings = await ValidateAsync("skill", SkillMetadata.Empty);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task Reference_into_nothing_is_reported()
    {
        await _assets.CreateAsync("vorhanden", null, "Text", null, TestContext.Current.CancellationToken);

        var findings = await ValidateAsync(
            "einstieg", new SkillMetadata(References: ["vorhanden", "gibt-es-nicht"]));

        findings.Should().ContainSingle()
            .Which.Message.Should().Contain("gibt-es-nicht",
                "ein Verweis ins Leere ist genau das, was die Struktur sichtbar machen soll");
    }

    [Fact]
    public async Task Self_reference_is_reported_as_such()
    {
        var findings = await ValidateAsync("kreis", new SkillMetadata(References: ["kreis"]));

        findings.Should().ContainSingle().Which.Message.Should().Contain("sich selbst",
            "ein Selbstverweis existiert zwar, hilft dem Agenten aber nicht weiter");
    }

    [Fact]
    public async Task Required_tool_is_checked_against_the_catalog()
    {
        var findings = await ValidateAsync(
            "skill", new SkillMetadata(RequiredTools: ["github__create_issue", "github__gibt_es_nicht"]));

        findings.Should().ContainSingle().Which.Field.Should().Be(nameof(SkillMetadata.RequiredTools));
        findings[0].Message.Should().Contain("github__gibt_es_nicht");
    }

    /// <summary>
    /// Die eigentliche Entwurfsentscheidung: Befunde sind Warnungen. Wer A schreibt und B danach
    /// anlegt, darf nicht blockiert werden — sonst lässt er das Feld leer, und ein leeres Feld
    /// prüft nichts. Deshalb wirft der Prüfer nicht, sondern berichtet.
    /// </summary>
    [Fact]
    public async Task Findings_are_reported_not_thrown()
    {
        var act = async () => await ValidateAsync(
            "skill", new SkillMetadata("später", ["nichts"], ["auch__nichts"]));

        var findings = await act.Should().NotThrowAsync();
        findings.Subject.Should().HaveCount(2);
    }
}
