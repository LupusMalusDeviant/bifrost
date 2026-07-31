using AwesomeAssertions;

using Bifrost.Persistence;
using Bifrost.Persistence.Startup;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Bifrost.Integration.Tests.Persistence;

/// <summary>
/// WP2.3: Migrationssicherheit und Startkoordination (ADR-0024 E7).
///
/// <para>
/// Gefahren wird gegen SQLite, weil dort beide Hälften des Verfahrens sichtbar werden — Dateilock
/// und Journal — und weil SQLite der Zero-Setup-Default ist. Das PostgreSQL-Gegenstück
/// (<c>pg_try_advisory_lock</c>) steht in <see cref="MigrationLock"/> und läuft in der
/// Provider-Suite mit; ein echter Mehrknotenbetrieb ist hier nicht prüfbar (M2-Vertrag §7).
/// </para>
/// </summary>
public sealed class MigrationSafetyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"bifrost-migsafe-{Guid.NewGuid():N}");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen ist kein Testergebnis.
        }

        return ValueTask.CompletedTask;
    }

    // ── Pflichttest 1: leere Datenbank ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_database_is_initialised_and_journalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = NewDatabase("leer");

        var outcome = await new DatabaseInitializer(factory).InitializeAsync(ct);

        outcome.Should().Be(DatabaseInitOutcome.CreatedFromMigrations);

        await using var db = await factory.CreateDbContextAsync(ct);
        (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
        (await db.Identities.CountAsync(ct)).Should().Be(0, "das Schema ist nutzbar");

        var journal = await MigrationJournal.ReadAllAsync(db, ct);
        journal.Should().ContainSingle("genau ein Migrationslauf hat stattgefunden");
        journal[0].State.Should().Be(MigrationRunState.Completed);
        journal[0].FromMigration.Should().BeNull("vorher war nichts angewendet");
        journal[0].FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Second_start_without_pending_migrations_writes_no_journal_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = NewDatabase("idempotent");

        await new DatabaseInitializer(factory).InitializeAsync(ct);
        var second = await new DatabaseInitializer(factory).InitializeAsync(ct);

        second.Should().Be(DatabaseInitOutcome.Migrated, "Initialisierung ist idempotent");

        await using var db = await factory.CreateDbContextAsync(ct);
        (await MigrationJournal.ReadAllAsync(db, ct))
            .Should().ContainSingle("ohne ausstehende Migration wird nichts vermerkt");
    }

    // ── Pflichttest 2: zwei parallele Starts ────────────────────────────────────────────────────

    [Fact]
    public async Task Two_parallel_starts_migrate_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = NewDatabase("parallel");

        var options = new MigrationSafetyOptions
        {
            LockTimeout = TimeSpan.FromSeconds(30),
            LockPollInterval = TimeSpan.FromMilliseconds(20),
        };

        var first = Task.Run(() => new DatabaseInitializer(factory, options: options).InitializeAsync(ct), ct);
        var second = Task.Run(() => new DatabaseInitializer(factory, options: options).InitializeAsync(ct), ct);

        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(o => o is DatabaseInitOutcome.CreatedFromMigrations)
            .Should().Be(1, "genau eine Instanz legt das Schema an");
        outcomes.Count(o => o is DatabaseInitOutcome.Migrated)
            .Should().Be(1, "die andere findet nichts mehr zu tun — sie hat gewartet, nicht mitmigriert");

        await using var db = await factory.CreateDbContextAsync(ct);
        var journal = await MigrationJournal.ReadAllAsync(db, ct);
        journal.Should().ContainSingle("das Journal belegt genau EINEN Migrationslauf");
        journal[0].State.Should().Be(MigrationRunState.Completed);
        (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Start_against_a_held_lock_fails_explainably()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, path) = NewDatabase("gehalten");

        // Der Lock wird von außen gehalten — genau das, was eine zweite Instanz vorfindet.
        var lockFile = path + MigrationLock.SqliteLockFileSuffix;
        await using var holder = new FileStream(
            lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var initializer = new DatabaseInitializer(factory, options: new MigrationSafetyOptions
        {
            LockTimeout = TimeSpan.FromMilliseconds(300),
            LockPollInterval = TimeSpan.FromMilliseconds(50),
        });

        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => initializer.InitializeAsync(ct));

        error.Code.Should().Be("BFR-DB-0100");
        error.Remediation.Should().NotBeNullOrWhiteSpace("ein Abbruch ohne nächste Handlung ist kein Abbruch, sondern ein Rätsel");
        error.SafeDetails.Should().ContainKey("mechanism");
        error.ToCheck().Status.Should().Be(Bifrost.Abstractions.Operations.CheckStatus.Fail);
    }

    // ── Pflichttest 3: Abbruch mitten in der Migration ──────────────────────────────────────────

    [Fact]
    public async Task Interrupted_migration_is_detected_and_write_access_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = NewDatabase("abbruch");

        // Failpoint NACH dem Journaleintrag, VOR der Migration: stellt den verschwundenen Prozess
        // nach, der nichts mehr aufräumt.
        var aborting = new DatabaseInitializer(factory, options: new MigrationSafetyOptions
        {
            Failpoint = (point, _) => point is MigrationFailpoint.BeforeMigrate
                ? throw new MigrationAbortSimulationException()
                : Task.CompletedTask,
        });

        await Assert.ThrowsAsync<MigrationAbortSimulationException>(() => aborting.InitializeAsync(ct));

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var journal = await MigrationJournal.ReadAllAsync(db, ct);
            journal.Should().ContainSingle();
            journal[0].State.Should().Be(MigrationRunState.Started, "der Eintrag bleibt offen stehen");
        }

        // Der nächste Start erkennt den halben Zustand und verweigert — ohne Reparaturversuch.
        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => new DatabaseInitializer(factory).InitializeAsync(ct));

        error.Code.Should().Be("BFR-DB-0101");
        error.Remediation.Should().Contain("wiederherstellen");
        error.SafeDetails.Should().ContainKey("journalEntry");

        // Und er verweigert weiter, solange niemand hinschaut: kein stiller Selbstheilungsversuch.
        (await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => new DatabaseInitializer(factory).InitializeAsync(ct))).Code.Should().Be("BFR-DB-0101");

        // Der Ausweg ist eine ausdrückliche Handlung des Betreibers, kein Automatismus.
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            (await MigrationJournal.ClearUnfinishedAsync(db, ct)).Should().Be(1);
        }

        (await new DatabaseInitializer(factory).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.CreatedFromMigrations, "nach dem bewussten Lösen läuft der Start normal");
    }

    [Fact]
    public async Task Failed_migration_is_journalled_and_blocks_the_next_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, path) = NewDatabase("gescheitert");

        // Eine Migration zum Scheitern bringen, ohne EF zu manipulieren: Die Zieltabelle der
        // Initial-Migration steht bereits, aber ohne Migrationshistorie UND ohne die Fachtabelle,
        // an der die Baseline-Erkennung hängt. CREATE TABLE läuft dann auf ein vorhandenes Objekt.
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"Roles\" (\"Id\" TEXT NOT NULL PRIMARY KEY)";
            await command.ExecuteNonQueryAsync(ct);
        }

        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => new DatabaseInitializer(factory).InitializeAsync(ct));
        error.Code.Should().Be("BFR-DB-0103");

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var journal = await MigrationJournal.ReadAllAsync(db, ct);
            journal.Should().ContainSingle();
            journal[0].State.Should().Be(MigrationRunState.Failed);
            journal[0].Failure.Should().NotBeNullOrWhiteSpace();
        }

        // Ein gescheiterter Lauf ist kein Zustand, über den der nächste Start hinweggeht.
        (await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => new DatabaseInitializer(factory).InitializeAsync(ct))).Code.Should().Be("BFR-DB-0101");
    }

    // ── Pflichttest 4: neueres, unbekanntes Schema ──────────────────────────────────────────────

    [Fact]
    public async Task Newer_unknown_schema_is_refused_instead_of_downgraded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, path) = NewDatabase("neuer");

        await new DatabaseInitializer(factory).InitializeAsync(ct);

        // Eine Migration eintragen, die dieser Build nicht kennt — das ist das Bild einer Datenbank,
        // die von einer neueren Version angefasst wurde.
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") "
                + "VALUES ('99991231235959_ZukunftsSchema', '99.0.0')";
            await command.ExecuteNonQueryAsync(ct);
        }

        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => new DatabaseInitializer(factory).InitializeAsync(ct));

        error.Code.Should().Be("BFR-DB-0102");
        error.SafeDetails["unknownMigrations"].Should().Contain("ZukunftsSchema");
        error.Remediation.Should().Contain("Downgrade");

        // Die Diagnose meldet denselben Befund, ohne den Start zu provozieren.
        var checks = await new DatabaseInitializer(factory).InspectAsync(ct);
        checks.Should().Contain(c => c.Code == "BFR-DB-0102"
            && c.Status == Bifrost.Abstractions.Operations.CheckStatus.Fail);
    }

    // ── Vor-Migrationsbackup: der Haken für WP2.1 ───────────────────────────────────────────────

    [Fact]
    public async Task Pre_migration_backup_hook_is_called_before_the_migration()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, path) = NewDatabase("hook");

        var hook = new RecordingBackup(Path.Combine(_directory, "vorher.zip"));
        var initializer = new DatabaseInitializer(factory, preMigrationBackup: hook,
            options: new MigrationSafetyOptions { PreMigrationBackup = PreMigrationBackupRequirement.Always });

        await initializer.InitializeAsync(ct);

        hook.Calls.Should().ContainSingle("gesichert wird genau einmal, unter dem Lock");
        hook.Calls[0].Provider.Should().Be(BifrostDbOptions.Sqlite);
        hook.Calls[0].DatabaseFilePath.Should().Be(Path.GetFullPath(path));
        hook.Calls[0].CurrentMigrationId.Should().BeNull();
        hook.Calls[0].PendingMigrationIds.Should().NotBeEmpty();

        await using var db = await factory.CreateDbContextAsync(ct);
        var journal = await MigrationJournal.ReadAllAsync(db, ct);
        journal[0].BackupPath.Should().Be(hook.ArchivePath, "das Journal sagt, wohin der Rückweg führt");
    }

    [Fact]
    public async Task Required_pre_migration_backup_without_a_service_stops_the_start()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = NewDatabase("kein-hook");

        var initializer = new DatabaseInitializer(factory,
            options: new MigrationSafetyOptions { PreMigrationBackup = PreMigrationBackupRequirement.Always });

        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => initializer.InitializeAsync(ct));

        error.Code.Should().Be("BFR-DB-0104");
    }

    [Fact]
    public async Task Without_a_backup_service_the_default_start_still_runs()
    {
        // Solange WP2.1 nicht verdrahtet ist, darf die fehlende Sicherung den Start nicht blockieren
        // — sie wird protokolliert. Dieser Test hält fest, dass die Zusage aus E7 damit VORBEREITET
        // und nicht erfüllt ist.
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = NewDatabase("ohne-hook");

        (await new DatabaseInitializer(factory).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.CreatedFromMigrations);
    }

    // ── Diagnose ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inspect_reports_pending_and_up_to_date_without_touching_the_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = NewDatabase("inspect");
        var initializer = new DatabaseInitializer(factory);

        var before = await initializer.InspectAsync(ct);
        before.Should().ContainSingle();
        before[0].Code.Should().Be("BFR-DB-0112", "vor dem ersten Start gibt es nichts zu beurteilen");

        await initializer.InitializeAsync(ct);

        var after = await initializer.InspectAsync(ct);
        after.Should().Contain(c => c.Code == "BFR-DB-0110"
            && c.Status == Bifrost.Abstractions.Operations.CheckStatus.Pass);
    }

    private (IDbContextFactory<BifrostDbContext> Factory, string Path) NewDatabase(string name)
    {
        var path = Path.Combine(_directory, $"{name}.db");
        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}")
            .Options;
        return (new TestDbContextFactory(options), path);
    }

    private sealed class RecordingBackup : IPreMigrationBackup
    {
        public RecordingBackup(string archivePath) => ArchivePath = archivePath;

        public string ArchivePath { get; }

        public List<PreMigrationBackupContext> Calls { get; } = [];

        public Task<PreMigrationBackupOutcome> CreateAsync(
            PreMigrationBackupContext context, CancellationToken ct)
        {
            Calls.Add(context);
            return Task.FromResult(new PreMigrationBackupOutcome(Created: true, ArchivePath));
        }
    }
}
