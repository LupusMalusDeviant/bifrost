using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Invocation;
using McpMcp.Core.Tests.Catalog;
using Xunit;

namespace McpMcp.Core.Tests.Invocation;

/// <summary>
/// FR-40: Skills sind über Meta-Tools erreichbar, nach demselben Muster wie der Katalog — entdecken
/// ist billig, der Text kommt auf Abruf.
/// </summary>
public class SkillMetaToolTests
{
    private readonly InvokerTestWorld _w = new();
    private readonly FakeAssetStore _assets = new();

    private Task<ToolInvocationResult> ExecuteAsync(IdentityId caller, string metaTool, object args)
        => _w.WithAssets(_assets).ExecuteAsync(
            caller, CallOrigin.Mcp, metaTool,
            JsonSerializer.SerializeToElement(args), TestContext.Current.CancellationToken);

    private Task<AssetId> SeedAsync(string name, string? description, string content, SkillMetadata? metadata = null)
        => _assets.CreateAsync(name, description, content, metadata, TestContext.Current.CancellationToken);

    [Fact]
    public async Task List_shows_when_to_use_but_not_the_text()
    {
        await SeedAsync("release", "Wie ein Release gebaut wird", "Ein sehr langer Text.",
            new SkillMetadata(WhenToUse: "Wenn ein Tag gesetzt werden soll"));
        var agent = _w.RegisterAgent();

        var result = await ExecuteAsync(agent, MetaToolService.ListSkillsName, new { });

        result.Status.Should().Be(InvocationStatus.Success);
        var skill = result.Content!.Value.GetProperty("skills").EnumerateArray().Single();
        skill.GetProperty("name").GetString().Should().Be("release");
        skill.GetProperty("whenToUse").GetString().Should().Be("Wenn ein Tag gesetzt werden soll",
            "die Angabe entscheidet über den Zugriff — sie gehört in die Liste, nicht erst in den Text");
        skill.TryGetProperty("content", out _).Should().BeFalse(
            "die Liste kostet Kontext für jeden Skill, der Text nur für den einen, den man liest");
    }

    /// <summary>
    /// Kein RBAC-Filter: Skills sind für jede authentifizierte Identität sichtbar (FR-40). Das ist
    /// entschieden — und steht hier, damit es niemand versehentlich ändert.
    /// </summary>
    [Fact]
    public async Task Identity_without_any_grant_sees_the_skills()
    {
        await SeedAsync("offen", "Für alle", "Inhalt");
        var ohneGrant = _w.RegisterAgent();

        var result = await ExecuteAsync(ohneGrant, MetaToolService.ListSkillsName, new { });

        result.Content!.Value.GetProperty("skills").EnumerateArray().Should().ContainSingle();
    }

    /// <summary>
    /// Ein mehrteiliger Skill (Einstieg + <c>references/…</c>) blähte die Liste um seine Bestandteile
    /// auf, die niemand durchblättert. Bei 14 Einstiegen mit 61 Teilen war das der Unterschied
    /// zwischen ~1900 und ~5000 Tokens — und damit genau das, was die schrittweise Offenlegung
    /// verhindern soll.
    /// </summary>
    [Fact]
    public async Task Parts_referenced_by_another_skill_stay_out_of_the_list()
    {
        await SeedAsync("mapper/references/format", null, "Beiwerk");
        await SeedAsync("mapper", "Der Einstieg", "Text",
            new SkillMetadata(References: ["mapper/references/format"]));
        var agent = _w.RegisterAgent();

        var result = await ExecuteAsync(agent, MetaToolService.ListSkillsName, new { });

        // Der Teil ist über die Referenz des Einstiegs erreichbar, nicht über die Liste.
        Names(result).Should().Equal(["mapper"]);
    }

    [Fact]
    public async Task Parts_are_still_reachable_by_name_and_listable_on_request()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync("mapper/references/format", null, "Beiwerk");
        await SeedAsync("mapper", null, "Text", new SkillMetadata(References: ["mapper/references/format"]));
        var agent = _w.RegisterAgent();

        var read = await ExecuteAsync(agent, MetaToolService.ReadSkillName,
            new { name = "mapper/references/format" });
        var full = await ExecuteAsync(agent, MetaToolService.ListSkillsName, new { includeParts = true });

        read.Content!.Value.GetProperty("content").GetString().Should().Be("Beiwerk",
            "ausgeblendet heißt nicht unerreichbar");
        Names(full).Should().HaveCount(2, "wer alles sehen will, sagt es");
    }

    /// <summary>
    /// Der Filter darf NICHT am Namen hängen: Ein Skill aus einem Paket heißt
    /// <c>&lt;paket-id&gt;/&lt;skill&gt;</c> (ADR-0021) und ist trotzdem ein Einstieg. Ein Filter auf
    /// „enthält einen Schrägstrich" hätte genau die verschwinden lassen.
    /// </summary>
    [Fact]
    public async Task A_packaged_skill_stays_listed_although_its_name_contains_a_slash()
    {
        var ct = TestContext.Current.CancellationToken;
        await _assets.PublishFromPackageAsync(
            "com.example.echo/benutzung", "Aus dem Paket", "Inhalt", null,
            new SkillSource("com.example.echo", "1.0.0"), ct);
        var agent = _w.RegisterAgent();

        var result = await ExecuteAsync(agent, MetaToolService.ListSkillsName, new { });

        Names(result).Should().Equal(["com.example.echo/benutzung"]);
    }

    [Fact]
    public async Task List_filters_by_query_over_name_and_description()
    {
        await SeedAsync("release", "Tags und Pakete", "…");
        await SeedAsync("review", "Durchsicht von Änderungen", "…");
        var agent = _w.RegisterAgent();

        var byName = await ExecuteAsync(agent, MetaToolService.ListSkillsName, new { query = "rele" });
        var byDescription = await ExecuteAsync(agent, MetaToolService.ListSkillsName, new { query = "Durchsicht" });

        Names(byName).Should().Equal("release");
        Names(byDescription).Should().Equal("review");
    }

    [Fact]
    public async Task Read_returns_declarations_as_fields_and_the_text_unchanged()
    {
        await SeedAsync("einstieg", "Der Einstieg", "## Ablauf\nZuerst suchen.",
            new SkillMetadata("Beim Kartieren", ["einstieg/format"], ["github__create_issue"]));
        var agent = _w.RegisterAgent();

        var result = await ExecuteAsync(agent, MetaToolService.ReadSkillName, new { name = "einstieg" });

        result.Status.Should().Be(InvocationStatus.Success);
        var payload = result.Content!.Value;
        payload.GetProperty("whenToUse").GetString().Should().Be("Beim Kartieren");
        payload.GetProperty("references").EnumerateArray().Single().GetString().Should().Be("einstieg/format");
        payload.GetProperty("requiredTools").EnumerateArray().Single().GetString().Should().Be("github__create_issue");
        payload.GetProperty("content").GetString().Should().Be("## Ablauf\nZuerst suchen.",
            "in den ausgelieferten Text wird nichts hineinmontiert");
    }

    [Fact]
    public async Task Read_serves_the_latest_version()
    {
        var id = await SeedAsync("wandel", null, "alter Stand");
        await _assets.PublishAsync(id, "neuer Stand", null, TestContext.Current.CancellationToken);
        var agent = _w.RegisterAgent();

        var result = await ExecuteAsync(agent, MetaToolService.ReadSkillName, new { name = "wandel" });

        result.Content!.Value.GetProperty("content").GetString().Should().Be("neuer Stand");
        result.Content!.Value.GetProperty("version").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Read_without_name_fails_validation_and_unknown_skill_is_not_found()
    {
        var agent = _w.RegisterAgent();

        var ohneName = await ExecuteAsync(agent, MetaToolService.ReadSkillName, new { falsch = 1 });
        var unbekannt = await ExecuteAsync(agent, MetaToolService.ReadSkillName, new { name = "gibt-es-nicht" });

        ohneName.Status.Should().Be(InvocationStatus.ValidationFailed);
        unbekannt.Status.Should().Be(InvocationStatus.ToolNotFound);
    }

    /// <summary>
    /// Eine Zusammenstellung ohne Skill-Ablage ist gültig — sie darf die Meta-Tools nicht mit einem
    /// Absturz beantworten, sondern mit einem klaren „gibt es hier nicht".
    /// </summary>
    [Fact]
    public async Task Without_an_asset_store_the_skill_tools_report_absence()
    {
        var agent = _w.RegisterAgent();
        var args = JsonSerializer.SerializeToElement(new { name = "egal" });

        var list = await _w.MetaTools.ExecuteAsync(
            agent, CallOrigin.Mcp, MetaToolService.ListSkillsName, args, TestContext.Current.CancellationToken);
        var read = await _w.MetaTools.ExecuteAsync(
            agent, CallOrigin.Mcp, MetaToolService.ReadSkillName, args, TestContext.Current.CancellationToken);

        list.Status.Should().Be(InvocationStatus.ToolNotFound);
        read.Status.Should().Be(InvocationStatus.ToolNotFound);
    }

    [Fact]
    public async Task Skill_access_is_audited_like_every_other_call()
    {
        await SeedAsync("release", null, "Inhalt");
        var agent = _w.RegisterAgent();

        await ExecuteAsync(agent, MetaToolService.ReadSkillName, new { name = "release" });

        _w.Audit.Events.Should().ContainSingle().Which.Tool.Should().Be(MetaToolService.ReadSkillName);
    }

    private static List<string?> Names(ToolInvocationResult result)
        => [.. result.Content!.Value.GetProperty("skills").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString())];
}
