using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Configuration;
using Xunit;

namespace Bifrost.Core.Tests.Configuration;

/// <summary>
/// Die Zusagen des zweistufigen Imports: Der Plan sagt vorher, was passiert; das Anwenden passiert
/// ganz oder gar nicht. Ein Import, der erst beim Schreiben merkt, dass er nicht passt, hat bereits
/// geschrieben (M2-Vertrag §1).
/// </summary>
public class ConfigurationImportPlanTests
{
    [Fact]
    public async Task Namenskollision_wird_als_konflikt_gemeldet_und_nicht_ueberschrieben()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.SecretFree())
            .ExportAsync(new ConfigurationExportRequest(), ct);

        var ziel = new FakeInstance();
        var bestehendeRolle = new Role(RoleId.New(), "Leser", [], new RateLimit(5));
        var bestehenderSkill = new SkillSnapshot(
            AssetId.New(), "wetter-abfragen", "etwas anderes", "Anderer Text", SkillMetadata.Empty);
        ziel.Roles.Add(bestehendeRolle);
        ziel.Skills.Add(bestehenderSkill);

        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        var plan = await dienst.PlanImportAsync(export.Payload, null, ct);

        plan.CanApply.Should().BeFalse();
        plan.Conflicts.Should().Contain(c => c.Contains("Rolle 'Leser'", StringComparison.Ordinal));
        plan.Conflicts.Should().Contain(c => c.Contains("Skill 'wetter-abfragen'", StringComparison.Ordinal));

        var act = async () => await dienst.ApplyImportAsync(plan, ct);
        await act.Should().ThrowAsync<ConfigurationImportException>();

        ziel.Writes.Should().Be(0, "ein nicht anwendbarer Plan schreibt nichts");
        ziel.Roles.Single().Should().Be(bestehendeRolle);
        ziel.Skills.Single().Should().Be(bestehenderSkill);
    }

    /// <summary>
    /// Fehlende Abhängigkeit. Die Aufgabenstellung nennt als Beispiel „Profil verweist auf unbekannte
    /// Rolle" — im heutigen Modell verweist ein <see cref="ToolProfile"/> nicht auf Rollen, sondern
    /// über angeheftete Werkzeuge auf Upstreams (und eine <see cref="Role"/> über ihre Grants
    /// ebenso). Geprüft wird deshalb derselbe Sachverhalt an der Stelle, an der es ihn gibt.
    /// </summary>
    [Fact]
    public async Task Fehlende_abhaengigkeit_wird_gemeldet_und_nicht_angewendet()
    {
        var ct = TestContext.Current.CancellationToken;
        var quelle = ConfigurationFixtures.SecretFree();
        quelle.Profiles.Add(new ToolProfile(
            ProfileId.New(), "Verwaist", [NamespacedToolName.Create("gibtsnicht", "werkzeug")], false));
        quelle.Roles.Add(new Role(
            RoleId.New(),
            "Verwaiste-Rolle",
            [new Grant(new PermissionScope(new ServerId(Guid.Parse("99999999-9999-9999-9999-999999999999")), null), [ToolAction.UseTool])],
            null));

        var export = await ConfigurationFixtures.ServiceFor(quelle)
            .ExportAsync(new ConfigurationExportRequest(), ct);

        var ziel = new FakeInstance();
        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        var plan = await dienst.PlanImportAsync(export.Payload, null, ct);

        plan.CanApply.Should().BeFalse();
        plan.MissingDependencies.Should().Contain(m => m.Contains("Profil 'Verwaist'", StringComparison.Ordinal));
        plan.MissingDependencies.Should().Contain(m => m.Contains("Rolle 'Verwaiste-Rolle'", StringComparison.Ordinal));

        var act = async () => await dienst.ApplyImportAsync(plan, ct);
        await act.Should().ThrowAsync<ConfigurationImportException>();

        ziel.IsEmpty.Should().BeTrue();
        ziel.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Verschluesselter_vollexport_mit_falscher_passphrase_scheitert_klar()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.WithSecretsEverywhere())
            .ExportAsync(new ConfigurationExportRequest(IncludeSecrets: true, Passphrase: "richtig"), ct);

        var ziel = new FakeInstance();
        var dienst = ConfigurationFixtures.ServiceFor(ziel);

        var act = async () => await dienst.PlanImportAsync(export.Payload, "falsch", ct);

        (await act.Should().ThrowAsync<ConfigurationImportException>())
            .Which.Message.Should().Contain("Passphrase");
        ziel.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Verschluesselter_export_ohne_passphrase_scheitert_klar()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.WithSecretsEverywhere())
            .ExportAsync(new ConfigurationExportRequest(IncludeSecrets: true, Passphrase: "richtig"), ct);

        var dienst = ConfigurationFixtures.ServiceFor(new FakeInstance());

        var act = async () => await dienst.PlanImportAsync(export.Payload, null, ct);

        (await act.Should().ThrowAsync<ConfigurationImportException>())
            .Which.Message.Should().Contain("verschlüsselt");
    }

    /// <summary>
    /// Der Kopf des Umschlags ist Associated Data der Verschlüsselung: Wer daran dreht, um einen
    /// Credential-Export als harmlos auszugeben, bekommt einen Fehler statt eines Dokuments.
    /// </summary>
    [Fact]
    public async Task Manipulierter_kopf_macht_den_export_unlesbar()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.WithSecretsEverywhere())
            .ExportAsync(new ConfigurationExportRequest(IncludeSecrets: true, Passphrase: "richtig"), ct);

        var manipuliert = export.Payload.Replace(
            "\"containsSecrets\": true", "\"containsSecrets\": false", StringComparison.Ordinal);
        manipuliert.Should().NotBe(export.Payload, "die Vorbedingung des Tests muss greifen");

        var dienst = ConfigurationFixtures.ServiceFor(new FakeInstance());
        var act = async () => await dienst.PlanImportAsync(manipuliert, "richtig", ct);

        await act.Should().ThrowAsync<ConfigurationImportException>();
    }

    /// <summary>
    /// Teilfehler beim Anwenden. Der Skill scheitert als vorletzter Schritt — Upstream, Rolle,
    /// Profil, Guard-Regel und Freigabe sind zu diesem Zeitpunkt bereits geschrieben und müssen
    /// wieder verschwinden.
    /// </summary>
    [Fact]
    public async Task Teilfehler_beim_anwenden_laesst_nichts_halb_angewendet()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.SecretFree())
            .ExportAsync(new ConfigurationExportRequest(), ct);

        var ziel = new FakeInstance { FailWhenAdding = "wetter-abfragen" };
        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        var plan = await dienst.PlanImportAsync(export.Payload, null, ct);
        plan.CanApply.Should().BeTrue("der Plan ist stimmig — der Fehler passiert erst beim Schreiben");

        var act = async () => await dienst.ApplyImportAsync(plan, ct);

        (await act.Should().ThrowAsync<ConfigurationImportException>())
            .Which.Message.Should().Contain("zurückgenommen");

        ziel.Writes.Should().BeGreaterThan(0, "es wurde geschrieben — und wieder zurueckgenommen");
        ziel.IsEmpty.Should().BeTrue("nach der Ruecknahme steht die Instanz wie vorher");
    }

    /// <summary>
    /// Ein Fehler beim Zurücknehmen wird gemeldet, nicht verschluckt: Wenn schon etwas stehen
    /// bleibt, muss jemand erfahren, was.
    /// </summary>
    [Fact]
    public async Task Unvollstaendige_ruecknahme_wird_benannt()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.SecretFree())
            .ExportAsync(new ConfigurationExportRequest(), ct);

        var ziel = new FakeInstance { FailWhenAdding = "wetter-abfragen", FailOnRollback = true };
        var dienst = ConfigurationFixtures.ServiceFor(ziel);

        var plan = await dienst.PlanImportAsync(export.Payload, null, ct);
        var act = async () => await dienst.ApplyImportAsync(plan, ct);

        (await act.Should().ThrowAsync<ConfigurationImportException>())
            .Which.Message.Should().Contain("Rücknahme war unvollständig");
    }

    [Fact]
    public async Task Unbekannte_formatversion_wird_nicht_geraten()
    {
        var ct = TestContext.Current.CancellationToken;
        var export = await ConfigurationFixtures.ServiceFor(ConfigurationFixtures.SecretFree())
            .ExportAsync(new ConfigurationExportRequest(), ct);
        var ausDerZukunft = export.Payload.Replace(
            "\"formatVersion\": 1", "\"formatVersion\": 99", StringComparison.Ordinal);

        var dienst = ConfigurationFixtures.ServiceFor(new FakeInstance());
        var act = async () => await dienst.PlanImportAsync(ausDerZukunft, null, ct);

        (await act.Should().ThrowAsync<ConfigurationImportException>())
            .Which.Message.Should().Contain("99");
    }

    /// <summary>
    /// Ein Plan, der nicht aus diesem Vorgang stammt, wird nicht angewendet. Er trägt keinen Verweis
    /// auf die Nutzlast — ihn trotzdem anzuwenden hieße, auf geratenen Daten zu schreiben.
    /// </summary>
    [Fact]
    public async Task Fremder_plan_wird_nicht_angewendet()
    {
        var ziel = new FakeInstance();
        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        var erfunden = new ConfigurationImportPlan(true, [], [], []);

        var act = async () => await dienst.ApplyImportAsync(erfunden, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConfigurationImportException>();
        ziel.Writes.Should().Be(0);
    }

    /// <summary>
    /// Der Grund für das Handle im Plan. Über eine HTTP-Schnittstelle geht der Plan als JSON hinaus
    /// und kommt als <em>neues Objekt</em> zurück — an der Objektidentität wiedererkannt wäre er
    /// dort niemals anwendbar.
    /// </summary>
    [Fact]
    public async Task Plan_der_als_json_gereist_ist_bleibt_anwendbar()
    {
        var ct = TestContext.Current.CancellationToken;
        var nutzlast = (await ConfigurationFixtures
            .ServiceFor(ConfigurationFixtures.SecretFree())
            .ExportAsync(new ConfigurationExportRequest(), ct)).Payload;

        var ziel = new FakeInstance();
        var dienst = ConfigurationFixtures.ServiceFor(ziel);
        var plan = await dienst.PlanImportAsync(nutzlast, null, ct);
        plan.CanApply.Should().BeTrue();

        var gereist = JsonSerializer.Deserialize<ConfigurationImportPlan>(
            JsonSerializer.Serialize(plan))!;
        gereist.Should().NotBeSameAs(plan);
        gereist.Token.Should().Be(plan.Token);

        await dienst.ApplyImportAsync(gereist, ct);

        ziel.Writes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Unveraendertes_steht_getrennt_von_neuem()
    {
        var ct = TestContext.Current.CancellationToken;
        var quelle = ConfigurationFixtures.SecretFree();
        var nutzlast = (await ConfigurationFixtures.ServiceFor(quelle)
            .ExportAsync(new ConfigurationExportRequest(), ct)).Payload;

        var plan = await ConfigurationFixtures.ServiceFor(quelle).PlanImportAsync(nutzlast, null, ct);

        plan.Unchanged.Should().NotBeNullOrEmpty(
            "ein Export gegen die eigene Instanz legt nichts an, und das ist kein Konflikt");
        plan.Additions.Should().NotIntersectWith(plan.Unchanged!);
    }
}
