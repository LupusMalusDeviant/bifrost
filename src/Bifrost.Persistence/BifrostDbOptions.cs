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

    /// <param name="sqliteJournalMode">
    /// Nur für SQLite. Vorgabe ist <see cref="SqliteJournalMode.Wal"/>; der Rückweg
    /// <see cref="SqliteJournalMode.Delete"/> ist über <c>BIFROST_SQLITE_JOURNAL_MODE</c>
    /// einstellbar. Bei PostgreSQL ohne Wirkung.
    /// </param>
    public static DbContextOptionsBuilder UseBifrostDatabase(
        this DbContextOptionsBuilder builder,
        string? provider,
        string connectionString,
        SqliteJournalMode sqliteJournalMode = SqliteJournalMode.Wal)
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
            // Ohne diese beiden PRAGMAs sperrt SQLite, sobald die Instanz gleichzeitig ihr eigenes
            // Audit schreibt — siehe SqliteConnectionSetupInterceptor. Sie hängen hier und nicht an
            // der Verdrahtung des Hosts, damit Laufzeit und Tests dieselbe Datenbank sehen.
            builder.AddInterceptors(new SqliteConnectionSetupInterceptor(sqliteJournalMode));
        }

        return builder;
    }

    /// <summary>Generische Überladung, damit <c>DbContextOptionsBuilder&lt;BifrostDbContext&gt;</c> seinen Typ behält.</summary>
    public static DbContextOptionsBuilder<TContext> UseBifrostDatabase<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string? provider,
        string connectionString,
        SqliteJournalMode sqliteJournalMode = SqliteJournalMode.Wal)
        where TContext : DbContext
    {
        UseBifrostDatabase((DbContextOptionsBuilder)builder, provider, connectionString, sqliteJournalMode);
        return builder;
    }

    /// <summary>
    /// Liest den Wert von <c>BIFROST_SQLITE_JOURNAL_MODE</c>. Ein unbekannter Wert wird
    /// <b>abgewiesen</b> und nicht still auf die Vorgabe zurückgesetzt: Wer <c>walk</c> schreibt und
    /// stillschweigend WAL bekommt, hält seinen Rückweg für eingelegt.
    /// </summary>
    public static SqliteJournalMode ParseJournalMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SqliteJournalMode.Wal;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "WAL" => SqliteJournalMode.Wal,
            "DELETE" => SqliteJournalMode.Delete,
            _ => throw new ArgumentException(
                $"BIFROST_SQLITE_JOURNAL_MODE nennt '{value}' — bekannt sind nur 'wal' (Vorgabe) und "
                + "'delete' (Rückweg auf das klassische Rollback-Journal).",
                nameof(value)),
        };
    }
}
