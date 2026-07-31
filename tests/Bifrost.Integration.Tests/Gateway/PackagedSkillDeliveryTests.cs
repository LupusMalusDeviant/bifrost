using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Skills aus einem Paket gegen den <b>echten</b> Store (Material 0021-EM, Option B). Der Ablauf
/// selbst ist in den Core-Tests belegt; hier geht es um das, was nur die Datenbank beantwortet:
/// Eindeutigkeit der Namen und dass ein eingespielter Skill wirklich beim Agenten ankommt.
/// </summary>
public sealed class PackagedSkillDeliveryTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public PackagedSkillDeliveryTests(GatewayFixture gw) => _gw = gw;

    private IAssetStore Assets => _gw.Services.GetRequiredService<IAssetStore>();

    private MetaToolService MetaTools => _gw.Services.GetRequiredService<MetaToolService>();

    [Fact]
    public async Task A_packaged_skill_is_readable_through_read_skill()
    {
        var ct = TestContext.Current.CancellationToken;
        var packageId = $"com.example.p{Guid.NewGuid():N}"[..24];
        var name = $"{packageId}/benutzung";

        await Assets.PublishFromPackageAsync(
            name, "Wie der Konnektor benutzt wird", "## Ablauf\nErst suchen.",
            new SkillMetadata("Beim ersten Aufruf", null, ["srv__tool"]),
            new SkillSource(packageId, "1.0.0"), ct);

        var caller = await _gw.SeedAdminAsync($"pkgskill-{Guid.NewGuid():N}");
        var result = await MetaTools.ExecuteAsync(
            caller.Identity, CallOrigin.Mcp, MetaToolService.ReadSkillName,
            JsonSerializer.SerializeToElement(new { name }), ct);

        result.Status.Should().Be(InvocationStatus.Success);
        result.Content!.Value.GetProperty("content").GetString().Should().Be("## Ablauf\nErst suchen.");
        result.Content!.Value.GetProperty("whenToUse").GetString().Should().Be("Beim ersten Aufruf");
    }

    [Fact]
    public async Task Publishing_from_a_package_appends_and_records_the_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"com.example.q{Guid.NewGuid():N}"[..24] + "/benutzung";

        var first = await Assets.PublishFromPackageAsync(
            name, null, "Fassung 1", null, new SkillSource("com.example.q", "1.0.0"), ct);
        var second = await Assets.PublishFromPackageAsync(
            name, null, "Fassung 2", null, new SkillSource("com.example.q", "1.1.0"), ct);

        second.Id.Should().Be(first.Id, "derselbe Name ist derselbe Skill");
        second.Version.Value.Should().Be(first.Version.Value + 1);
        second.ReplacedLocalEdit.Should().BeFalse();

        var latest = await Assets.GetAsync(first.Id, null, ct);
        latest.Source.Should().Be(new SkillSource("com.example.q", "1.1.0"));
        (await Assets.GetVersionsAsync(first.Id, ct)).Should().HaveCount(2,
            "die vorherige Fassung bleibt — append-only gilt auch für Paket-Updates");
    }

    /// <summary>
    /// Eine von Hand veröffentlichte Version verliert die Herkunft. Genau daran erkennt das nächste
    /// Paket-Update, dass es eine angepasste Fassung ablösen würde.
    /// </summary>
    [Fact]
    public async Task A_manual_version_drops_the_origin_and_the_next_package_update_reports_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"com.example.r{Guid.NewGuid():N}"[..24] + "/benutzung";

        var installed = await Assets.PublishFromPackageAsync(
            name, null, "Herstellerfassung", null, new SkillSource("com.example.r", "1.0.0"), ct);
        await Assets.PublishAsync(installed.Id, "Meine Fassung", null, ct);

        (await Assets.GetAsync(installed.Id, null, ct)).Source.Should().BeNull();

        var update = await Assets.PublishFromPackageAsync(
            name, null, "Herstellerfassung 1.1", null, new SkillSource("com.example.r", "1.1.0"), ct);

        update.ReplacedLocalEdit.Should().BeTrue(
            "wer den Text angepasst hat, muss erfahren, dass ein Update ihn ablöst");
        (await Assets.GetVersionsAsync(installed.Id, ct)).Select(v => v.Content)
            .Should().Contain("Meine Fassung", "die eigene Fassung bleibt in der Historie");
    }

    /// <summary>
    /// ADR-0021 F5 gegen den echten Store: Gelöscht werden <b>alle</b> Versionen, auch die von Hand
    /// veröffentlichte obenauf. Bliebe sie stehen, wäre der Name weiter belegt — und der Skill
    /// halb weg.
    /// </summary>
    [Fact]
    public async Task Deleting_a_packages_skills_takes_every_version_and_frees_the_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var packageId = $"com.example.s{Guid.NewGuid():N}"[..24];
        var name = $"{packageId}/benutzung";

        var published = await Assets.PublishFromPackageAsync(
            name, null, "Herstellerfassung", null, new SkillSource(packageId, "1.0.0"), ct);
        await Assets.PublishAsync(published.Id, "Meine Fassung", null, ct);

        var preview = await Assets.ListFromPackageAsync(packageId, ct);
        preview.Should().ContainSingle().Which.Source.Should().BeNull(
            "die Ankündigung muss sagen, dass hier eigene Arbeit mitgeht");

        var removed = await Assets.DeleteFromPackageAsync(packageId, ct);

        removed.Should().Equal([name]);
        (await Assets.ListAsync(ct)).Should().NotContain(a => a.Name == name);
        (await Assets.GetVersionsAsync(published.Id, ct)).Should().BeEmpty();

        // Der Beweis, dass der Name wirklich frei ist: Anlegen würde sonst am eindeutigen Index
        // scheitern.
        var act = async () => await Assets.CreateAsync(name, null, "neu", null, ct);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_deleted_skill_is_gone_for_agents_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var packageId = $"com.example.t{Guid.NewGuid():N}"[..24];
        var name = $"{packageId}/benutzung";
        await Assets.PublishFromPackageAsync(
            name, null, "Anleitung", null, new SkillSource(packageId, "1.0.0"), ct);
        await Assets.DeleteFromPackageAsync(packageId, ct);

        var caller = await _gw.SeedAdminAsync($"pkgdel-{Guid.NewGuid():N}");
        var result = await MetaTools.ExecuteAsync(
            caller.Identity, CallOrigin.Mcp, MetaToolService.ReadSkillName,
            JsonSerializer.SerializeToElement(new { name }), ct);

        result.Status.Should().Be(InvocationStatus.ToolNotFound,
            "genau darum ging es: keine Anleitung für Tools, die es nicht mehr gibt");
    }

    [Fact]
    public async Task Skills_of_another_package_are_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var bleibt = $"com.example.u{Guid.NewGuid():N}"[..24];
        var geht = $"com.example.v{Guid.NewGuid():N}"[..24];
        await Assets.PublishFromPackageAsync(
            $"{bleibt}/a", null, "A", null, new SkillSource(bleibt, "1.0.0"), ct);
        await Assets.PublishFromPackageAsync(
            $"{geht}/b", null, "B", null, new SkillSource(geht, "1.0.0"), ct);

        await Assets.DeleteFromPackageAsync(geht, ct);

        (await Assets.ListAsync(ct)).Should().Contain(a => a.Name == $"{bleibt}/a");
    }

    /// <summary>
    /// Skills werden über ihren <b>Namen</b> ausgeliefert. Zwei gleichen Namens wären nicht
    /// unterscheidbar — ausgeliefert würde der erstbeste. Das war bis hierhin möglich.
    /// </summary>
    [Fact]
    public async Task Two_skills_cannot_share_a_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"doppelt-{Guid.NewGuid():N}";
        await Assets.CreateAsync(name, null, "Der erste", null, ct);

        var act = async () => await Assets.CreateAsync(name, null, "Der zweite", null, ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
