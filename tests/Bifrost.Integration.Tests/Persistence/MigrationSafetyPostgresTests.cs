using AwesomeAssertions;

using Bifrost.Persistence;
using Bifrost.Persistence.Startup;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Testcontainers.PostgreSql;

using Xunit;

namespace Bifrost.Integration.Tests.Persistence;

/// <summary>
/// Das PostgreSQL-Gegenstück zu <see cref="MigrationSafetyTests"/>: derselbe Vertrag, anderes
/// Lock-Verfahren (<c>pg_try_advisory_lock</c> statt Dateilock). Ohne erreichbaren Docker-Daemon
/// wird übersprungen — dieselbe Regel wie in <see cref="PostgresPersistenceTests"/>, inklusive
/// <c>BIFROST_REQUIRE_POSTGRES</c>.
/// </summary>
public sealed class MigrationSafetyPostgresTests : IAsyncLifetime
{
    private static bool PostgresRequired =>
        Environment.GetEnvironmentVariable("BIFROST_REQUIRE_POSTGRES") is "1" or "true";

    private PostgreSqlContainer? _container;
    private string? _unavailableReason;

    public async ValueTask InitializeAsync()
    {
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
    }

    [Fact]
    public async Task Empty_database_is_initialised_and_journalled()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = await NewDatabaseAsync("leer");

        (await new DatabaseInitializer(factory).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.CreatedFromMigrations);

        await using var db = await factory.CreateDbContextAsync(ct);
        var journal = await MigrationJournal.ReadAllAsync(db, ct);
        journal.Should().ContainSingle();
        journal[0].State.Should().Be(MigrationRunState.Completed);
    }

    [Fact]
    public async Task Two_parallel_starts_migrate_exactly_once()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = await NewDatabaseAsync("parallel");

        var options = new MigrationSafetyOptions
        {
            LockTimeout = TimeSpan.FromSeconds(60),
            LockPollInterval = TimeSpan.FromMilliseconds(20),
        };

        var outcomes = await Task.WhenAll(
            Task.Run(() => new DatabaseInitializer(factory, options: options).InitializeAsync(ct), ct),
            Task.Run(() => new DatabaseInitializer(factory, options: options).InitializeAsync(ct), ct));

        outcomes.Count(o => o is DatabaseInitOutcome.CreatedFromMigrations).Should().Be(1);
        outcomes.Count(o => o is DatabaseInitOutcome.Migrated).Should().Be(1);

        await using var db = await factory.CreateDbContextAsync(ct);
        (await MigrationJournal.ReadAllAsync(db, ct))
            .Should().ContainSingle("der Advisory Lock lässt genau eine Instanz migrieren");
        (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Start_against_a_held_advisory_lock_fails_explainably()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (factory, connectionString) = await NewDatabaseAsync("gehalten");

        // Der Lock wird von einer fremden Sitzung gehalten — das Bild einer migrierenden Instanz.
        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync(ct);
        await using (var command = holder.CreateCommand())
        {
            command.CommandText = "SELECT pg_advisory_lock(@key)";
            command.Parameters.AddWithValue("key", MigrationLock.PostgresAdvisoryKey);
            await command.ExecuteScalarAsync(ct);
        }

        var initializer = new DatabaseInitializer(factory, options: new MigrationSafetyOptions
        {
            LockTimeout = TimeSpan.FromMilliseconds(500),
            LockPollInterval = TimeSpan.FromMilliseconds(50),
        });

        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => initializer.InitializeAsync(ct));

        error.Code.Should().Be("BFR-DB-0100");
        error.SafeDetails["mechanism"].Should().Contain("pg_advisory_lock");
    }

    [Fact]
    public async Task Interrupted_migration_is_detected_and_write_access_is_refused()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (factory, _) = await NewDatabaseAsync("abbruch");

        var aborting = new DatabaseInitializer(factory, options: new MigrationSafetyOptions
        {
            Failpoint = (point, _) => point is MigrationFailpoint.BeforeMigrate
                ? throw new MigrationAbortSimulationException()
                : Task.CompletedTask,
        });

        await Assert.ThrowsAsync<MigrationAbortSimulationException>(() => aborting.InitializeAsync(ct));

        var error = await Assert.ThrowsAsync<DatabaseInitializationException>(
            () => new DatabaseInitializer(factory).InitializeAsync(ct));
        error.Code.Should().Be("BFR-DB-0101");

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            (await MigrationJournal.ClearUnfinishedAsync(db, ct)).Should().Be(1);
        }

        (await new DatabaseInitializer(factory).InitializeAsync(ct))
            .Should().Be(DatabaseInitOutcome.CreatedFromMigrations);
    }

    [Fact]
    public async Task Newer_unknown_schema_is_refused_instead_of_downgraded()
    {
        MarkSkippedIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var (factory, connectionString) = await NewDatabaseAsync("neuer");

        await new DatabaseInitializer(factory).InitializeAsync(ct);

        await using (var connection = new NpgsqlConnection(connectionString))
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
    }

    private async Task<(IDbContextFactory<BifrostDbContext> Factory, string ConnectionString)> NewDatabaseAsync(
        string name)
    {
        var dbName = $"bifrost_ms_{name}_{Guid.NewGuid():N}"[..40].ToLowerInvariant();

        await using (var admin = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = dbName,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Postgres, connectionString)
            .Options;

        return (new TestDbContextFactory(options), connectionString);
    }

    private void MarkSkippedIfUnavailable()
        => Assert.SkipWhen(_unavailableReason is not null, _unavailableReason ?? string.Empty);
}
