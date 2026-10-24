using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Integration.Tests.Gateway;
using Bifrost.Persistence.Backup;

using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Bifrost.Core.Operations;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// <b>BFR-F-0047 — der Riegel nach einem Restore.</b>
/// <para>
/// Gefunden im Piloten vom 2026-08-05 auf einer echten Installation aus dem veröffentlichten
/// Release: <c>bifrost restore --replace --yes</c> endete mit Exit 0 und
/// „Wiederhergestellt: Database, KeyRing, Config" — und danach bekam der API-Key <b>aus dem
/// Archiv</b> ein <c>401</c>, während der Schlüssel des soeben <b>überschriebenen</b> Zustands
/// weiter <c>200</c> bekam. Erst ein Neustart drehte es um. Der Restore war also richtig und
/// unwirksam zugleich, und die Meldung sagte davon nichts.
/// </para>
/// <para>
/// <b>Warum eine eigene Klasse mit eigener Fixture.</b> Der Riegel gilt für die Lebenszeit des
/// Prozesses. Bliebe dieser Test bei den übrigen Betriebs-Endpunkten, nähme der erste erfolgreiche
/// Restore alle nach ihm laufenden Tests derselben Klasse mit — und woran es liegt, stünde in
/// keinem Fehlschlag. <c>IClassFixture</c> ist je Klasse; ein eigener Host ist die Trennung.
/// </para>
/// <para>
/// <b>Warum alles in einem Test.</b> Aus demselben Grund: Ein zweiter Test in dieser Klasse käme
/// nicht einmal mehr bis zu seinem Plan-Aufruf. Der zweistufige Weg und der Riegel sind hier
/// derselbe Vorgang, nacheinander geprüft.
/// </para>
/// </summary>
public class RestoreRestartLatchTests : IClassFixture<GatewayFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] ConfigSection = ["Config"];

    private readonly GatewayFixture _fixture;

    public RestoreRestartLatchTests(GatewayFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Erst der Nachweis für den Nachtrag zum M2-Vertrag: Der Plan geht als JSON hinaus, kommt als
    /// <b>neues Objekt</b> zurück und ist trotzdem anwendbar — weil er ein Handle trägt und nicht
    /// seine Objektidentität. Dann der Riegel.
    /// <para>
    /// Gesichert und zurückgespielt wird ausschließlich der Bereich <c>Config</c>. Das ist bewusst
    /// der <em>kleinste</em> Restore, den es gibt: Wenn schon er den Riegel umlegt, dann jeder.
    /// Und er ist der ehrliche Fall — <c>instance.json</c> ist im laufenden Prozess ebenso gelesen
    /// und gehalten wie die Datenbank.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_restore_locks_the_gateway_until_it_is_restarted()
    {
        var client = await AdminClientAsync();
        var location = _fixture.Services.GetRequiredService<BackupLocation>();
        var archive = Path.Combine(
            location.Directory, "testarchive", $"config-{Guid.NewGuid():N}.zip");

        var created = await client.PostAsJsonAsync(
            "/api/v1/operations/backup",
            new { targetPath = archive, sections = ConfigSection },
            TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        // ── Stufe 1: planen ────────────────────────────────────────────────
        var planResponse = await client.PostAsJsonAsync(
            "/api/v1/operations/restore/plan",
            new { archivePath = archive, mode = "Replace" },
            TestContext.Current.CancellationToken);
        planResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var planText = await planResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Der Plan trägt weder Archivpfad noch Passphrase — nur das Handle. Eine Passphrase, die
        // durch eine API-Antwort läuft, steht danach in jedem Log.
        planText.Should().NotContain(archive);
        planText.Should().Contain("token");

        // Ein NEUES Objekt aus dem JSON: genau der Weg, den ein fremder Client geht.
        var roundTripped = JsonSerializer.Deserialize<RestorePlan>(planText, Json);
        roundTripped.Should().NotBeNull();
        roundTripped!.CanApply.Should().BeTrue(
            "der Plan muss anwendbar sein: " + string.Join(" | ", roundTripped.Blockers));
        roundTripped.Token.Should().NotBeNullOrEmpty();

        // ── Stufe 2: anwenden ──────────────────────────────────────────────
        var applyResponse = await client.PostAsync(
            "/api/v1/operations/restore/apply",
            new StringContent(JsonSerializer.Serialize(roundTripped, Json), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        applyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await applyResponse.Content.ReadFromJsonAsync<RestoreResult>(
            Json, TestContext.Current.CancellationToken);
        result.Should().NotBeNull();
        result!.Applied.Should().BeTrue();
        result.RestoredSections.Should().Be(BackupSections.Config);
        // ADR-0024 E5: Ohne Ausweg kein Überschreiben.
        result.PreBackupPath.Should().NotBeNullOrEmpty();
        File.Exists(result.PreBackupPath).Should().BeTrue();

        // ── Der Befund: das Ergebnis sagt es, und zwar als Zusage ──────────
        result.RestartRequired.Should().BeTrue(
            "der getauschte Stand liegt auf der Platte, nicht in diesem Prozess — und ein Aufrufer "
            + "muss das erfahren, ohne eine Notiz lesen zu müssen");
        result.Notes.Should().Contain(n => n.Contains("NEUSTART ERFORDERLICH", StringComparison.Ordinal));

        // ── Und der Riegel hält, für alle ──────────────────────────────────
        // Der API-Weg. Vor der Korrektur kam hier eine gewöhnliche Antwort — aus der ersetzten
        // Datenbank, mit den ersetzten Zugängen.
        var afterwards = await client.GetAsync(
            "/api/v1/servers", TestContext.Current.CancellationToken);
        afterwards.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var body = await afterwards.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("restart-required");

        // Auch die Gesundheitsprobe. Ein Container, der „healthy" meldet und nichts mehr
        // beantwortet, ist genau das Auseinanderfallen, das dieser Befund war.
        var health = await _fixture.CreateDefaultClient()
            .GetAsync("/healthz", TestContext.Current.CancellationToken);
        health.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Auch die Oberfläche. Sie rendert aus derselben Datenbank.
        var ui = await _fixture.CreateUiClient()
            .GetAsync("/login", TestContext.Current.CancellationToken);
        ui.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Der Riegel sitzt VOR allem anderen: Selbst der Wiederholungsversuch mit demselben Handle
        // — der sonst mit 409 abgewiesen würde, weil ein Handle einmalig ist — kommt nicht mehr bis
        // zum Dienst. Dass Handles einmalig sind, hält
        // OperationsApiTests.An_invented_handle_is_refused_instead_of_guessed fest.
        var second = await client.PostAsync(
            "/api/v1/operations/restore/apply",
            new StringContent(JsonSerializer.Serialize(roundTripped, Json), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, key) = await _fixture.SeedAdminAsync("restore-admin-" + Guid.NewGuid().ToString("N")[..8]);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
}
