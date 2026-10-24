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
using Bifrost.Persistence.Startup;

using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Bifrost.Core.Operations;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// Die Betriebs-Endpunkte am laufenden Gateway (M2, WP2.7): Sicherung, zweistufige
/// Wiederherstellung, Diagnose und der Ausweg aus BFR-DB-0101.
/// </summary>
public class OperationsApiTests : IClassFixture<GatewayFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] ConfigSection = ["Config"];

    private static readonly string[] UnknownSection = ["datenbank"];

    private readonly GatewayFixture _fixture;

    public OperationsApiTests(GatewayFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_invented_handle_is_refused_instead_of_guessed()
    {
        var client = await AdminClientAsync();
        var invented = new RestorePlan(
            CanApply: true,
            Manifest: null,
            Mode: RestoreMode.Replace,
            TargetIsEmpty: false,
            Blockers: [],
            Warnings: [],
            PreBackupPath: null,
            Token: "0123456789abcdef0123456789abcdef");

        var response = await client.PostAsync(
            "/api/v1/operations/restore/apply",
            new StringContent(JsonSerializer.Serialize(invented, Json), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("kein Archiv bekannt");
    }

    [Fact]
    public async Task A_broken_archive_is_reported_as_invalid_without_a_restore()
    {
        var client = await AdminClientAsync();
        var location = _fixture.Services.GetRequiredService<BackupLocation>();
        var broken = Path.Combine(location.Directory, "testarchive", $"kaputt-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        await File.WriteAllTextAsync(broken, "das ist kein ZIP", TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/operations/backup/verify",
            new { archivePath = broken },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var inspection = await response.Content.ReadFromJsonAsync<BackupInspection>(
            Json, TestContext.Current.CancellationToken);
        inspection!.Valid.Should().BeFalse();
        inspection.Problems.Should().NotBeEmpty();
    }

    /// <summary>
    /// Ohne Grenze läse „prüfen" jede Datei, die der Gateway lesen darf, und die Antwort verriete, ob
    /// es sie gibt und ob sie ein Zip ist. Auch einem Admin wird das nicht beantwortet.
    /// </summary>
    [Fact]
    public async Task An_archive_outside_the_backup_directory_is_not_read()
    {
        var client = await AdminClientAsync();
        var outside = Path.Combine(Path.GetTempPath(), $"draussen-{Guid.NewGuid():N}.zip");
        await File.WriteAllTextAsync(outside, "das ist kein ZIP", TestContext.Current.CancellationToken);

        try
        {
            foreach (var path in new[] { outside, Path.Combine("..", "..", Path.GetFileName(outside)) })
            {
                var verify = await client.PostAsJsonAsync(
                    "/api/v1/operations/backup/verify", new { archivePath = path },
                    TestContext.Current.CancellationToken);
                verify.StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
                (await verify.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                    .Should().Contain("außerhalb des Sicherungsverzeichnisses");
            }

            var restore = await client.PostAsJsonAsync(
                "/api/v1/operations/restore/plan", new { archivePath = outside, mode = "Replace" },
                TestContext.Current.CancellationToken);
            restore.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>
    /// Eine Sicherung entsteht nur im Sicherungsverzeichnis; ein bloßer Name landet dort. Über eine
    /// fremde Datei zu schreiben, ist kein Bedienfehler, den man zulassen muss.
    /// </summary>
    [Fact]
    public async Task A_backup_is_only_written_into_the_backup_directory()
    {
        var client = await AdminClientAsync();
        var location = _fixture.Services.GetRequiredService<BackupLocation>();

        var outside = await client.PostAsJsonAsync(
            "/api/v1/operations/backup",
            new { targetPath = Path.Combine(Path.GetTempPath(), $"draussen-{Guid.NewGuid():N}.zip"), sections = ConfigSection },
            TestContext.Current.CancellationToken);
        outside.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var name = $"nur-name-{Guid.NewGuid():N}.zip";
        var inside = await client.PostAsJsonAsync(
            "/api/v1/operations/backup", new { targetPath = name, sections = ConfigSection },
            TestContext.Current.CancellationToken);
        inside.StatusCode.Should().Be(
            HttpStatusCode.OK, await inside.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        File.Exists(Path.Combine(location.Directory, name)).Should().BeTrue();

        var verify = await client.PostAsJsonAsync(
            "/api/v1/operations/backup/verify", new { archivePath = name },
            TestContext.Current.CancellationToken);
        verify.StatusCode.Should().Be(HttpStatusCode.OK, "derselbe Name findet die Sicherung wieder");
    }

    /// <summary>
    /// Die Sonden aus WP2.7 sind verdrahtet: Ohne sie melden BFR-DB-0002/0003/0004 und BFR-UP-0001
    /// dauerhaft <c>Skipped</c> — kein stilles Bestehen, aber eben auch keine Aussage.
    /// </summary>
    [Fact]
    public async Task Doctor_reports_real_database_and_upstream_findings()
    {
        await _fixture.AddEchoUpstreamAsync("doctor-echo");
        var client = await AdminClientAsync();

        var response = await client.GetAsync("/api/v1/operations/doctor", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var checks = document.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(
                check => check.GetProperty("code").GetString()!,
                check => check.GetProperty("status").GetString()!,
                StringComparer.Ordinal);

        checks.Should().ContainKey("BFR-DB-0002").WhoseValue.Should().Be("Pass");
        checks["BFR-DB-0003"].Should().NotBe("Skipped", "die Migrationshistorie ist lesbar");
        checks["BFR-DB-0004"].Should().Be("Pass", "das Schema ist auf dem Stand dieses Builds");
        checks["BFR-UP-0001"].Should().Be("Pass", "der EchoServer ist bereit");

        // Und die Befunde der Startkoordination hängen mit im selben Bericht — sonst stünde
        // BFR-DB-0101 in keiner Diagnose, obwohl genau der den Start verhindert.
        checks.Should().ContainKey(MigrationDiagnosticCodes.SchemaUpToDate);
    }

    [Fact]
    public async Task Db_unblock_is_reachable_and_reports_nothing_to_do_on_a_healthy_instance()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsync(
            "/api/v1/operations/database/unblock", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("removed").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// Diese Endpunkte sind mächtiger als alles andere im Produkt: Ein Vollbackup enthält den
    /// Key-Ring (ADR-0024 E3), ein Restore überschreibt die Instanz. Sie liegen deshalb hinter
    /// derselben Schwelle wie RBAC und Paketinstallation — einem Global-Grant.
    /// </summary>
    [Fact]
    public async Task Every_operations_endpoint_demands_a_global_grant()
    {
        var (_, key) = await _fixture.SeedIdentityAsync(
            "ops-ohne-global",
            [new Grant(new PermissionScope(new ServerId(Guid.NewGuid()), null), [ToolAction.UseTool])]);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var doctor = await client.GetAsync("/api/v1/operations/doctor", TestContext.Current.CancellationToken);
        doctor.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var backup = await client.PostAsJsonAsync(
            "/api/v1/operations/backup",
            new { targetPath = "/tmp/darf-nicht.zip" },
            TestContext.Current.CancellationToken);
        backup.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var export = await client.PostAsJsonAsync(
            "/api/v1/operations/config/export",
            new { includeSecrets = false },
            TestContext.Current.CancellationToken);
        export.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_section_name_is_refused_instead_of_silently_dropped()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/operations/backup",
            new { targetPath = "/tmp/egal.zip", sections = UnknownSection },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("usage");
    }

    [Fact]
    public async Task The_configuration_export_carries_no_secret_values()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/operations/config/export",
            new { includeSecrets = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var export = await response.Content.ReadFromJsonAsync<ConfigurationExport>(
            Json, TestContext.Current.CancellationToken);
        export!.ContainsSecrets.Should().BeFalse();
        export.Payload.Should().NotBeNullOrEmpty();
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, key) = await _fixture.SeedAdminAsync("ops-admin-" + Guid.NewGuid().ToString("N")[..8]);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
}

/// <summary>
/// ADR-0024 E7 — „Vor schemaändernden Migrationen entsteht bei SQLite automatisch ein Backup."
/// <para>
/// Bis WP2.7 war der Haken vorbereitet und unbesetzt: <see cref="IPreMigrationBackup"/> existierte,
/// war aber nirgends registriert, und der Start migrierte mit einer Warnung ohne Sicherung. Dieser
/// Test ist die Stelle, an der das auffällt, wenn die Verdrahtung wieder verschwindet.
/// </para>
/// </summary>
public class PreMigrationBackupTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _fixture;

    public PreMigrationBackupTests(GatewayFixture fixture) => _fixture = fixture;

    [Fact]
    public void A_sqlite_start_refuses_to_migrate_without_a_backup()
    {
        var safety = _fixture.Services.GetRequiredService<MigrationSafetyOptions>();
        safety.PreMigrationBackup.Should().Be(PreMigrationBackupRequirement.Always);
        _fixture.Services.GetService<IPreMigrationBackup>().Should().NotBeNull();
    }

    /// <summary>
    /// <b>BFR-F-0056 — hier stand bis zum 2026-08-05 das Gegenteil.</b>
    /// <para>
    /// Der Test hielt fest, dass der Erststart ein <c>pre-migration-*.zip</c> hinterlässt, mit der
    /// Begründung: Eine frische Datenbank anzulegen sei auch eine schemaändernde Migration. Das
    /// stimmt — nur schützt eine Sicherung einen <em>vorherigen</em> Zustand, und den gibt es hier
    /// nicht. Übrig blieb ein Archiv, das den DataProtection-Key-Ring unverschlüsselt enthält
    /// (ADR-0024 E3) und nichts schützt; der Start meldete es zusätzlich als Warnung.
    /// </para>
    /// <para>
    /// Dass der Haken überhaupt feuert, hält jetzt
    /// <c>MigrationSafetyTests.Pre_migration_backup_hook_is_called_before_the_migration</c> fest —
    /// dort an einer Datenbank, die schon da war.
    /// </para>
    /// </summary>
    [Fact]
    public void The_first_start_leaves_no_pre_migration_archive_behind()
    {
        var options = _fixture.Services.GetRequiredService<BackupOptions>();

        var archives = Directory.Exists(options.PreBackupDirectory)
            ? Directory.GetFiles(options.PreBackupDirectory, "pre-migration-*.zip")
            : [];

        archives.Should().BeEmpty(
            "eine Vorsicherung ohne Vorzustand schuetzt nichts und ist selbst ein Geheimnis");
    }
}
