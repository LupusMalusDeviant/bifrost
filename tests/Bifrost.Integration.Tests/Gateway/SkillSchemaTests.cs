using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Das Skill-Schema und seine Prüfung.
/// <para>
/// Der Grund für die Struktur steht in <c>SkillValidator</c>: Nur was deklariert ist, lässt sich
/// prüfen. Ein Verweis in der Prosa hängt still ins Leere, sobald jemand umbenennt — und die
/// vorausgesetzten Tools kann <b>nur der Gateway</b> prüfen, weil nur er den Katalog kennt.
/// </para>
/// </summary>
public sealed class SkillSchemaTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public SkillSchemaTests(GatewayFixture gw) => _gw = gw;

    private IAssetStore Assets => _gw.Services.GetRequiredService<IAssetStore>();

    private ISkillValidator Validator => _gw.Services.GetRequiredService<ISkillValidator>();

    private MetaToolService MetaTools => _gw.Services.GetRequiredService<MetaToolService>();

    [Fact]
    public async Task Metadata_survives_a_roundtrip_and_belongs_to_the_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"mapper-{Guid.NewGuid():N}";
        var first = new SkillMetadata("Wenn ein Repo dokumentiert wird", ["a", "b"], ["srv__tool"]);

        var id = await Assets.CreateAsync(name, "Bauplan", "v1-Text", first, ct);
        var readBack = (await Assets.GetAsync(id, null, ct)).MetadataOrEmpty;
        readBack.WhenToUse.Should().Be("Wenn ein Repo dokumentiert wird");
        readBack.ReferencesOrEmpty.Should().Equal("a", "b");
        readBack.RequiredToolsOrEmpty.Should().Equal("srv__tool");

        // Metadaten gehören zur Version: Wer die Referenzen ändert, ändert den Skill.
        await Assets.PublishAsync(id, "v2-Text", new SkillMetadata("Anders", ["c"], null), ct);

        var versions = await Assets.GetVersionsAsync(id, ct);
        versions.Should().HaveCount(2);
        versions[0].Version.Value.Should().Be(2, "neueste zuerst");
        versions[0].MetadataOrEmpty.ReferencesOrEmpty.Should().Equal("c");
        // Die alte Fassung behält ihre Angaben — sonst wäre die Historie unvollständig.
        versions[1].MetadataOrEmpty.ReferencesOrEmpty.Should().Equal(["a", "b"]);
    }

    /// <summary>
    /// Der Kernnutzen: Ein Verweis auf einen Skill, den es nicht gibt, fällt auf. Vorher hing so
    /// etwas still ins Leere, sobald jemand umbenannte.
    /// </summary>
    [Fact]
    public async Task A_reference_into_nothing_is_reported()
    {
        var ct = TestContext.Current.CancellationToken;

        var findings = await Validator.ValidateAsync(
            "irgendwas", new SkillMetadata(References: ["gibt-es-nicht"]), ct);

        findings.Should().ContainSingle().Which.Message.Should().Contain("existiert nicht");
    }

    [Fact]
    public async Task A_reference_to_an_existing_skill_is_fine()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = $"ziel-{Guid.NewGuid():N}";
        await Assets.CreateAsync(target, null, "da", metadata: null, ct);

        var findings = await Validator.ValidateAsync(
            "quelle", new SkillMetadata(References: [target]), ct);

        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_self_reference_is_reported()
    {
        var ct = TestContext.Current.CancellationToken;

        var findings = await Validator.ValidateAsync(
            "ich", new SkillMetadata(References: ["ich"]), ct);

        findings.Should().ContainSingle().Which.Message.Should().Contain("sich selbst");
    }

    /// <summary>
    /// Die Prüfung, die kein Datei-Editor leisten kann: Steht das vorausgesetzte Tool im Katalog?
    /// </summary>
    [Fact]
    public async Task A_required_tool_that_is_not_in_the_catalogue_is_reported()
    {
        var ct = TestContext.Current.CancellationToken;

        var findings = await Validator.ValidateAsync(
            "skill", new SkillMetadata(RequiredTools: ["nichtda__tool"]), ct);

        findings.Should().ContainSingle().Which.Message.Should().Contain("nicht im Katalog");
    }

    /// <summary>
    /// Befunde sind Warnungen: Wer A schreibt und B danach anlegt, darf nicht blockiert werden —
    /// sonst lässt er das Feld leer, und ein leeres Feld prüft nichts.
    /// </summary>
    [Fact]
    public async Task A_skill_with_findings_can_still_be_saved()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"trotzdem-{Guid.NewGuid():N}";

        var id = await Assets.CreateAsync(
            name, null, "Text", new SkillMetadata(References: ["kommt-noch"]), ct);

        (await Assets.GetAsync(id, null, ct)).MetadataOrEmpty.ReferencesOrEmpty
            .Should().Equal("kommt-noch");
    }

    /// <summary>
    /// <c>whenToUse</c> entscheidet, ob ein Agent zugreift — deshalb steht es in der Liste und
    /// nicht erst im Text, den man dafür schon geladen haben müsste. Die Referenzen dagegen
    /// erscheinen erst beim Lesen.
    /// </summary>
    [Fact]
    public async Task List_shows_when_to_use_read_shows_the_references()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"felder-{Guid.NewGuid():N}";
        await Assets.CreateAsync(
            name, "Beschreibung", "Der Text",
            new SkillMetadata("Beim Ausrollen", ["anderer-skill"], ["srv__tool"]), ct);
        var (admin, _) = await _gw.SeedAdminAsync($"schema-{Guid.NewGuid():N}");

        var list = await MetaTools.ExecuteAsync(
            admin, CallOrigin.Mcp, MetaToolService.ListSkillsName,
            JsonSerializer.Deserialize<JsonElement>($$"""{"query":"{{name}}"}"""), ct);
        var listed = list.Content!.Value.GetRawText();
        listed.Should().Contain("Beim Ausrollen");
        listed.Should().NotContain("anderer-skill", "Referenzen kosten Kontext und kommen beim Lesen");

        var read = await MetaTools.ExecuteAsync(
            admin, CallOrigin.Mcp, MetaToolService.ReadSkillName,
            JsonSerializer.Deserialize<JsonElement>($$"""{"name":"{{name}}"}"""), ct);
        var payload = read.Content!.Value;
        payload.GetProperty("references").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("anderer-skill");
        payload.GetProperty("requiredTools").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("srv__tool");
        payload.GetProperty("content").GetString().Should().Be("Der Text",
            "der ausgelieferte Text bleibt genau der, den jemand geschrieben hat");
    }
}
