using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Wo die zu sichernden Dinge liegen. Bewusst als Datenobjekt und nicht aus der Umgebung gelesen:
/// Dieselbe Klasse sichert im Betrieb das Datenverzeichnis und im Test ein Wegwerfverzeichnis —
/// eine zweite Ableitungslogik wäre eine zweite Fehlerquelle.
/// </summary>
public sealed class BackupOptions
{
    /// <summary>Das Datenverzeichnis der Instanz (<c>BIFROST_DATA_DIR</c>).</summary>
    public required string DataDirectory { get; init; }

    public DatabaseProvider Provider { get; init; } = DatabaseProvider.Sqlite;

    /// <summary>
    /// Die SQLite-Datei. Fehlt sie, gilt <c>bifrost.db</c> im Datenverzeichnis — und ersatzweise die
    /// v1.0-Datei <c>mcpmcp.db</c>, falls nur die existiert (dieselbe Regel wie im Server-Start).
    /// </summary>
    public string? SqliteFilePath { get; init; }

    /// <summary>
    /// Die Verbindungszeichenfolge, wenn <see cref="Provider"/> <see cref="DatabaseProvider.Postgres"/>
    /// ist. <c>pg_dump</c> und <c>pg_restore</c> bekommen Wirt, Port, Benutzer und Datenbank daraus;
    /// das Passwort geht über eine <c>PGPASSFILE</c> und nicht über die Kommandozeile (ADR-0024 E2,
    /// siehe <see cref="PostgresTools"/>).
    /// <para>
    /// Bewusst hier und nicht aus der Umgebung gelesen: Gesichert werden muss <b>die</b> Datenbank,
    /// gegen die der Server tatsächlich läuft. Eine zweite Ableitung wäre eine zweite Fehlerquelle —
    /// und ihr Fehler fiele erst beim Zurückspielen auf.
    /// </para>
    /// </summary>
    public string? PostgresConnectionString { get; init; }

    /// <summary>
    /// Wo <c>pg_dump</c> und <c>pg_restore</c> liegen, falls sie nicht im <c>PATH</c> stehen. Leer
    /// heißt: <c>BIFROST_POSTGRES_BIN</c>, sonst <c>PATH</c>. Ist der Wert gesetzt, wird
    /// <b>ausschließlich</b> dort gesucht (siehe <see cref="PostgresTools.TryLocate(string?, out PostgresToolset?)"/>).
    /// </summary>
    public string? PostgresToolDirectory { get; init; }

    /// <summary>Key-Ring-Verzeichnis; Vorgabe <c>&lt;DataDirectory&gt;/keys</c>.</summary>
    public string? KeyRingDirectory { get; init; }

    /// <summary>Paketverzeichnis; Vorgabe <c>&lt;DataDirectory&gt;/packages</c>.</summary>
    public string? PackagesDirectory { get; init; }

    /// <summary>Instanzkonfiguration; Vorgabe <c>&lt;DataDirectory&gt;/config/instance.json</c>.</summary>
    public string? InstanceConfigPath { get; init; }

    public string ProductVersion { get; init; } = BifrostProductInfo.Version;

    public string MinimumRestoreVersion { get; init; } = BackupLayout.DefaultMinimumRestoreVersion;

    /// <summary>
    /// Die Migrationen, die <b>dieser Build</b> kennt — die Grundlage des Rückwärts-Tors aus
    /// ADR-0024 E6.
    /// <para>
    /// <b>Warum nicht die Versionsangabe:</b> <see cref="MinimumRestoreVersion"/> ist eine Angabe,
    /// die das Archiv <em>über sich selbst</em> macht. Ein Archiv aus einer neueren Version trägt
    /// dieselbe Zahl wie eines von heute, solange niemand sie anhebt — und dann bewacht sie das Tor,
    /// das gerade gegen dieses Archiv schützen soll. Der Migrationsstand dagegen ist eine Tatsache:
    /// Kennt dieser Build ihn nicht, stammt das Archiv aus einer neueren Instanz, ganz ohne
    /// Versionsbuchhaltung.
    /// </para>
    /// <para>
    /// Bleibt die Menge leer, kann das Tor nicht prüfen. Es meldet dann eine <b>Warnung</b> und
    /// nicht etwa nichts — ein Schutz, der still ausfällt, ist schlimmer als keiner.
    /// </para>
    /// </summary>
    public IReadOnlySet<string> KnownMigrationIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public string ResolvedSqliteFile
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SqliteFilePath))
            {
                return SqliteFilePath;
            }

            var current = Path.Combine(DataDirectory, "bifrost.db");
            var legacy = Path.Combine(DataDirectory, "mcpmcp.db");
            return !File.Exists(current) && File.Exists(legacy) ? legacy : current;
        }
    }

    /// <summary>
    /// Die PostgreSQL-Verbindung — oder eine klare Absage. Ein Backupdienst, der auf Postgres
    /// eingestellt ist und keine Verbindung kennt, kann nichts sichern; das jetzt zu sagen ist
    /// besser, als ein leeres Archiv zu erzeugen.
    /// </summary>
    public string RequiredPostgresConnectionString => string.IsNullOrWhiteSpace(PostgresConnectionString)
        ? throw new InvalidOperationException(
            "Für PostgreSQL fehlt die Verbindungszeichenfolge in den Backupoptionen. Ohne sie weiß "
            + "pg_dump nicht, welche Datenbank es sichern soll.")
        : PostgresConnectionString;

    public string ResolvedKeyRingDirectory =>
        string.IsNullOrWhiteSpace(KeyRingDirectory) ? Path.Combine(DataDirectory, "keys") : KeyRingDirectory;

    public string ResolvedPackagesDirectory =>
        string.IsNullOrWhiteSpace(PackagesDirectory) ? Path.Combine(DataDirectory, "packages") : PackagesDirectory;

    public string ResolvedInstanceConfigPath =>
        string.IsNullOrWhiteSpace(InstanceConfigPath)
            ? Path.Combine(DataDirectory, "config", "instance.json")
            : InstanceConfigPath;

    /// <summary>Ablage der Sicherung, die vor einem <c>Replace</c>-Restore entsteht. Liegt bewusst
    /// NEBEN den ersetzten Zonen, damit der Umschaltvorgang sie nicht mitnimmt.</summary>
    public string PreBackupDirectory => Path.Combine(DataDirectory, "backups");
}
