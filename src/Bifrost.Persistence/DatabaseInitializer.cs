using Bifrost.Abstractions.Operations;
using Bifrost.Persistence.Startup;

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
/// Schema-Initialisierung ab v1.1 über EF-Migrationen statt <c>EnsureCreated</c>, ab M2 zusätzlich
/// exklusiv, diagnostizierbar und recoveryfähig (ADR-0024 E7).
///
/// <para>
/// Der heikle Fall ist das Upgrade: v1.0-Datenbanken wurden per <c>EnsureCreated</c> erzeugt und
/// besitzen daher <b>keine</b> <c>__EFMigrationsHistory</c>. Ein blindes <c>Migrate()</c> würde dort
/// CREATE TABLE auf bereits existierende Tabellen fahren und scheitern. Deshalb wird ein solches
/// Alt-Schema erkannt und die Initial-Migration als „bereits angewendet" gestempelt (Baseline),
/// bevor migriert wird — die Daten bleiben unangetastet.
/// </para>
///
/// <para>
/// <b>Startkoordination (M2, WP2.3).</b> Der Ablauf ist:
/// <list type="number">
///   <item>Zustand vor dem Lock feststellen; bei PostgreSQL die Datenbank anlegen, falls sie fehlt
///   (ein Advisory Lock braucht eine Datenbank, in der er genommen werden kann).</item>
///   <item><see cref="MigrationLock"/> erwerben — <b>genau eine Instanz migriert</b>, alle anderen
///   warten oder brechen mit <c>BFR-DB-0100</c> ab.</item>
///   <item>Unter dem Lock: Journal lesen. Ein offener Eintrag heißt, ein früherer Lauf ist
///   abgebrochen — dann wird der Betrieb <b>verweigert</b> (<c>BFR-DB-0101</c>) und
///   <b>nichts repariert</b>.</item>
///   <item>Angewendete Migrationen, die dieser Build nicht kennt, heißen: neueres Schema. Abbruch
///   mit <c>BFR-DB-0102</c> statt eines Downgrade-Versuchs.</item>
///   <item>Erst dann Backup-Hook, Journaleintrag, Migration, Journalabschluss.</item>
/// </list>
/// </para>
/// </summary>
public sealed partial class DatabaseInitializer
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly IPreMigrationBackup? _preMigrationBackup;
    private readonly MigrationSafetyOptions _options;

    public DatabaseInitializer(
        IDbContextFactory<BifrostDbContext> factory,
        ILogger<DatabaseInitializer>? logger = null,
        IPreMigrationBackup? preMigrationBackup = null,
        MigrationSafetyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _logger = logger ?? NullLogger<DatabaseInitializer>.Instance;
        _preMigrationBackup = preMigrationBackup;
        _options = options ?? new MigrationSafetyOptions();
    }

    /// <summary>
    /// Stellt das Schema her. Wirft <see cref="DatabaseInitializationException"/>, wenn der Zustand
    /// unklar ist — der Dienst kommt dann gar nicht erst hoch, und genau das ist die „Verweigerung
    /// des Schreibbetriebs" aus ADR-0024 E7.
    /// </summary>
    public async Task<DatabaseInitOutcome> InitializeAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var provider = BifrostDbOptions.DetectProvider(db.Database);
        var creator = db.GetService<IRelationalDatabaseCreator>();

        // Reihenfolge zählt: Die Existenzprüfung muss laufen, BEVOR irgendetwas die Verbindung
        // öffnet — bei SQLite legt schon das Öffnen die Datei an.
        var existedBefore = await creator.ExistsAsync(ct).ConfigureAwait(false);

        if (!existedBefore && BifrostDbOptions.IsPostgres(provider))
        {
            // Der Advisory Lock lebt IN einer Datenbank; ohne sie gibt es nichts zu sperren. Zwei
            // Instanzen können hier gleichzeitig ankommen — die Verliererin bekommt „existiert
            // bereits" und darf einfach weitermachen.
            try
            {
                await creator.CreateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.DatabaseCreationRaced(_logger, ex.Message);
            }
        }

        await Failpoint(MigrationFailpoint.BeforeLock, ct).ConfigureAwait(false);

        await using var lease = await MigrationLock.AcquireAsync(db, _options, ct).ConfigureAwait(false);
        Log.LockAcquired(_logger, lease.Description);

        await MigrationJournal.EnsureTableAsync(db, ct).ConfigureAwait(false);

        var unfinished = await MigrationJournal.FindUnfinishedAsync(db, ct).ConfigureAwait(false);
        if (unfinished is not null)
        {
            throw InterruptedError(unfinished);
        }

        var applied = await GetAppliedSafeAsync(db, ct).ConfigureAwait(false);
        var known = db.Database.GetMigrations().ToList();

        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw NewerSchemaError(unknown, known);
        }

        var outcome = DatabaseInitOutcome.Migrated;
        if (applied.Count == 0)
        {
            if (existedBefore && await HasApplicationSchemaAsync(db, ct).ConfigureAwait(false))
            {
                await StampBaselineAsync(db, db.GetService<IHistoryRepository>(), ct).ConfigureAwait(false);
                applied = await GetAppliedSafeAsync(db, ct).ConfigureAwait(false);
                outcome = DatabaseInitOutcome.BaselinedLegacySchema;
            }
            else
            {
                outcome = DatabaseInitOutcome.CreatedFromMigrations;
            }
        }

        // Bewusst ohne DB-Rundreise berechnet: „ausstehend" ist definitionsgemäß die Differenz
        // zwischen der Migrations-Assembly und der Historie, und beides liegt schon vor.
        var pending = known.Except(applied, StringComparer.Ordinal).ToList();
        if (pending.Count > 0)
        {
            await MigrateUnderLockAsync(db, provider, applied, pending, ct).ConfigureAwait(false);
        }

        Log.Initialized(_logger, outcome, applied.Count + pending.Count);
        return outcome;
    }

    /// <summary>
    /// Beurteilt den Zustand, <b>ohne</b> ihn zu ändern: keine Migration, kein Lock, kein Journaleintrag.
    /// Damit kann die Diagnose (WP2.4) und ein Recovery-Werkzeug dieselben Codes melden, die der
    /// Start werfen würde — ohne den Start zu provozieren.
    /// </summary>
    public async Task<IReadOnlyList<DiagnosticCheck>> InspectAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var creator = db.GetService<IRelationalDatabaseCreator>();

        if (!await creator.ExistsAsync(ct).ConfigureAwait(false))
        {
            return
            [
                new DiagnosticCheck(
                    MigrationDiagnosticCodes.DatabaseAbsent,
                    CheckStatus.Skipped,
                    "Die Datenbank existiert noch nicht — sie entsteht beim ersten Start aus den Migrationen.",
                    "Kein Handlungsbedarf."),
            ];
        }

        var checks = new List<DiagnosticCheck>();

        MigrationJournalEntry? unfinished = null;
        try
        {
            await MigrationJournal.EnsureTableAsync(db, ct).ConfigureAwait(false);
            unfinished = await MigrationJournal.FindUnfinishedAsync(db, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(new DiagnosticCheck(
                MigrationDiagnosticCodes.SafetyMechanismUnavailable,
                CheckStatus.Fail,
                "Das Migrationsjournal ist nicht lesbar.",
                "Schreibrechte und Zustand der Datenbank prüfen.",
                new Dictionary<string, string> { ["error"] = ex.GetType().Name }));
        }

        if (unfinished is not null)
        {
            checks.Add(InterruptedError(unfinished).ToCheck());
        }

        var applied = await GetAppliedSafeAsync(db, ct).ConfigureAwait(false);
        var known = db.Database.GetMigrations().ToList();

        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            checks.Add(NewerSchemaError(unknown, known).ToCheck());
            return checks;
        }

        var pending = known.Except(applied, StringComparer.Ordinal).ToList();
        checks.Add(pending.Count > 0
            ? new DiagnosticCheck(
                MigrationDiagnosticCodes.SchemaPending,
                CheckStatus.Warning,
                $"{pending.Count} Migration(en) stehen aus.",
                "Der nächste Start wendet sie an. Vorher eine Sicherung anlegen.",
                new Dictionary<string, string> { ["pending"] = string.Join(", ", pending) })
            : new DiagnosticCheck(
                MigrationDiagnosticCodes.SchemaUpToDate,
                CheckStatus.Pass,
                "Das Schema ist auf dem Stand dieses Builds.",
                null,
                new Dictionary<string, string> { ["applied"] = applied.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        return checks;
    }

    private async Task MigrateUnderLockAsync(
        BifrostDbContext db,
        string provider,
        List<string> applied,
        List<string> pending,
        CancellationToken ct)
    {
        var current = applied.Count > 0 ? applied[^1] : null;
        var backupPath = await RunPreMigrationBackupAsync(db, provider, current, pending, ct).ConfigureAwait(false);

        // Der Eintrag steht auf der Platte, BEVOR die erste Migration läuft. Verschwindet der
        // Prozess dazwischen, findet der nächste Start genau ihn — das ist der ganze Zweck.
        var journalId = await MigrationJournal
            .BeginAsync(db, current, pending[^1], pending.Count, backupPath, ct)
            .ConfigureAwait(false);
        Log.MigrationStarted(_logger, pending.Count, pending[^1]);

        // Ein MigrationAbortSimulationException an dieser Stelle lässt das Journal absichtlich offen: Er
        // stellt den verschwundenen Prozess nach, und der räumt auch nichts auf.
        await Failpoint(MigrationFailpoint.BeforeMigrate, ct).ConfigureAwait(false);

        try
        {
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MigrationAbortSimulationException)
        {
            await MigrationJournal.FailAsync(db, journalId, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw new DatabaseInitializationException(
                MigrationDiagnosticCodes.MigrationFailed,
                $"Die Migration nach '{pending[^1]}' ist gescheitert; der Schemazustand ist unklar.",
                backupPath is null
                    ? "Die Datenbank aus der letzten Sicherung wiederherstellen und den Start wiederholen. "
                      + "Erst danach den Journaleintrag lösen."
                    : $"Die Datenbank aus '{backupPath}' wiederherstellen und den Start wiederholen.",
                new Dictionary<string, string>
                {
                    ["from"] = current ?? "(leer)",
                    ["to"] = pending[^1],
                    ["journalEntry"] = journalId,
                },
                ex);
        }

        await Failpoint(MigrationFailpoint.AfterMigrate, ct).ConfigureAwait(false);
        await MigrationJournal.CompleteAsync(db, journalId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ruft den Vor-Migrationsbackup-Hook, wenn einer verdrahtet ist (ADR-0024 E7). Ob eine fehlende
    /// Sicherung den Start abbricht, entscheidet <see cref="MigrationSafetyOptions.PreMigrationBackup"/>
    /// — nicht der Backupdienst und nicht diese Methode.
    /// </summary>
    private async Task<string?> RunPreMigrationBackupAsync(
        BifrostDbContext db,
        string provider,
        string? current,
        List<string> pending,
        CancellationToken ct)
    {
        if (_options.PreMigrationBackup is PreMigrationBackupRequirement.Never)
        {
            return null;
        }

        var required = _options.PreMigrationBackup is PreMigrationBackupRequirement.Always;

        if (_preMigrationBackup is null)
        {
            if (required)
            {
                throw NoBackupError(
                    "Vor einer schemaändernden Migration ist eine Sicherung verlangt, aber kein Backupdienst registriert.",
                    "Einen IPreMigrationBackup registrieren oder die Anforderung bewusst auf 'WhenAvailable' senken.");
            }

            Log.PreMigrationBackupUnavailable(_logger, pending.Count);
            return null;
        }

        var context = new PreMigrationBackupContext(
            provider, MigrationLock.ResolveSqliteFile(db), current, pending);

        PreMigrationBackupOutcome outcome;
        try
        {
            outcome = await _preMigrationBackup.CreateAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (required)
            {
                throw NoBackupError(
                    "Das Vor-Migrationsbackup ist gescheitert; es wird nicht ohne Sicherung migriert.",
                    "Ursache im Backupdienst beheben und den Start wiederholen.",
                    ex);
            }

            Log.PreMigrationBackupFailed(_logger, ex.Message);
            return null;
        }

        if (!outcome.Created)
        {
            if (required)
            {
                throw NoBackupError(
                    $"Der Backupdienst hat keine Sicherung erzeugt: {outcome.SkipReason ?? "ohne Angabe"}.",
                    "Ursache im Backupdienst beheben und den Start wiederholen.");
            }

            Log.PreMigrationBackupSkipped(_logger, outcome.SkipReason ?? "ohne Angabe");
            return null;
        }

        Log.PreMigrationBackupCreated(_logger, outcome.ArchivePath ?? "(ohne Pfad)");
        return outcome.ArchivePath;
    }

    private Task Failpoint(MigrationFailpoint point, CancellationToken ct)
        => _options.Failpoint is null ? Task.CompletedTask : _options.Failpoint(point, ct);

    private static DatabaseInitializationException InterruptedError(MigrationJournalEntry entry)
        => new(
            MigrationDiagnosticCodes.InterruptedMigration,
            $"Ein früherer Migrationslauf ({entry.FromMigration ?? "(leer)"} → {entry.ToMigration ?? "?"}) "
            + $"ist nicht abgeschlossen worden ({(entry.State is MigrationRunState.Failed ? "gescheitert" : "abgebrochen")}). "
            + "Der Schemazustand ist unbekannt; der Schreibbetrieb wird verweigert.",
            entry.BackupPath is null
                ? "Die Datenbank aus einer Sicherung wiederherstellen. Ist der Zustand geprüft und in Ordnung, "
                  + $"den offenen Eintrag der Tabelle {MigrationJournal.TableName} ausdrücklich lösen — "
                  + "dieser Start repariert von sich aus nichts."
                : $"Die Datenbank aus '{entry.BackupPath}' wiederherstellen und den Start wiederholen.",
            new Dictionary<string, string>
            {
                ["journalEntry"] = entry.Id,
                ["state"] = entry.State.ToString(),
                ["startedAt"] = entry.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["origin"] = entry.Origin,
                ["from"] = entry.FromMigration ?? "(leer)",
                ["to"] = entry.ToMigration ?? "(unbekannt)",
                ["backupPath"] = entry.BackupPath ?? "(keine Sicherung vermerkt)",
                ["failure"] = entry.Failure ?? "(kein Fehlertext — der Prozess ist verschwunden)",
            });

    private static DatabaseInitializationException NewerSchemaError(
        List<string> unknown, List<string> known)
        => new(
            MigrationDiagnosticCodes.UnknownNewerSchema,
            $"Die Datenbank trägt {unknown.Count} Migration(en), die dieser Stand nicht kennt — "
            + "sie stammt aus einer neueren Version.",
            "Diese Instanz auf die Version anheben, die das Schema erzeugt hat. Ein Downgrade wird "
            + "nicht versucht: Ein neueres Schema mit alten Regeln zu bedienen, fällt später und "
            + "woanders auf (ADR-0024 E6).",
            new Dictionary<string, string>
            {
                ["unknownMigrations"] = string.Join(", ", unknown),
                ["newestKnownMigration"] = known.Count > 0 ? known[^1] : "(keine)",
            });

    private static DatabaseInitializationException NoBackupError(
        string message, string remediation, Exception? inner = null)
        => new(
            MigrationDiagnosticCodes.PreMigrationBackupMissing, message, remediation, null, inner);

    /// <summary>
    /// Die bereits eingetragenen (angewendeten) Migrationen. Fehlt die Historie-Tabelle, gilt das als
    /// „keine" — und nicht als Fehler, weil genau das der Normalfall einer leeren Datenbank ist.
    /// </summary>
    private static async Task<List<string>> GetAppliedSafeAsync(BifrostDbContext db, CancellationToken ct)
    {
        try
        {
            return (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        }
        catch (Exception)
        {
            return [];
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

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Migrationslock gehalten: {Mechanism}.")]
        public static partial void LockAcquired(ILogger logger, string mechanism);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Migration beginnt: {PendingCount} ausstehend, Ziel {Target}.")]
        public static partial void MigrationStarted(ILogger logger, int pendingCount, string target);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Anlegen der Datenbank ist parallel schon passiert: {Reason}")]
        public static partial void DatabaseCreationRaced(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Kein Backupdienst verdrahtet — {PendingCount} Migration(en) laufen OHNE Vor-Migrationsbackup (ADR-0024 E7).")]
        public static partial void PreMigrationBackupUnavailable(ILogger logger, int pendingCount);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Vor-Migrationsbackup gescheitert, es wird ohne Sicherung migriert: {Reason}")]
        public static partial void PreMigrationBackupFailed(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Vor-Migrationsbackup ausgelassen: {Reason}")]
        public static partial void PreMigrationBackupSkipped(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Vor-Migrationsbackup erstellt: {ArchivePath}")]
        public static partial void PreMigrationBackupCreated(ILogger logger, string archivePath);
    }
}
