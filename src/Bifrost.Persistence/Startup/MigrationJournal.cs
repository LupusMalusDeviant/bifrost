using System.Data.Common;
using System.Globalization;

using Bifrost.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence.Startup;

/// <summary>Zustand eines Migrationslaufs, wie er im Journal steht.</summary>
public enum MigrationRunState
{
    /// <summary>Begonnen. Steht dieser Zustand beim nächsten Start noch da, ist der Lauf abgebrochen.</summary>
    Started = 0,

    /// <summary>Sauber durchgelaufen.</summary>
    Completed = 1,

    /// <summary>Mit einem Fehler beendet — die Migration hat sich selbst abgebrochen.</summary>
    Failed = 2,
}

/// <summary>Ein Eintrag des Migrationsjournals.</summary>
public sealed record MigrationJournalEntry(
    string Id,
    MigrationRunState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? FromMigration,
    string? ToMigration,
    int PendingCount,
    string ProductVersion,
    string Origin,
    string? BackupPath,
    string? Failure);

/// <summary>
/// Das Migrationsjournal aus ADR-0024 E7: „Der Migrationszustand wird außerhalb flüchtiger Logs
/// vermerkt."
///
/// <para>
/// Es liegt in der Datenbank selbst, in einer eigenen Tabelle <c>__BifrostMigrationJournal</c>. Das
/// ist bewusst so und nicht als Datei neben der Datenbank: Ein Journal auf der lokalen Platte
/// beantwortet die Frage „hat hier schon jemand angefangen?" nur für <b>diesen</b> Rechner. Bei
/// PostgreSQL, wo mehrere Instanzen dieselbe Datenbank benutzen, wäre das die falsche Antwort.
/// </para>
///
/// <para>
/// <b>Die Tabelle gehört nicht zum EF-Modell und entsteht nicht durch eine EF-Migration.</b> Sie wird
/// idempotent per <c>CREATE TABLE IF NOT EXISTS</c> angelegt — genau das Verfahren, mit dem EF seine
/// eigene <c>__EFMigrationsHistory</c> anlegt. Damit bleibt die Regel „keine neuen und keine
/// editierten Migrationen" unangetastet, und das Journal steht auch dann schon, wenn noch gar kein
/// Fachschema existiert.
/// </para>
///
/// <para>
/// Geschrieben wird <b>vor</b> und <b>nach</b> der Migration, jeweils in einer eigenen, sofort
/// abgeschlossenen Transaktion. Der Eintrag darf nicht Teil der Migrationstransaktion sein: Genau
/// deren Rollback soll er überleben.
/// </para>
/// </summary>
public static class MigrationJournal
{
    /// <summary>Name der Journaltabelle. Doppelter Unterstrich wie bei EF: Infrastruktur, keine Fachdaten.</summary>
    public const string TableName = "__BifrostMigrationJournal";

    private const string CreateTableSql = $"""
        CREATE TABLE IF NOT EXISTS "{TableName}" (
            "Id" TEXT NOT NULL PRIMARY KEY,
            "State" TEXT NOT NULL,
            "StartedAtTicks" BIGINT NOT NULL,
            "FinishedAtTicks" BIGINT NULL,
            "FromMigration" TEXT NULL,
            "ToMigration" TEXT NULL,
            "PendingCount" BIGINT NOT NULL,
            "ProductVersion" TEXT NOT NULL,
            "Origin" TEXT NOT NULL,
            "BackupPath" TEXT NULL,
            "Failure" TEXT NULL
        )
        """;

    private const string SelectSql = $"""
        SELECT "Id", "State", "StartedAtTicks", "FinishedAtTicks", "FromMigration", "ToMigration",
               "PendingCount", "ProductVersion", "Origin", "BackupPath", "Failure"
        FROM "{TableName}"
        ORDER BY "StartedAtTicks"
        """;

    /// <summary>Woher der Lauf kam — Rechner und Prozess. Zur Einordnung, nie ein Geheimnis.</summary>
    public static string CurrentOrigin { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"{Environment.MachineName}/{Environment.ProcessId}");

    /// <summary>Legt die Journaltabelle an, falls sie fehlt. Idempotent, kein DDL auf Fachtabellen.</summary>
    public static Task EnsureTableAsync(BifrostDbContext db, CancellationToken ct)
        => ExecuteAsync(db, CreateTableSql, static _ => { }, ct);

    /// <summary>Alle Einträge, älteste zuerst. Für Diagnose, Tests und den Recovery-Hinweis.</summary>
    public static async Task<IReadOnlyList<MigrationJournalEntry>> ReadAllAsync(
        BifrostDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var entries = new List<MigrationJournalEntry>();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = SelectSql;

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entries.Add(Read(reader));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return entries;
    }

    /// <summary>
    /// Der jüngste Eintrag, der nicht sauber abgeschlossen ist — also der Beleg für einen halben
    /// Zustand. <c>null</c> heißt: Es ist nichts offen.
    /// </summary>
    public static async Task<MigrationJournalEntry?> FindUnfinishedAsync(
        BifrostDbContext db, CancellationToken ct)
    {
        var all = await ReadAllAsync(db, ct).ConfigureAwait(false);
        return all.LastOrDefault(e => e.State is not MigrationRunState.Completed);
    }

    /// <summary>
    /// Vermerkt einen beginnenden Lauf und gibt dessen Id zurück. Der Eintrag ist committed, bevor
    /// die erste Migration läuft — sonst könnte ein Abbruch ihn mitnehmen.
    /// </summary>
    public static async Task<string> BeginAsync(
        BifrostDbContext db,
        string? fromMigration,
        string? toMigration,
        int pendingCount,
        string? backupPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var id = Guid.NewGuid().ToString("N");
        const string sql = $"""
            INSERT INTO "{TableName}"
                ("Id", "State", "StartedAtTicks", "FinishedAtTicks", "FromMigration", "ToMigration",
                 "PendingCount", "ProductVersion", "Origin", "BackupPath", "Failure")
            VALUES (@id, @state, @started, NULL, @from, @to, @pending, @product, @origin, @backup, NULL)
            """;

        await ExecuteAsync(db, sql, command =>
        {
            Add(command, "@id", id);
            Add(command, "@state", Name(MigrationRunState.Started));
            Add(command, "@started", DateTimeOffset.UtcNow.UtcTicks);
            Add(command, "@from", fromMigration);
            Add(command, "@to", toMigration);
            Add(command, "@pending", (long)pendingCount);
            Add(command, "@product", BifrostProductInfo.Version);
            Add(command, "@origin", CurrentOrigin);
            Add(command, "@backup", backupPath);
        }, ct).ConfigureAwait(false);

        return id;
    }

    /// <summary>Schließt einen Lauf als erfolgreich ab.</summary>
    public static Task CompleteAsync(BifrostDbContext db, string id, CancellationToken ct)
        => FinishAsync(db, id, MigrationRunState.Completed, failure: null, ct);

    /// <summary>
    /// Schließt einen Lauf als gescheitert ab. Der Eintrag bleibt stehen und verweigert beim nächsten
    /// Start den Betrieb — ein gescheiterter Lauf ist kein Zustand, über den man hinweggeht.
    /// </summary>
    public static Task FailAsync(BifrostDbContext db, string id, string failure, CancellationToken ct)
        => FinishAsync(db, id, MigrationRunState.Failed, failure, ct);

    /// <summary>
    /// Räumt offene Einträge weg. <b>Ausdrücklich kein Teil des Startpfads.</b> Der Initializer ruft
    /// das nie selbst auf — ADR-0024 E7 verlangt Verweigerung, nicht Reparatur. Der Weg existiert für
    /// den Betreiber, der die Datenbank geprüft (oder wiederhergestellt) hat und den Riegel bewusst
    /// löst; der Lead hängt ihn an einen Befehl.
    /// </summary>
    /// <returns>Anzahl der entfernten Einträge.</returns>
    public static async Task<int> ClearUnfinishedAsync(BifrostDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        const string sql = $"""DELETE FROM "{TableName}" WHERE "State" <> @completed""";
        var affected = 0;
        await ExecuteAsync(db, sql, command => Add(command, "@completed", Name(MigrationRunState.Completed)), ct, r => affected = r)
            .ConfigureAwait(false);
        return affected;
    }

    private static Task FinishAsync(
        BifrostDbContext db, string id, MigrationRunState state, string? failure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        const string sql = $"""
            UPDATE "{TableName}"
            SET "State" = @state, "FinishedAtTicks" = @finished, "Failure" = @failure
            WHERE "Id" = @id
            """;

        return ExecuteAsync(db, sql, command =>
        {
            Add(command, "@state", Name(state));
            Add(command, "@finished", DateTimeOffset.UtcNow.UtcTicks);
            Add(command, "@failure", Truncate(failure));
            Add(command, "@id", id);
        }, ct);
    }

    /// <summary>
    /// Führt eine Anweisung in einer eigenen, sofort abgeschlossenen Transaktion aus.
    /// <para>
    /// Bei SQLite läuft sie als <c>BEGIN IMMEDIATE</c>: Der Schreib-Lock der Datenbank wird sofort
    /// genommen statt erst beim ersten Schreibzugriff. Das ist die „Transaktion" aus ADR-0024 E7
    /// neben dem Dateilock — sie stellt sicher, dass zwei Journalschreiber sich nicht überholen und
    /// der Eintrag auf der Platte steht, bevor die Migration beginnt.
    /// </para>
    /// </summary>
    private static async Task ExecuteAsync(
        BifrostDbContext db,
        string sql,
        Action<DbCommand> configure,
        CancellationToken ct,
        Action<int>? onAffected = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var immediate = BifrostDbOptions.IsSqlite(BifrostDbOptions.DetectProvider(db.Database));

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = db.Database.GetDbConnection();

            if (immediate)
            {
                await using var begin = connection.CreateCommand();
                begin.CommandText = "BEGIN IMMEDIATE";
                await begin.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                configure(command);
                var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                onAffected?.Invoke(affected);

                if (immediate)
                {
                    await using var commit = connection.CreateCommand();
                    commit.CommandText = "COMMIT";
                    await commit.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                if (immediate)
                {
                    await using var rollback = connection.CreateCommand();
                    rollback.CommandText = "ROLLBACK";
                    await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static MigrationJournalEntry Read(DbDataReader reader) => new(
        reader.GetString(0),
        Parse(reader.GetString(1)),
        new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
        reader.IsDBNull(3) ? null : new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        (int)reader.GetInt64(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Name(MigrationRunState state) => state switch
    {
        MigrationRunState.Completed => "completed",
        MigrationRunState.Failed => "failed",
        _ => "started",
    };

    private static MigrationRunState Parse(string value) => value switch
    {
        "completed" => MigrationRunState.Completed,
        "failed" => MigrationRunState.Failed,
        _ => MigrationRunState.Started,
    };

    /// <summary>Fehlertexte werden gekappt: Ein Journal ist kein Logspeicher.</summary>
    private static string? Truncate(string? value)
        => value is { Length: > 1000 } ? value[..1000] : value;
}
