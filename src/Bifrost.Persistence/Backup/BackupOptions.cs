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

    /// <summary>Key-Ring-Verzeichnis; Vorgabe <c>&lt;DataDirectory&gt;/keys</c>.</summary>
    public string? KeyRingDirectory { get; init; }

    /// <summary>Paketverzeichnis; Vorgabe <c>&lt;DataDirectory&gt;/packages</c>.</summary>
    public string? PackagesDirectory { get; init; }

    /// <summary>Instanzkonfiguration; Vorgabe <c>&lt;DataDirectory&gt;/config/instance.json</c>.</summary>
    public string? InstanceConfigPath { get; init; }

    public string ProductVersion { get; init; } = BifrostProductInfo.Version;

    public string MinimumRestoreVersion { get; init; } = BackupLayout.DefaultMinimumRestoreVersion;

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
