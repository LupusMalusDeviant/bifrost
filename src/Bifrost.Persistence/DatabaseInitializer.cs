using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Persistence;

/// <summary>Was der Initializer beim Start vorgefunden und getan hat — für Logging und Tests.</summary>
public enum DatabaseInitOutcome
{
    /// <summary>Leere/neue Datenbank: Schema komplett über Migrationen angelegt.</summary>
    CreatedFromMigrations = 0,

    /// <summary>Bestehende v1.0-Datenbank (per EnsureCreated erzeugt, ohne Migrationshistorie): Baseline gestempelt.</summary>
    BaselinedLegacySchema = 1,

    /// <summary>Bereits migrationsverwaltet: ausstehende Migrationen angewendet (ggf. keine).</summary>
    Migrated = 2,
}

/// <summary>
/// Schema-Initialisierung ab v1.1 über EF-Migrationen statt <c>EnsureCreated</c>.
///
/// Der heikle Fall ist das Upgrade: v1.0-Datenbanken wurden per <c>EnsureCreated</c> erzeugt und
/// besitzen daher **keine** <c>__EFMigrationsHistory</c>. Ein blindes <c>Migrate()</c> würde dort
/// CREATE TABLE auf bereits existierende Tabellen fahren und scheitern. Deshalb wird ein solches
/// Alt-Schema erkannt und die Initial-Migration als "bereits angewendet" gestempelt (Baseline),
/// bevor migriert wird — die Daten bleiben unangetastet.
/// </summary>
public sealed partial class DatabaseInitializer
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IDbContextFactory<BifrostDbContext> factory,
        ILogger<DatabaseInitializer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _logger = logger ?? NullLogger<DatabaseInitializer>.Instance;
    }

    public async Task<DatabaseInitOutcome> InitializeAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var history = db.GetService<IHistoryRepository>();
        var creator = db.GetService<IRelationalDatabaseCreator>();

        // Erkennungslogik bewusst provider-neutral:
        //  * Die bloße Existenz der __EFMigrationsHistory taugt NICHT als Kriterium — Npgsql legt sie
        //    (anders als SQLite) auch ohne angewendete Migration an. Maßgeblich ist, ob tatsächlich
        //    Migrationen als angewendet eingetragen sind.
        //  * HasTables() taugt ebenfalls nicht, weil es die Historie-Tabelle selbst mitzählt. Daher
        //    wird gezielt auf eine unserer Fachtabellen geprüft.
        // Reihenfolge zählt: erst DB-Existenz, dann Inhalt (Npgsql legt keine Datenbank implizit an).
        var outcome = DatabaseInitOutcome.Migrated;
        if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
        {
            outcome = DatabaseInitOutcome.CreatedFromMigrations;
        }
        else if (!await HasAppliedMigrationsAsync(db, ct).ConfigureAwait(false))
        {
            if (await HasApplicationSchemaAsync(db, ct).ConfigureAwait(false))
            {
                await StampBaselineAsync(db, history, ct).ConfigureAwait(false);
                outcome = DatabaseInitOutcome.BaselinedLegacySchema;
            }
            else
            {
                outcome = DatabaseInitOutcome.CreatedFromMigrations;
            }
        }

        await db.Database.MigrateAsync(ct).ConfigureAwait(false);

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).Count();
        Log.Initialized(_logger, outcome, applied);
        return outcome;
    }

    /// <summary>Gibt es bereits eingetragene (angewendete) Migrationen? Fehlt die Historie-Tabelle, gilt das als "nein".</summary>
    private static async Task<bool> HasAppliedMigrationsAsync(BifrostDbContext db, CancellationToken ct)
    {
        try
        {
            return (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Steht bereits ein Fach-Schema (also eine unserer Tabellen)? Bewusst über eine echte Abfrage statt
    /// über <c>HasTables()</c>, weil letzteres die Migrationshistorie mitzählen würde.
    /// </summary>
    private static async Task<bool> HasApplicationSchemaAsync(BifrostDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Identities.AnyAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Trägt die Initial-Migration als angewendet ein, ohne ihr DDL auszuführen (Historie-Tabelle wird
    /// angelegt, falls sie fehlt). Die SQL-Skripte stammen aus EF selbst (kein Fremdeingabe-Pfad).
    /// </summary>
    private async Task StampBaselineAsync(BifrostDbContext db, IHistoryRepository history, CancellationToken ct)
    {
        var baseline = db.Database.GetMigrations().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Keine Migrationen in der Provider-Migrations-Assembly gefunden — ist sie referenziert?");

        var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "10.0.0";

        Log.BaseliningLegacySchema(_logger, baseline);

        // Bewusst das idempotente Create-Skript statt einer ExistsAsync()-Abfrage: EF cacht das
        // Exists-Ergebnis pro Repository-Instanz, wodurch die Prüfung nach vorherigen Aufrufen
        // veraltet sein kann (auf Npgsql beobachtet). "IF NOT EXISTS" ist unabhängig davon korrekt.
#pragma warning disable EF1002 // Skripte kommen aus EF, nicht aus Nutzereingaben
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct).ConfigureAwait(false);
        await db.Database
            .ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(baseline, productVersion)), ct)
            .ConfigureAwait(false);
#pragma warning restore EF1002
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Datenbank initialisiert ({Outcome}), {AppliedCount} Migration(en) angewendet.")]
        public static partial void Initialized(ILogger logger, DatabaseInitOutcome outcome, int appliedCount);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Bestehendes v1.0-Schema ohne Migrationshistorie erkannt — Migration {Baseline} wird als Baseline gestempelt (kein DDL, Daten bleiben erhalten).")]
        public static partial void BaseliningLegacySchema(ILogger logger, string baseline);
    }
}
