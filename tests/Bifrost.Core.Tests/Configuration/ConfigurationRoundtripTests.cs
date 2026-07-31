using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Configuration;
using Xunit;

namespace Bifrost.Core.Tests.Configuration;

/// <summary>
/// Export → Import auf einer leeren Instanz → semantisch gleiche Konfiguration. Das ist der Zweck
/// des Formats: eine gleichartige Instanz aufbauen (ADR-0024 E8).
/// </summary>
public class ConfigurationRoundtripTests
{
    [Fact]
    public async Task Roundtrip_auf_leerer_instanz_ergibt_dieselbe_konfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        var quelle = ConfigurationFixtures.SecretFree();
        var export = await ConfigurationFixtures.ServiceFor(quelle)
            .ExportAsync(new ConfigurationExportRequest(), ct);

        var ziel = new FakeInstance();
        ziel.IsEmpty.Should().BeTrue();

        var zielDienst = ConfigurationFixtures.ServiceFor(ziel);
        var plan = await zielDienst.PlanImportAsync(export.Payload, null, ct);

        plan.CanApply.Should().BeTrue();
        plan.Conflicts.Should().BeEmpty();
        plan.MissingDependencies.Should().BeEmpty();
        plan.Additions.Should().NotBeEmpty();

        await zielDienst.ApplyImportAsync(plan, ct);

        // „Semantisch gleich" heißt: Was die Zielinstanz jetzt exportieren würde, ist Zeichen für
        // Zeichen dasselbe Dokument. Ein Vergleich einzelner Felder würde genau das übersehen, was
        // beim Abbilden verloren geht.
        var gegenprobe = await zielDienst.ExportAsync(new ConfigurationExportRequest(), ct);
        gegenprobe.Payload.Should().Be(export.Payload);
    }

    [Fact]
    public async Task Ids_bleiben_erhalten_damit_die_grants_nicht_ins_leere_zeigen()
    {
        var ct = TestContext.Current.CancellationToken;
        var quelle = ConfigurationFixtures.SecretFree();
        var export = await ConfigurationFixtures.ServiceFor(quelle)
            .ExportAsync(new ConfigurationExportRequest(), ct);

        var ziel = new FakeInstance();
        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        await dienst.ApplyImportAsync(await dienst.PlanImportAsync(export.Payload, null, ct), ct);

        ziel.Upstreams.Single().Id.Should().Be(quelle.Upstreams.Single().Id);
        ziel.Roles.Single().Id.Should().Be(quelle.Roles.Single().Id);
        ziel.Roles.Single().Grants.Single().Scope.Server.Should().Be(quelle.Upstreams.Single().Id);
        ziel.Profiles.Single().Id.Should().Be(quelle.Profiles.Single().Id);
        ziel.Skills.Single().Id.Should().Be(quelle.Skills.Single().Id);
    }

    /// <summary>
    /// Der Standardexport überträgt kein Zugangsdatum — also darf der Import den Upstream nicht so
    /// anlegen, als wäre er betriebsbereit. Er entsteht abgeschaltet, und das steht im Plan.
    /// </summary>
    [Fact]
    public async Task Import_ohne_zugangsdaten_legt_den_upstream_abgeschaltet_an()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.WithSecretsEverywhere())
            .ExportAsync(new ConfigurationExportRequest(), ct);

        var ziel = new FakeInstance();
        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        var plan = await dienst.PlanImportAsync(export.Payload, null, ct);
        await dienst.ApplyImportAsync(plan, ct);

        ziel.Upstreams.Should().OnlyContain(u => !u.Config.Enabled);
        plan.Additions.Should().Contain(a => a.Contains("abgeschaltet", StringComparison.Ordinal));
        plan.Additions.Should().Contain(a => a.Contains("nachgetragen", StringComparison.Ordinal));
    }

    /// <summary>
    /// Der verschlüsselte Vollexport ist der Weg, auf dem eine Instanz vollständig umzieht — dort
    /// kommen die Zugangsdaten mit, und der Upstream bleibt eingeschaltet.
    /// </summary>
    [Fact]
    public async Task Verschluesselter_vollexport_stellt_die_zugangsdaten_wieder_her()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Passphrase = "korrekt-pferd-batterie-heftklammer";

        var quelle = ConfigurationFixtures.WithSecretsEverywhere();
        var export = await ConfigurationFixtures.ServiceFor(quelle).ExportAsync(
            new ConfigurationExportRequest(IncludeSecrets: true, Passphrase: Passphrase), ct);

        var ziel = new FakeInstance();
        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        var plan = await dienst.PlanImportAsync(export.Payload, Passphrase, ct);
        plan.CanApply.Should().BeTrue();
        await dienst.ApplyImportAsync(plan, ct);

        var stdio = ziel.Upstreams.Single(u => u.Config.Slug == "mit-stdio").Config;
        stdio.Enabled.Should().BeTrue();
        stdio.Stdio!.EnvironmentVariables!["GITHUB_TOKEN"].Should().Be(ConfigurationFixtures.StdioEnvSecret);

        ziel.Upstreams.Single(u => u.Config.Slug == "mit-http").Config.Http!.OAuth!.ClientSecret
            .Should().Be(ConfigurationFixtures.OAuthClientSecret);
        ziel.Upstreams.Single(u => u.Config.Slug == "mit-openrpc").Config.OpenRpc!.Credential
            .Should().Be(ConfigurationFixtures.OpenRpcSecret);
        ziel.Upstreams.Single(u => u.Config.Slug == "mit-wasi").Config.Wasi!.Secrets!["TOKEN"]
            .Should().Be(ConfigurationFixtures.WasiSecret);
    }

    /// <summary>
    /// Ein Import löscht nichts, was im Export fehlt. Das ist die Grenze zum Restore (ADR-0024 E5):
    /// Wer eine Instanz auf einen Stand zurücksetzen will, nimmt ein Backup, keinen Export.
    /// </summary>
    [Fact]
    public async Task Import_loescht_nichts_was_im_export_fehlt()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.SecretFree())
            .ExportAsync(new ConfigurationExportRequest(), ct);

        var ziel = new FakeInstance();
        var fremdeRolle = new Role(RoleId.New(), "Nur-hier", [], null);
        ziel.Roles.Add(fremdeRolle);
        ziel.Skills.Add(new SkillSnapshot(AssetId.New(), "nur-hier", null, "Text", SkillMetadata.Empty));

        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        await dienst.ApplyImportAsync(await dienst.PlanImportAsync(export.Payload, null, ct), ct);

        ziel.Roles.Should().Contain(fremdeRolle);
        ziel.Skills.Should().Contain(s => s.Name == "nur-hier");
    }
}
