using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Persistence;
using Bifrost.Persistence.Backup;
using Bifrost.Integration.Tests.Persistence;
using Bifrost.Persistence.Startup;
using Bifrost.Server.Operations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Testcontainers.PostgreSql;

using Xunit;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// ADR-0024 E7 auf PostgreSQL: <b>Vor einer schemaändernden Migration entsteht eine Sicherung.</b>
///
/// <para>
/// Bis <c>pg_dump</c> umgesetzt war, entstand dort keine — der Start warnte und migrierte trotzdem,
/// jedes Upgrade lief ohne Rückweg. Diese Suite prüft beides, was sich daran geändert hat: dass die
/// Sicherung wirklich entsteht <b>und</b> dass die Verdrahtung sie verlangt.
/// </para>
///
/// <para>
/// <b>Die Verdrahtungsprüfung braucht keinen Container</b> und läuft deshalb immer. Sie ist die
/// wichtigere von beiden: <c>Always</c> heißt „ohne Sicherung keine Migration", und ein
/// <c>Always</c> auf einer Instanz ohne die Werkzeuge wäre kein Schutz, sondern ein Startverbot.
/// </para>
/// </summary>
public sealed class PostgresPreMigrationBackupWiringTests
{
    /// <summary>
    /// Die Entscheidung aus <c>OperationsRegistration</c>, an ihrem einzigen sichtbaren Ergebnis
    /// abgelesen. Bewusst <b>ohne</b> feste Erwartung, sondern gegen die tatsächliche
    /// Werkzeuglage: Der Test läuft auf Rechnern mit und ohne Clientpaket, und beide Male soll er
    /// dieselbe Regel belegen.
    /// </summary>
    [Fact]
    public void Postgres_demands_a_pre_migration_backup_exactly_when_the_tools_are_there()
    {
        var services = new ServiceCollection().AddBifrostOperations(
            Path.Combine(Path.GetTempPath(), $"bifrost-e7-{Guid.NewGuid():N}"),
            BifrostDbOptions.Postgres,
            "Host=127.0.0.1;Port=5432;Database=bifrost;Username=u;Password=p");

        var options = services.BuildServiceProvider().GetRequiredService<MigrationSafetyOptions>();

        var toolsPresent = PostgresTools.TryLocate(out _);
        options.PreMigrationBackup.Should().Be(
            toolsPresent ? PreMigrationBackupRequirement.Always : PreMigrationBackupRequirement.WhenAvailable,
            toolsPresent
                ? "mit pg_dump ist die Zusage aus E7 einlösbar — also wird sie verlangt"
                : "ohne pg_dump wäre 'Always' ein Startverbot statt eines Schutzes");
    }

    /// <summary>
    /// Die Gegenprobe auf der SQLite-Seite: Dort hängt die Entscheidung an der Datei und nicht an
    /// einem fremden Programm. Ohne diese Zeile könnte die Regel oben auch „immer WhenAvailable"
    /// heißen und würde trotzdem grün.
    /// </summary>
    [Fact]
    public void Sqlite_with_a_file_behind_it_always_demands_a_pre_migration_backup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bifrost-e7-sqlite-{Guid.NewGuid():N}");
        var services = new ServiceCollection().AddBifrostOperations(
            directory, BifrostDbOptions.Sqlite, $"Data Source={Path.Combine(directory, "bifrost.db")}");

        services.BuildServiceProvider().GetRequiredService<MigrationSafetyOptions>()
            .PreMigrationBackup.Should().Be(PreMigrationBackupRequirement.Always);
    }
}

/// <summary>
/// Und derselbe Haken gegen einen echten Server: Die Sicherung entsteht, und sie ist ein gültiges
/// Archiv. Eine Datei, die nur so heißt, wäre kein Rückweg.
/// <para>
/// Übersprungen ohne Docker oder ohne <c>pg_dump</c>; mit <c>BIFROST_REQUIRE_POSTGRES=1</c> ist
/// beides ein Fehlschlag.
/// </para>
/// </summary>
public sealed class PostgresPreMigrationBackupTests : IAsyncLifetime
{
    private static bool PostgresRequired =>
        Environment.GetEnvironmentVariable("BIFROST_REQUIRE_POSTGRES") is "1" or "true";

    private PostgreSqlContainer? _container;
    private string? _unavailableReason;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"bifrost-e7-pg-{Guid.NewGuid():N}");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        if (!PostgresTools.TryLocate(out _))
        {
            if (PostgresRequired)
            {
                throw new InvalidOperationException(PostgresTools.MissingMessage);
            }

            _unavailableReason = "pg_dump/pg_restore sind hier nicht erreichbar; "
                + "die Vor-Migrationssicherung auf PostgreSQL ist damit nicht prüfbar.";
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (!PostgresRequired)
        {
            _unavailableReason = $"PostgreSQL-Testcontainer nicht startbar: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_pre_migration_backup_is_created_and_is_a_valid_archive()
    {
        Assert.SkipWhen(_unavailableReason is not null, _unavailableReason ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

        var dataDirectory = Path.Combine(_root, "instanz");
        Directory.CreateDirectory(dataDirectory);

        var connectionString = await NewDatabaseAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Postgres, connectionString)
            .Options);

        // Ein bestehendes Schema: So gibt es beim nächsten Lauf etwas zu sichern, das nicht leer ist.
        await new DatabaseInitializer(factory).InitializeAsync(ct);

        var options = new BackupOptions
        {
            DataDirectory = dataDirectory,
            Provider = DatabaseProvider.Postgres,
            PostgresConnectionString = connectionString,
        };

        var backups = new BackupService(options);
        var service = new PreMigrationBackupService(
            backups,
            options,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<PreMigrationBackupService>.Instance);

        var outcome = await service.CreateAsync(
            new PreMigrationBackupContext(
                BifrostDbOptions.Postgres,
                DatabaseFilePath: null,
                CurrentMigrationId: null,
                PendingMigrationIds: ["20990101000000_Irgendetwas"]),
            ct);

        outcome.Created.Should().BeTrue(
            "seit ADR-0024 E2 umgesetzt ist, gibt es auf PostgreSQL einen Rückweg. Grund für ein "
            + "Nein wäre: {0}", outcome.SkipReason ?? "(keiner genannt)");
        outcome.ArchivePath.Should().NotBeNull();
        File.Exists(outcome.ArchivePath!).Should().BeTrue();

        var inspection = await backups.InspectAsync(outcome.ArchivePath!, null, ct);
        inspection.Valid.Should().BeTrue(
            "eine Sicherung, die kein gültiges Archiv ist, ist kein Rückweg. Befunde: {0}",
            string.Join(" | ", inspection.Problems));
        inspection.Manifest!.Provider.Should().Be(DatabaseProvider.Postgres);
        inspection.Manifest.Sections.Should().HaveFlag(BackupSections.Database);
    }

    /// <summary>
    /// Die Gegenprobe: Ohne erreichbares <c>pg_dump</c> meldet der Dienst ein <b>Nein mit
    /// Begründung</b> — und baut sich keinen Ersatzweg (ADR-0024 E2).
    /// </summary>
    [Fact]
    public async Task Without_pg_dump_the_hook_says_no_instead_of_inventing_a_backup()
    {
        var ct = TestContext.Current.CancellationToken;

        var dataDirectory = Path.Combine(_root, "ohne-werkzeug");
        var emptyToolDirectory = Path.Combine(dataDirectory, "leer");
        Directory.CreateDirectory(emptyToolDirectory);

        var options = new BackupOptions
        {
            DataDirectory = dataDirectory,
            Provider = DatabaseProvider.Postgres,
            PostgresConnectionString = "Host=127.0.0.1;Port=1;Database=bifrost;Username=u;Password=p",
            PostgresToolDirectory = emptyToolDirectory,
        };

        var service = new PreMigrationBackupService(
            new BackupService(options),
            options,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<PreMigrationBackupService>.Instance);

        var outcome = await service.CreateAsync(
            new PreMigrationBackupContext(
                BifrostDbOptions.Postgres, null, null, ["20990101000000_Irgendetwas"]),
            ct);

        outcome.Created.Should().BeFalse();
        outcome.ArchivePath.Should().BeNull("es gibt kein Archiv, also wird auch keines genannt");
        outcome.SkipReason.Should().Contain("pg_dump");
        Directory.Exists(Path.Combine(dataDirectory, "backups")).Should().BeFalse();
    }

    private async Task<string> NewDatabaseAsync()
    {
        var dbName = $"bifrost_e7_{Guid.NewGuid():N}"[..40].ToLowerInvariant();

        await using (var admin = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = dbName,
        }.ConnectionString;
    }
}
