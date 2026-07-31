using AwesomeAssertions;

using Bifrost.Persistence;
using Bifrost.Persistence.Startup;
using Bifrost.Upgrade.Tests.Harness;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Testcontainers.PostgreSql;

using Xunit;

namespace Bifrost.Upgrade.Tests;

/// <summary>
/// Ein PostgreSQL-Container fuer die gesamte Klasse. Bewusst nicht pro Test: Die Matrix hat so viele
/// Felder wie es Migrationen gibt, und ein Container je Feld waere Wartezeit ohne Erkenntnis.
/// <para>
/// Ohne erreichbaren Docker-Daemon werden die Felder <b>uebersprungen</b> und als uebersprungen
/// gemeldet — nicht als bestanden. Mit <c>BIFROST_REQUIRE_POSTGRES=1</c> ist ein nicht startbarer
/// Container ein Fehlschlag; genau so laeuft die CI.
/// </para>
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private static bool PostgresRequired =>
        Environment.GetEnvironmentVariable("BIFROST_REQUIRE_POSTGRES") is "1" or "true";

    public PostgreSqlContainer? Container { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            Container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await Container.StartAsync();
        }
        catch (Exception ex) when (!PostgresRequired)
        {
            UnavailableReason = $"PostgreSQL-Testcontainer nicht startbar: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Container is not null)
        {
            await Container.DisposeAsync();
        }
    }
}

/// <summary>
/// Dieselbe Matrix auf PostgreSQL. Das eigene DDL des Providers ist der Grund, warum es diese Suite
/// zweimal gibt: SQLite baut Tabellen bei einer Spaltenaenderung neu auf, PostgreSQL aendert sie in
/// place — ein Datenverlust beim Upgrade sieht auf beiden Seiten anders aus.
///
/// <para>
/// Backup und Restore fehlen hier: Sie sind fuer PostgreSQL <b>nicht implementiert</b>
/// (ADR-0024 E2 sieht <c>pg_dump</c> vor, <c>PostgresBackup</c> weist jeden Aufruf ab). Das steht
/// als Luecke in <c>docs/upgrade-matrix.md</c>; ein Test, der die Absage als Erfolg feiert, waere
/// eine Fertigmeldung ohne Deckung — die Absage selbst wird darum in
/// <see cref="BackupRestoreUpgradeTests"/> nur als <i>Absage</i> festgehalten.
/// </para>
/// </summary>
public sealed class PostgresUpgradeMatrixTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    public PostgresUpgradeMatrixTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public static TheoryData<string> PublishedMigrations()
    {
        var data = new TheoryData<string>();
        foreach (var migration in UpgradeHarness.PublishedMigrations(BifrostDbOptions.Postgres))
        {
            data.Add(migration);
        }

        return data;
    }

    // ── Matrixfeld 1: leere Datenbank -> aktueller Stand ────────────────────────────────────────

    [Fact]
    public async Task Empty_database_reaches_the_current_state()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var factory = await NewDatabaseAsync("leer");

        (await new DatabaseInitializer(factory).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.CreatedFromMigrations);

        await using var db = await factory.CreateDbContextAsync(ct);
        (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
        (await db.Database.GetAppliedMigrationsAsync(ct))
            .Should().Equal(UpgradeHarness.PublishedMigrations(BifrostDbOptions.Postgres));
    }

    // ── Matrixfeld 2: jeder veroeffentlichte Stand -> aktueller Stand, mit Bestand ──────────────

    [Theory]
    [MemberData(nameof(PublishedMigrations))]
    public async Task Published_migration_state_upgrades_without_losing_data(string fixtureMigration)
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var published = UpgradeHarness.PublishedMigrations(BifrostDbOptions.Postgres);
        var factory = await NewDatabaseAsync(Shorten(fixtureMigration));

        // Der Schluesselring liegt im Arbeitsverzeichnis des Tests; er gehoert nicht in die
        // Datenbank und reist bei PostgreSQL auch nicht mit ihr.
        var keyDirectory = Path.Combine(
            Path.GetTempPath(), $"bifrost-upgrade-pg-{Guid.NewGuid():N}");
        var protection = UpgradeHarness.KeyRing(keyDirectory);

        try
        {
            await UpgradeHarness.CreateFixtureAsync(factory, fixtureMigration, ct);
            var applied = UpgradeHarness.Through(published, fixtureMigration);
            var payload = await UpgradePayloadWriter.WriteAsync(factory, protection, applied, ct);

            var pendingBefore = new List<string>();
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                pendingBefore.AddRange(await db.Database.GetPendingMigrationsAsync(ct));
                pendingBefore.Should().Equal(
                    published.Skip(applied.Count),
                    "das Fixture steht wirklich auf dem alten Stand");
            }

            (await new DatabaseInitializer(factory).InitializeAsync(ct))
                .Should().Be(DatabaseInitOutcome.Migrated);

            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
                (await db.Database.GetAppliedMigrationsAsync(ct)).Should().Equal(published);

                var journal = await MigrationJournal.ReadAllAsync(db, ct);
                if (pendingBefore.Count > 0)
                {
                    journal.Should().ContainSingle();
                    journal[0].State.Should().Be(MigrationRunState.Completed);
                    journal[0].FromMigration.Should().Be(fixtureMigration);
                }
                else
                {
                    journal.Should().BeEmpty();
                }
            }

            await UpgradePayloadWriter.VerifyAsync(factory, protection, payload, ct);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Aufraeumen ist kein Testergebnis.
            }
        }
    }

    private async Task<IDbContextFactory<BifrostDbContext>> NewDatabaseAsync(string name)
    {
        var sanitised = new string([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var dbName = $"bifrost_up_{sanitised}_{Guid.NewGuid():N}";
        dbName = dbName[..Math.Min(dbName.Length, 60)];

        await using (var admin = new NpgsqlConnection(_fixture.Container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.Container.GetConnectionString())
        {
            Database = dbName,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Postgres, connectionString)
            .Options;

        return new UpgradeDbFactory(options);
    }

    private void MarkSkippedIfUnavailable()
        => Assert.SkipWhen(_fixture.UnavailableReason is not null, _fixture.UnavailableReason ?? string.Empty);

    private static string Shorten(string migrationId)
    {
        var underscore = migrationId.IndexOf('_', StringComparison.Ordinal);
        return underscore < 0 ? migrationId : migrationId[(underscore + 1)..];
    }
}
