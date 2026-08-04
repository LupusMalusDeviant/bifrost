using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bifrost.Persistence;

/// <summary>
/// Das Journalverfahren der SQLite-Datei. <b>Eine Eigenschaft der Datei, nicht der Verbindung</b>:
/// Wer eine bestehende Datenbank einmal mit <see cref="Wal"/> öffnet, hat sie dauerhaft umgestellt.
/// </summary>
public enum SqliteJournalMode
{
    /// <summary>
    /// Write-Ahead-Log — Leser und ein Schreiber behindern sich nicht mehr. Der Vorgabewert seit
    /// v0.7; ADR-0007 hat ihn immer vorausgesetzt („SQLite im WAL-Modus schafft die Ziellast"), der
    /// Code hat ihn nur nie eingeschaltet.
    /// </summary>
    Wal = 0,

    /// <summary>
    /// Das klassische Rollback-Journal (SQLite-Vorgabe). Der ausdrückliche <b>Rückweg</b>: Ein
    /// Betreiber, dessen Datenverzeichnis die WAL-Begleitdateien nicht verträgt — ein Netzlaufwerk
    /// etwa, auf dem SQLite den gemeinsamen Speicher nicht anlegen kann —, stellt hierauf zurück.
    /// Er kauft sich damit den Sperrfehler zurück, den WAL behebt.
    /// </summary>
    Delete = 1,
}

/// <summary>
/// Die zwei Einstellungen, ohne die SQLite unter der Last <b>dieser</b> Anwendung sperrt.
/// <para>
/// Der zweite Schreiber ist die Instanz selbst: Audit-Flush (1 s), Audit-Retention (sofort beim
/// Start) und Fristprüfung der Vorgänge laufen ab dem Startende dauerhaft auf derselben Datei.
/// Trifft eine Schreibung aus der Oberfläche darauf, ist die Datei ohne WAL gesperrt — und das
/// Audit-Ereignis dazu verschwand bis v0.7 still (siehe <see cref="Audit.AuditBatchWriter"/>).
/// </para>
/// <para>
/// <b>Warum ein Interceptor und nicht die Verbindungszeichenfolge:</b> <c>journal_mode</c> und
/// <c>busy_timeout</c> sind PRAGMAs und keine Schlüsselwörter der Verbindungszeichenfolge. Sie
/// müssen auf <i>jeder</i> geöffneten Verbindung gesetzt werden — <c>busy_timeout</c> gilt je
/// Verbindung, und EF Core öffnet über die Factory beliebig viele.
/// </para>
/// </summary>
internal sealed class SqliteConnectionSetupInterceptor : DbConnectionInterceptor
{
    /// <summary>
    /// Wie lange eine Schreibung auf einen gehaltenen Schreib-Lock wartet, bevor sie mit
    /// <c>SQLITE_BUSY</c> aufgibt.
    /// <para>
    /// Ohne diesen Wert ist SQLites Vorgabe <b>0</b>: Der erste Konflikt ist sofort ein Fehler. Die
    /// Zahl ist bewusst endlich — ein unbegrenztes Warten macht aus einem erklärbaren Fehler einen
    /// Hänger, und ein Hänger ist teurer. Fünf Sekunden sind ein Vielfaches jeder hier auftretenden
    /// Schreibdauer (Batch-Insert, ExecuteDelete auf einem Index).
    /// </para>
    /// </summary>
    public const int BusyTimeoutMilliseconds = 5_000;

    private readonly SqliteJournalMode _journalMode;

    public SqliteConnectionSetupInterceptor(SqliteJournalMode journalMode)
        => _journalMode = journalMode;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection, _journalMode);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection, _journalMode);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    /// <summary>
    /// Setzt beide PRAGMAs auf einer offenen Verbindung. <b>Die Reihenfolge ist Absicht:</b> Der
    /// Wechsel des Journalverfahrens braucht kurz eine exklusive Sperre auf der Datei. Stünde
    /// <c>busy_timeout</c> danach, scheiterte ausgerechnet die Umstellung einer Bestandsdatei am
    /// Sperrfehler, den sie beseitigen soll.
    /// </summary>
    internal static void Apply(DbConnection connection, SqliteJournalMode journalMode)
    {
        if (connection is not SqliteConnection sqlite)
        {
            return;
        }

        using (var busy = sqlite.CreateCommand())
        {
            busy.CommandText = string.Create(
                CultureInfo.InvariantCulture, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};");
            busy.ExecuteNonQuery();
        }

        // NUR-LESEND: EF Core öffnet für die blosse Existenzprüfung einer SQLite-Datei eine eigene
        // Verbindung mit 'Mode=ReadOnly' (SqliteDatabaseCreator.Exists). Ein Umstellen des
        // Journalverfahrens ist dort ein Schreibzugriff und scheitert mit
        // 'attempt to write a readonly database' — eine Ausnahme, mit der der Aufrufer an dieser
        // Stelle nicht rechnet und die den Start mit einer irreführenden Meldung abbrechen würde.
        // Die Wartezeit gilt trotzdem: Auch ein Leser wartet lieber, als sofort aufzugeben.
        if (new SqliteConnectionStringBuilder(sqlite.ConnectionString).Mode is SqliteOpenMode.ReadOnly)
        {
            return;
        }

        // ERST LESEN. Der Wert steht in der Datei, nicht in der Verbindung — steht er schon richtig,
        // gibt es nichts umzustellen. Das Lesen ist eine reine Abfrage ohne Sperre; das Setzen
        // dagegen braucht kurz exklusiven Zugriff und kann scheitern. Auf jeder Verbindung blind zu
        // schreiben hiesse: bei jedem Verbindungsaufbau denselben Konflikt riskieren, obwohl die
        // Umstellung genau einmal im Leben einer Datei nötig ist.
        //
        // Auf einer Datenbank im Arbeitsspeicher antwortet SQLite mit 'memory' und lässt sich nicht
        // umstellen — dort gibt es keine Datei, die zwei Prozesse teilen könnten. Der Vergleich
        // unten trifft dann nie zu, und genau deshalb steht hier keine Sonderbehandlung.
        var wanted = journalMode is SqliteJournalMode.Wal ? "wal" : "delete";
        using (var read = sqlite.CreateCommand())
        {
            read.CommandText = "PRAGMA journal_mode;";
            if (read.ExecuteScalar() is string current
                && current.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var journal = sqlite.CreateCommand();
        journal.CommandText = journalMode is SqliteJournalMode.Wal
            ? "PRAGMA journal_mode=WAL;"
            : "PRAGMA journal_mode=DELETE;";
        journal.ExecuteNonQuery();
    }
}
