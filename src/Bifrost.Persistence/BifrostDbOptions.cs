using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Bifrost.Persistence;

/// <summary>
/// Zentrale Provider-Verdrahtung inklusive Migrations-Assembly (v1.1). SQLite und PostgreSQL
/// erzeugen unterschiedliches DDL, daher hat jeder Provider eine eigene Migrations-Assembly
/// (EF-Core-Standardvorgehen). Host und Tests konfigurieren den Context ausschließlich hierüber,
/// damit Laufzeit und Tests denselben Migrationspfad nutzen.
/// </summary>
public static class BifrostDbOptions
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";

    public const string SqliteMigrationsAssembly = "Bifrost.Persistence.Migrations.Sqlite";
    public const string PostgresMigrationsAssembly = "Bifrost.Persistence.Migrations.Postgres";

    public static bool IsPostgres(string? provider)
        => string.Equals(provider, Postgres, StringComparison.OrdinalIgnoreCase);

    public static bool IsSqlite(string? provider)
        => string.Equals(provider, Sqlite, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Welcher Provider hinter einem bereits gebauten Context steht. Die Startkoordination (WP2.3)
    /// braucht das, weil Lock-Verfahren und Journal-Transaktion providerabhängig sind — und weil ein
    /// unbekannter Provider dort nicht stillschweigend als SQLite durchgehen darf.
    /// </summary>
    public static string DetectProvider(DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (database.IsNpgsql())
        {
            return Postgres;
        }

        if (database.IsSqlite())
        {
            return Sqlite;
        }

        throw new NotSupportedException(
            $"Unbekannter EF-Provider '{database.ProviderName}'. Für ihn gibt es kein geprüftes " +
            "Migrations-Lock-Verfahren; ein Lock, der nur manchmal hält, wäre schlimmer als keiner.");
    }

    public static DbContextOptionsBuilder UseBifrostDatabase(
        this DbContextOptionsBuilder builder, string? provider, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (IsPostgres(provider))
        {
            builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(PostgresMigrationsAssembly));
        }
        else
        {
            builder.UseSqlite(connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly));
        }

        return builder;
    }

    /// <summary>Generische Überladung, damit <c>DbContextOptionsBuilder&lt;BifrostDbContext&gt;</c> seinen Typ behält.</summary>
    public static DbContextOptionsBuilder<TContext> UseBifrostDatabase<TContext>(
        this DbContextOptionsBuilder<TContext> builder, string? provider, string connectionString)
        where TContext : DbContext
    {
        UseBifrostDatabase((DbContextOptionsBuilder)builder, provider, connectionString);
        return builder;
    }
}
