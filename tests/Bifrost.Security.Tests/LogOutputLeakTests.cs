using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Security.Tests.Infrastructure;
using Bifrost.Server.Bootstrap;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bifrost.Security.Tests;

/// <summary>
/// <b>Invariante 5:</b> Ein Mithoerer an <b>allen</b> Ausgabekanaelen prueft den Secret-Korpus
/// gegen jede Zeile, die der Dienst schreibt.
/// <para>
/// <b>Warum am laufenden Dienst und nicht an einzelnen Klassen:</b> Eine Logzeile mit Klartext
/// entsteht selten dort, wo jemand ein Geheimnis vermutet. Sie entsteht in der Ausnahmebehandlung
/// eines Startversuchs, in einem Debug-Aufruf beim Neuladen der Konfiguration, im Text einer
/// Datenbankausnahme, die die Verbindungszeichenfolge mitfuehrt. Diese Stellen finden sich nicht
/// durch Lesen, sondern indem man den Dienst dorthin treibt und mitschreibt.
/// </para>
/// <para>
/// <b>Wie er bei einer neuen Stelle rot wird:</b> Der Mithoerer ist an den Logfabrik-Provider und
/// an <see cref="Trace"/> gehaengt und filtert nichts weg (<c>LogLevel.Trace</c>). Jede neue
/// Logzeile in einem der hier durchlaufenen Pfade landet automatisch in der Pruefung — es gibt
/// keine Liste, in die sie eingetragen werden muesste.
/// </para>
/// <para>
/// <b>Grenze, ausdruecklich:</b> Geprueft werden die Pfade, die dieser Test durchlaeuft. Ein
/// Ausgabeweg, den niemand ausloest, schreibt auch nichts mit. Wer einen neuen Pfad baut, ergaenzt
/// hier einen Aufruf — dieselbe Regel wie beim Korpus der Transporte.
/// </para>
/// </summary>
public class LogOutputLeakTests : IClassFixture<SecurityGatewayFixture>, IDisposable
{
    private readonly SecurityGatewayFixture _gateway;
    private readonly CapturingTraceListener _trace = new();

    public LogOutputLeakTests(SecurityGatewayFixture gateway)
    {
        _gateway = gateway;
        Trace.Listeners.Add(_trace);
    }

    public void Dispose()
    {
        Trace.Listeners.Remove(_trace);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task No_secret_from_the_corpus_reaches_any_log_channel()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminKey) = await _gateway.SeedAdminAsync("log-admin");
        using var client = _gateway.CreateApiClient(adminKey);

        // 1) Ein Upstream mit Zugangsdaten in jedem Transportfeld. Der Startversuch scheitert
        //    (das Programm gibt es nicht) — genau der Ausnahmepfad, in dem Konfigurationswerte
        //    gern im Fehlertext mitreisen.
        var config = new UpstreamServerConfig(
            "leck-probe", "Leckprobe", UpstreamTransportKind.Stdio, Enabled: true,
            Stdio: new StdioTransportOptions(
                "programm-das-es-nicht-gibt", ["--serve"],
                new Dictionary<string, string> { ["TOKEN"] = SecretCorpus.StdioEnv }),
            Http: new HttpTransportOptions(
                new Uri("https://example.invalid/mcp"),
                Headers: new Dictionary<string, string> { ["Authorization"] = SecretCorpus.HttpHeader }),
            OpenApi: new OpenApiTransportOptions(
                new Uri("https://example.invalid/openapi.json"),
                Credential: SecretCorpus.OpenApiCredential),
            OpenRpc: new OpenRpcTransportOptions(
                new Uri("https://example.invalid/rpc"), Credential: SecretCorpus.OpenRpcCredential),
            Cli: new CliTransportOptions(
                "werkzeug", [new CliToolSpec("run")],
                EnvironmentVariables: new Dictionary<string, string> { ["PASSWORD"] = SecretCorpus.CliEnv }),
            Wasi: new WasiTransportOptions(
                "bifrost-wasi-host", "component.wasm", "component.sig", ["cHVibGlzaGVy"],
                Secrets: new Dictionary<string, string> { ["API_KEY"] = SecretCorpus.WasiSecret }));

        using var rejected = await client.PostAsJsonAsync("/api/v1/servers", config, ct);
        (await rejected.Content.ReadAsStringAsync(ct)).Should().NotContainAny(SecretCorpus.All,
            "auch eine Absage ist eine Ausgabe — Fehlertexte tragen Konfigurationswerte mit");

        // 1b) Derselbe Weg, aber eine Konfiguration, die die Validierung besteht: Erst dann
        //     laeuft der Startversuch und mit ihm die Ausnahmebehandlung des Supervisors.
        using var created = await client.PostAsJsonAsync(
            "/api/v1/servers",
            config with { Http = null, OpenApi = null, OpenRpc = null, Cli = null, Wasi = null },
            ct);

        // 2) Die Ausgabewege ueber dieselbe Konfiguration.
        if (created.IsSuccessStatusCode)
        {
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetString();
            using var history = await client.GetAsync($"/api/v1/servers/{id}/history", ct);
            (await history.Content.ReadAsStringAsync(ct)).Should().NotContainAny(SecretCorpus.All,
                "die Versionshistorie geht durch den Redactor");
        }

        // 3) Toolaufruf mit Zugangsdaten in den Argumenten.
        using var invoked = await client.PostAsJsonAsync(
            "/api/v1/tools/gibt-es-nicht__tool/invoke",
            new { password = SecretCorpus.ToolArgument, api_key = SecretCorpus.ApiKeyPlaintext }, ct);

        // 4) Diagnose und Export — die beiden Berichte, die das Haus verlassen.
        using var doctor = await client.GetAsync("/api/v1/operations/doctor", ct);
        using var export = await client.PostAsJsonAsync("/api/v1/operations/config/export", new { }, ct);

        // 5) Ein Webhook mit Secret, und ein fehlgeschlagener Anmeldeversuch mit einem
        //    Korpuswert als Zugangsdatum — die Stelle, an der ein praesentierter Schluessel im
        //    Fehlerprotokoll landen wuerde.
        using var hook = await client.PostAsJsonAsync(
            "/api/v1/webhooks",
            new { name = "leck", callerId = Guid.NewGuid(), tool = SecretCorpus.WebhookSecret }, ct);
        using var wrongKey = _gateway.CreateApiClient(SecretCorpus.ApiKeyPlaintext);
        using var denied = await wrongKey.GetAsync("/api/v1/tools", ct);

        // Erst der Nachweis, dass ueberhaupt mitgeschrieben wurde. Ohne ihn waere ein
        // stillgelegter Mithoerer nicht von einem sauberen Dienst zu unterscheiden — und genau so
        // sieht ein Test aus, der nichts prueft.
        _gateway.Log.Count.Should().BeGreaterThan(0, "sonst prueft der folgende Vergleich einen leeren Text");

        SecretCorpus.FirstLeakIn(_gateway.Log.Text).Should().BeNull(
            "eine Logzeile mit Klartext-Zugangsdatum liegt danach in jedem Logarchiv, das der "
            + "Betrieb einsammelt — und dort laenger als in der Datenbank");
        SecretCorpus.FirstLeakIn(_trace.Text).Should().BeNull(
            "System.Diagnostics.Trace ist der Kanal, den die Logkonfiguration nicht erfasst");
    }

    /// <summary>
    /// <b>Der Abnahmetest zu WP3.4.</b> Der normale erste Start schreibt weder ein Adminpasswort
    /// noch einen API-Key noch das Setup-Token in irgendeinen Logkanal.
    /// <para>
    /// <b>Warum der Wert hier zur Laufzeit geholt wird und nicht aus dem Korpus kommt:</b> Das
    /// Setup-Token entsteht beim Start. Ein fest verdrahteter Wert koennte hoechstens pruefen, dass
    /// <em>irgendein</em> Wert nicht auftaucht — nicht, dass genau der auftaucht, den diese Instanz
    /// gerade ausgestellt hat. Geprueft wird deshalb gegen den Klartext aus der Uebergabedatei,
    /// also gegen den Wert, den ein Angreifer im Logarchiv suchen wuerde. Zusaetzlich laeuft der
    /// Korpuswert <see cref="SecretCorpus.BootstrapToken"/> durch den Einloesepfad: Er deckt die
    /// andere Haelfte ab — ein <em>vorgelegtes</em> Geheimnis, das in einer Fehlerzeile mitreist.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_first_start_writes_no_bootstrap_secret_to_any_log_channel()
    {
        var ct = TestContext.Current.CancellationToken;

        // Zuerst den Host wirklich starten: Die Fabrik baut ihn erst beim ersten Zugriff, und ohne
        // Start gaebe es den Erstzugangs-Pfad nicht, den dieser Test pruefen soll.
        using var anonymous = _gateway.CreateApiClient(null);

        // Dann der Nachweis, dass dieser Lauf ueberhaupt ein Erstzugangs-Token erzeugt hat. Ohne
        // ihn waere der Test gruen, weil nichts passiert ist.
        var handoverPath = BootstrapLayout.HandoverPathFor(_gateway.DataDirectory);
        File.Exists(handoverPath).Should().BeTrue(
            "der erste Start einer leeren Instanz stellt ein Setup-Token aus — sonst prueft das "
            + "Folgende einen Pfad, den es nicht gab");

        var token = (await File.ReadAllTextAsync(handoverPath, ct))
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith(BootstrapToken.Prefix, StringComparison.Ordinal));

        // Ein abgelehnter Einloeseversuch mit einem Korpuswert — der unauthentifizierte
        // Schreibweg, auf dem ein vorgelegtes Geheimnis in die Ausnahmebehandlung geraet.
        using var rejected = await anonymous.PostAsync(
            "/auth/setup",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = SecretCorpus.BootstrapToken,
                ["username"] = "leck-probe",
                ["password"] = "ein-langes-passwort",
            }),
            ct);
        (await rejected.Content.ReadAsStringAsync(ct)).Should().NotContainAny(SecretCorpus.All);

        _gateway.Log.Count.Should().BeGreaterThan(0, "sonst prueft der folgende Vergleich einen leeren Text");

        _gateway.Log.Text.Should().NotContain(token,
            "das Setup-Token im Anwendungslog waere genau der Fehler, den dieses Paket abschafft — "
            + "ein Geheimnis an dem Ort, den man weitergibt, wenn etwas nicht funktioniert");
        _trace.Text.Should().NotContain(token);

        // Und die Bruchstuecke: Ein truncierender Formatierer macht aus einem Geheimnis kein
        // halbes. Acht Zeichen genuegen, um es in einem Logarchiv wiederzufinden.
        foreach (var fragment in SecretCorpus.Fragments(token[BootstrapToken.Prefix.Length..]))
        {
            _gateway.Log.Text.Should().NotContain(fragment);
            _trace.Text.Should().NotContain(fragment);
        }

        SecretCorpus.FirstLeakIn(_gateway.Log.Text).Should().BeNull();
        SecretCorpus.FirstLeakIn(_trace.Text).Should().BeNull();
    }

    /// <summary>
    /// Die Gegenprobe zum Mithoerer selbst: Er muss einen Korpuswert finden, wenn einer da ist.
    /// Ohne diesen Test bliebe offen, ob der vorige gruen ist, weil nichts leckt — oder weil der
    /// Vergleich nichts findet.
    /// </summary>
    [Fact]
    public void The_listener_actually_detects_a_leak()
    {
        var provider = new CapturingLogProvider();
        var logger = provider.CreateLogger("Probe");

        logger.LogWarning("Verbindung fehlgeschlagen fuer {Ziel}", SecretCorpus.StdioEnv);

        SecretCorpus.FirstLeakIn(provider.Text).Should().Be(SecretCorpus.StdioEnv);
        SecretCorpus.FirstLeakIn("nichts Verdaechtiges").Should().BeNull();
    }

    /// <summary>
    /// Und die Gegenprobe zur Bruchstueckerkennung: Ein gekuerzter Wert ist kein halber Fehler.
    /// Acht Zeichen reichen, um ein Geheimnis in einem Logarchiv wiederzufinden.
    /// </summary>
    [Fact]
    public void A_truncated_secret_still_counts_as_a_leak()
    {
        var random = SecretCorpus.OAuthToken[(SecretCorpus.OAuthToken.LastIndexOf('-') + 1)..];

        SecretCorpus.FirstLeakIn($"... {random[..10]} ...").Should().NotBeNull();
        SecretCorpus.FirstLeakIn("KORPUS-oauth-").Should().BeNull(
            "der Praefix beschreibt die Stelle, er ist nicht das Geheimnis");
    }
}
