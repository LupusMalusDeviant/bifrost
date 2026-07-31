using Bifrost.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Bifrost.Integration.Tests.Persistence;

/// <summary>
/// Identische Suite gegen echtes PostgreSQL (ADR-0007: Zwei-Provider-Disziplin per CI).
/// Ohne erreichbaren Docker-Daemon (z. B. Windows-CI-Runner mit Windows-Containern)
/// werden die Tests übersprungen — der Ubuntu-CI-Lauf trägt den Postgres-Nachweis.
///
/// Damit dieser Nachweis nicht still ausfallen kann, erzwingt <c>BIFROST_REQUIRE_POSTGRES=1</c>
/// (in CI auf Linux gesetzt) das Durchreichen jedes Fehlers: dort ist Docker vorhanden, ein
/// Fehlschlag ist also ein echter Defekt und kein Umgebungsproblem. Ohne die Unterscheidung
/// würde ein kaputtes Image oder ein Migrationsfehler als grüner Skip durchgehen.
/// </summary>
public sealed class PostgresPersistenceTests : PersistenceTestsBase
{
    private static bool PostgresRequired =>
        Environment.GetEnvironmentVariable("BIFROST_REQUIRE_POSTGRES") is "1" or "true";

    private PostgreSqlContainer? _container;
    private string? _unavailableReason;

    protected override async Task<DbContextOptions<BifrostDbContext>?> CreateOptionsAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            return new DbContextOptionsBuilder<BifrostDbContext>()
                .UseBifrostDatabase(BifrostDbOptions.Postgres, _container.GetConnectionString())
                .Options;
        }
        catch (Exception ex) when (!PostgresRequired)
        {
            _unavailableReason = $"PostgreSQL-Testcontainer nicht startbar: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Legt für Migrations-/Upgrade-Tests eine zusätzliche, garantiert leere Datenbank im selben
    /// Container an. Bewusst direkt über Npgsql statt ExecScriptAsync: CREATE DATABASE darf nicht in
    /// einer Transaktion laufen, und ein fehlgeschlagenes Skript bliebe hier sonst unbemerkt.
    /// </summary>
    protected override async Task<DbContextOptions<BifrostDbContext>> CreateFreshOptionsAsync(string name)
    {
        var dbName = $"bifrost_{name}_{Guid.NewGuid():N}"[..40].ToLowerInvariant();

        await using (var admin = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = dbName,
        }.ConnectionString;

        return new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Postgres, connectionString)
            .Options;
    }

    protected override void MarkSkippedIfUnavailable()
        => Assert.SkipWhen(_unavailableReason is not null, _unavailableReason ?? string.Empty);

    protected override string InitialCreateMigration => "20260718092006_InitialCreate";

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}
