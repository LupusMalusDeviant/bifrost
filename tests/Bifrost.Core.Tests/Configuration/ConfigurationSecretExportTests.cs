using AwesomeAssertions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Configuration;
using Xunit;

namespace Bifrost.Core.Tests.Configuration;

/// <summary>
/// Der wichtigste Test dieses Pakets. ADR-0024 E8 nennt den Zweck der Trennung von Backup und
/// Export ausdrücklich: zu verhindern, „dass jemand versehentlich seine Zugangsdaten in ein
/// Git-Repository legt, weil er ‚die Konfiguration' exportieren wollte."
/// </summary>
public class ConfigurationSecretExportTests
{
    /// <summary>
    /// Kein Wert aus dem Negativkorpus steht im Standardexport — und auch kein Bruchstück davon.
    /// <para>
    /// Die Prüfung auf Bruchstücke ist keine Übertreibung: Der naheliegende Fehler ist nicht, ein
    /// Secret ganz zu vergessen, sondern es „nur zum Wiedererkennen" zu kürzen. Ein maskiertes
    /// Zugangsdatum ist ein Zugangsdatum mit weniger Zeichen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Standardexport_enthaelt_keinen_wert_aus_dem_negativkorpus()
    {
        var instance = ConfigurationFixtures.WithSecretsEverywhere();
        var service = ConfigurationFixtures.ServiceFor(instance);

        var export = await service.ExportAsync(new ConfigurationExportRequest(), TestContext.Current.CancellationToken);

        export.ContainsSecrets.Should().BeFalse();

        foreach (var secret in ConfigurationFixtures.NegativeCorpus)
        {
            export.Payload.Should().NotContain(secret, "der Standardexport traegt keine Zugangsdaten");

            for (var start = 0; start + 8 <= secret.Length; start++)
            {
                var fragment = secret.Substring(start, 8);
                export.Payload.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                    .Should().BeFalse($"auch das Bruchstueck '{fragment}' darf nicht im Export stehen");
            }
        }
    }

    /// <summary>
    /// Jede entfernte Stelle steht als Referenz im Dokument — mit Ort, ohne Wert. Ein Export, der
    /// schweigend weglässt, wäre auf der Zielinstanz nicht nachvollziehbar.
    /// </summary>
    [Fact]
    public async Task Jede_entfernte_stelle_wird_als_referenz_benannt()
    {
        var instance = ConfigurationFixtures.WithSecretsEverywhere();
        var service = ConfigurationFixtures.ServiceFor(instance);

        var export = await service.ExportAsync(new ConfigurationExportRequest(), TestContext.Current.CancellationToken);
        var document = Deserialize(export.Payload);

        document.SecretReferences.Select(r => r.Reference).Should().BeEquivalentTo(
        [
            "${bifrost:secret/upstream/mit-cli/cli-env/API_KEY}",
            "${bifrost:secret/upstream/mit-http/http-header/Authorization}",
            "${bifrost:secret/upstream/mit-http/http-oauth/client-secret}",
            "${bifrost:secret/upstream/mit-openapi/openapi/credential}",
            "${bifrost:secret/upstream/mit-openrpc/openrpc/credential}",
            "${bifrost:secret/upstream/mit-stdio/stdio-env/GITHUB_TOKEN}",
            "${bifrost:secret/upstream/mit-wasi/wasi-secret/TOKEN}",
        ]);

        document.SecretReferences.Should().AllSatisfy(r => r.Location.Should().NotBeNullOrWhiteSpace());
    }

    /// <summary>
    /// Die Referenz darf nichts über den Wert verraten — sie wird ausschließlich aus dem Ort
    /// abgeleitet. Zwei Instanzen mit verschiedenen Zugangsdaten an derselben Stelle ergeben
    /// dieselbe Referenz; alles andere wäre ein Seitenkanal.
    /// </summary>
    [Fact]
    public async Task Referenz_haengt_nur_am_ort_nicht_am_wert()
    {
        var erste = ConfigurationFixtures.WithSecretsEverywhere();
        var zweite = ConfigurationFixtures.WithSecretsEverywhere();
        zweite.Upstreams[0] = zweite.Upstreams[0] with
        {
            Config = zweite.Upstreams[0].Config with
            {
                Stdio = zweite.Upstreams[0].Config.Stdio! with
                {
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["GITHUB_TOKEN"] = "ein-ganz-anderer-wert-mit-anderer-laenge",
                    },
                },
            },
        };

        var a = await ConfigurationFixtures.ServiceFor(erste)
            .ExportAsync(new ConfigurationExportRequest(), TestContext.Current.CancellationToken);
        var b = await ConfigurationFixtures.ServiceFor(zweite)
            .ExportAsync(new ConfigurationExportRequest(), TestContext.Current.CancellationToken);

        a.Payload.Should().Be(b.Payload);
    }

    /// <summary>
    /// Was nicht exportierbar ist, steht im Dokument. Ein Export, der Webhooks und Identitäten
    /// stillschweigend weglässt, sieht auf der Zielinstanz wie ein vollständiger aus.
    /// </summary>
    [Fact]
    public async Task Nicht_exportierbares_wird_benannt_statt_verschwiegen()
    {
        var instance = ConfigurationFixtures.WithSecretsEverywhere();
        var service = ConfigurationFixtures.ServiceFor(instance);

        var export = await service.ExportAsync(new ConfigurationExportRequest(), TestContext.Current.CancellationToken);
        var document = Deserialize(export.Payload);

        document.NotExportable.Should().HaveCount(4);
        document.NotExportable.Select(n => n.Subject).Should().BeEquivalentTo(
            ["2 Identitäten", "3 API-Keys", "1 Webhook", "1 OAuth-Token"]);
    }

    /// <summary>
    /// Ein Vollexport ohne Passphrase entsteht gar nicht erst. ADR-0024 E8 lässt ihn nur
    /// verschlüsselt zu — anders als beim Backup gibt es hier keinen Fall, in dem eine
    /// Klartextfassung der richtige Weg wäre.
    /// </summary>
    [Fact]
    public async Task Vollexport_ohne_passphrase_wird_abgelehnt()
    {
        var service = ConfigurationFixtures.ServiceFor(ConfigurationFixtures.WithSecretsEverywhere());

        var act = async () => await service.ExportAsync(
            new ConfigurationExportRequest(IncludeSecrets: true), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>Auch der verschlüsselte Vollexport trägt die Werte nirgends im Klartext.</summary>
    [Fact]
    public async Task Verschluesselter_vollexport_zeigt_die_werte_nicht_im_klartext()
    {
        var service = ConfigurationFixtures.ServiceFor(ConfigurationFixtures.WithSecretsEverywhere());

        var export = await service.ExportAsync(
            new ConfigurationExportRequest(IncludeSecrets: true, Passphrase: "eine-lange-passphrase"),
            TestContext.Current.CancellationToken);

        export.ContainsSecrets.Should().BeTrue("ein Credential-Export sagt, was er ist");
        foreach (var secret in ConfigurationFixtures.NegativeCorpus)
        {
            export.Payload.Should().NotContain(secret);
        }
    }

    private static ConfigurationExportDocument Deserialize(string payload)
        => System.Text.Json.JsonSerializer.Deserialize<ConfigurationExportDocument>(
            payload, ConfigurationExportJson.Options)!;
}
