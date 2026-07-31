using System.Globalization;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Bifrost.Persistence.Startup;

/// <summary>Ein gehaltener Migrationslock. Wird am Ende des Startpfads freigegeben.</summary>
public interface IMigrationLease : IAsyncDisposable
{
    /// <summary>Wie der Lock zustande kam — für Log und Diagnose. Enthält keine Verbindungsangaben.</summary>
    string Description { get; }
}

/// <summary>
/// Der Migrationslock aus ADR-0024 E7: „Genau eine Instanz migriert."
///
/// <para>
/// <b>PostgreSQL — Advisory Lock.</b> Eine eigene Verbindung nimmt
/// <c>pg_try_advisory_lock(<see cref="PostgresAdvisoryKey"/>)</c> in einer Warteschleife. Der Lock
/// hängt an der <i>Sitzung</i>, nicht an einer Transaktion: Stirbt der Prozess, beendet PostgreSQL
/// die Sitzung und gibt den Lock frei — es kann also kein verwaister Lock zurückbleiben.
/// <i>Grenzen:</i> Er gilt nur innerhalb derselben Datenbank desselben Clusters und nur für
/// Prozesse, die diesen Weg gehen — ein von Hand gestartetes <c>dotnet ef database update</c> oder
/// ein Fremdwerkzeug läuft daran vorbei. Eine hängende, aber lebende Sitzung hält ihn beliebig
/// lange; der Wartende bricht dann nach <see cref="MigrationSafetyOptions.LockTimeout"/> ab, statt
/// ihn zu stehlen.
/// </para>
///
/// <para>
/// <b>SQLite — Dateilock plus Transaktion.</b> Neben der Datenbankdatei entsteht
/// <c>&lt;datei&gt;.migrate-lock</c>, die exklusiv geöffnet wird (<c>FileShare.None</c>); ein zweiter
/// Prozess bekommt sie nicht und wartet. Das Betriebssystem gibt das Handle beim Prozessende frei,
/// also gibt es auch hier keinen verwaisten Lock. Die zweite Hälfte — die Transaktion — steckt in
/// <see cref="MigrationJournal"/>: Journaleinträge laufen als <c>BEGIN IMMEDIATE</c> und nehmen damit
/// den Schreib-Lock der Datenbank selbst.
/// <i>Grenzen:</i> Ein Dateilock ist nur auf demselben Rechner und demselben lokalen Dateisystem
/// verlässlich. Über NFS, SMB oder ein Container-Volume, das mehrere Hosts gleichzeitig einhängen,
/// hält er <b>nicht</b> — dort ist SQLite ohnehin kein tragfähiger Betriebsmodus. Liegt die
/// Datenbank im Arbeitsspeicher (<c>:memory:</c> oder <c>Mode=Memory</c>), gibt es keine Datei; dann
/// genügt ein prozessinterner Riegel, weil eine solche Datenbank den Prozess nicht verlässt.
/// </para>
/// </summary>
public static class MigrationLock
{
    /// <summary>
    /// Schlüssel des PostgreSQL-Advisory-Locks. Fest und dokumentiert, damit ein Betreiber ihn in
    /// <c>pg_locks</c> wiederfindet: die ASCII-Bytes <c>BIFROST</c> gefolgt von <c>0x01</c>.
    /// </summary>
    public const long PostgresAdvisoryKey = 0x424946524F535401L;

    /// <summary>Endung der SQLite-Locksperrdatei, angehängt an den Pfad der Datenbankdatei.</summary>
    public const string SqliteLockFileSuffix = ".migrate-lock";

    private static readonly SemaphoreSlim InProcessFallback = new(1, 1);

    /// <summary>
    /// Erwirbt den Lock oder bricht mit <see cref="MigrationDiagnosticCodes.LockNotAcquired"/> ab.
    /// Der Abbruch ist der erklärbare Fall: Eine andere Instanz migriert gerade.
    /// </summary>
    public static async Task<IMigrationLease> AcquireAsync(
        BifrostDbContext db, MigrationSafetyOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        var provider = BifrostDbOptions.DetectProvider(db.Database);
        return BifrostDbOptions.IsPostgres(provider)
            ? await AcquirePostgresAsync(db, options, ct).ConfigureAwait(false)
            : await AcquireSqliteAsync(db, options, ct).ConfigureAwait(false);
    }

    /// <summary>Pfad der SQLite-Datenbankdatei, oder null bei einer Datenbank im Arbeitsspeicher.</summary>
    public static string? ResolveSqliteFile(BifrostDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (!BifrostDbOptions.IsSqlite(BifrostDbOptions.DetectProvider(db.Database)))
        {
            return null;
        }

        var connectionString = ResolveConnectionString(db);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode is SqliteOpenMode.Memory
            || string.IsNullOrWhiteSpace(builder.DataSource)
            || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(builder.DataSource);
    }

    /// <summary>
    /// Die <b>konfigurierte</b> Verbindungszeichenfolge, nicht die der offenen Verbindung.
    /// <para>
    /// Der Unterschied ist keine Feinheit: Npgsql entfernt das Passwort aus
    /// <c>NpgsqlConnection.ConnectionString</c>, sobald die Verbindung offen war
    /// (<c>Persist Security Info=false</c> ist die Vorgabe). Eine daraus gebaute zweite Sitzung
    /// scheitert an der Anmeldung — und der Migrationslock wäre auf PostgreSQL nie zu bekommen.
    /// </para>
    /// </summary>
    private static string? ResolveConnectionString(BifrostDbContext db)
        => db.Database.GetConnectionString() ?? db.Database.GetDbConnection().ConnectionString;

    private static async Task<IMigrationLease> AcquireSqliteAsync(
        BifrostDbContext db, MigrationSafetyOptions options, CancellationToken ct)
    {
        var databaseFile = ResolveSqliteFile(db);
        if (databaseFile is null)
        {
            // Datenbank im Arbeitsspeicher: Sie existiert nur in diesem Prozess, also ist ein
            // prozessinterner Riegel exakt der richtige Geltungsbereich — nicht weniger.
            if (!await InProcessFallback.WaitAsync(options.LockTimeout, ct).ConfigureAwait(false))
            {
                throw LockTimeoutError("in-process", options.LockTimeout);
            }

            return new SemaphoreLease();
        }

        var lockFile = databaseFile + SqliteLockFileSuffix;
        var directory = Path.GetDirectoryName(lockFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var deadline = DateTimeOffset.UtcNow + options.LockTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                await WriteHolderAsync(stream, ct).ConfigureAwait(false);
                return new FileLease(stream, lockFile);
            }
            catch (IOException)
            {
                // Hält ein anderer. Erwartet, kein Fehler — deshalb nur warten.
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new DatabaseInitializationException(
                    MigrationDiagnosticCodes.SafetyMechanismUnavailable,
                    $"Die Sperrdatei '{lockFile}' ist nicht beschreibbar; ohne Migrationslock wird nicht migriert.",
                    "Schreibrechte auf dem Datenverzeichnis herstellen und den Start wiederholen.",
                    new Dictionary<string, string> { ["lockFile"] = lockFile },
                    ex);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw LockTimeoutError(lockFile, options.LockTimeout);
            }

            await Task.Delay(options.LockPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task<IMigrationLease> AcquirePostgresAsync(
        BifrostDbContext db, MigrationSafetyOptions options, CancellationToken ct)
    {
        // Bewusst eine EIGENE Verbindung: Der Advisory Lock hängt an der Sitzung, und eine an den
        // Pool zurückgegebene Verbindung wird von Npgsql mit DISCARD ALL geleert — der Lock wäre
        // mitten in der Migration still weg.
        var connectionString = ResolveConnectionString(db);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new DatabaseInitializationException(
                MigrationDiagnosticCodes.SafetyMechanismUnavailable,
                "Ohne Verbindungszeichenfolge lässt sich keine zweite Sitzung für den Advisory Lock öffnen.",
                "Den DbContext über eine Verbindungszeichenfolge konfigurieren statt über eine fertige Verbindung.");
        }

        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + options.LockTimeout;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT pg_try_advisory_lock(@key)";
                    command.Parameters.AddWithValue("key", PostgresAdvisoryKey);
                    if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true)
                    {
                        return new AdvisoryLease(connection);
                    }
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw LockTimeoutError(
                        string.Create(CultureInfo.InvariantCulture, $"pg_advisory_lock({PostgresAdvisoryKey})"),
                        options.LockTimeout);
                }

                await Task.Delay(options.LockPollInterval, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteHolderAsync(FileStream stream, CancellationToken ct)
    {
        // Best effort: Der Inhalt entscheidet nichts, er erklärt nur, wer gerade dran war.
        try
        {
            stream.SetLength(0);
            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"{MigrationJournal.CurrentOrigin} {DateTimeOffset.UtcNow:O}\n");
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Der Lock hängt am Handle, nicht am Inhalt.
        }
    }

    private static DatabaseInitializationException LockTimeoutError(string holder, TimeSpan timeout)
        => new(
            MigrationDiagnosticCodes.LockNotAcquired,
            $"Der Migrationslock war nach {timeout.TotalSeconds:0.#} s nicht zu bekommen — eine andere Instanz migriert oder hängt.",
            "Warten, bis die migrierende Instanz fertig ist. Bleibt der Lock stehen, prüfen, "
            + "ob noch ein Prozess auf dieser Datenbank läuft, und ihn beenden — der Lock löst sich mit ihm.",
            new Dictionary<string, string>
            {
                ["mechanism"] = holder,
                ["timeoutSeconds"] = timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture),
            });

    private sealed class FileLease : IMigrationLease
    {
        private readonly FileStream _stream;

        public FileLease(FileStream stream, string path)
        {
            _stream = stream;
            Description = $"Dateilock {path}";
        }

        public string Description { get; }

        public async ValueTask DisposeAsync() => await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class AdvisoryLease : IMigrationLease
    {
        private readonly NpgsqlConnection _connection;

        public AdvisoryLease(NpgsqlConnection connection) => _connection = connection;

        public string Description => string.Create(
            CultureInfo.InvariantCulture, $"pg_advisory_lock({PostgresAdvisoryKey})");

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = _connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@key)";
                command.Parameters.AddWithValue("key", PostgresAdvisoryKey);
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // Verbindung schon weg: Dann ist auch der Lock weg — genau das ist die gewollte
                // Eigenschaft eines sitzungsgebundenen Locks.
            }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class SemaphoreLease : IMigrationLease
    {
        public string Description => "prozessinterner Riegel (SQLite im Arbeitsspeicher)";

        public ValueTask DisposeAsync()
        {
            InProcessFallback.Release();
            return ValueTask.CompletedTask;
        }
    }
}
