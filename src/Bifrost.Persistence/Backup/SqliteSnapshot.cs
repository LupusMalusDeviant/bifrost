using Microsoft.Data.Sqlite;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Der konsistente Datenbankschnappschuss für SQLite (ADR-0024 E2).
/// <para>
/// Verwendet wird die Online-Backup-API (<c>sqlite3_backup</c> hinter
/// <see cref="SqliteConnection.BackupDatabase(SqliteConnection)"/>) und <b>nicht</b> das Kopieren der
/// Datei: Bei aktivem WAL stehen die zuletzt geschriebenen Seiten in der <c>-wal</c>-Datei, nicht in
/// der Hauptdatei. Eine Dateikopie ohne ihre Begleiter ist deshalb still älter als die Datenbank,
/// die man zu sichern glaubt — und man sieht es ihr nicht an.
/// </para>
/// <para>
/// Gegenüber <c>VACUUM INTO</c> hat die Backup-API hier zwei Vorteile: Sie kommt ohne einen Pfad im
/// SQL-Text aus (kein Anführungszeichen-Escaping in einer Anweisung), und sie läuft neben laufenden
/// Lesevorgängen.
/// </para>
/// </summary>
internal static class SqliteSnapshot
{
    public static void Create(string sourceFile, string destinationFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);

        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException(
                $"Die SQLite-Datenbank '{sourceFile}' existiert nicht — es gibt nichts zu sichern.",
                sourceFile);
        }

        // Pooling aus: Eine im Pool verbleibende Verbindung hielte die Zieldatei fest, und der
        // anschließende Move würde auf Windows scheitern.
        var source = new SqliteConnectionStringBuilder
        {
            DataSource = sourceFile,
            Pooling = false,
        }.ConnectionString;

        var destination = new SqliteConnectionStringBuilder
        {
            DataSource = destinationFile,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString;

        using var from = new SqliteConnection(source);
        from.Open();
        using var to = new SqliteConnection(destination);
        to.Open();
        from.BackupDatabase(to);
    }

    /// <summary>
    /// Der zuletzt angewendete Migrationsstand, direkt aus der Historientabelle gelesen. Bewusst als
    /// einfache Abfrage und nicht über den DbContext: Diese Datei gehört einem anderen Paket, und ein
    /// Backup hat keinen Grund, das Schema zu kennen.
    /// </summary>
    public static string? ReadLatestMigration(string databaseFile)
    {
        if (!File.Exists(databaseFile))
        {
            return null;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString;

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1
                """;
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            // Keine Historientabelle: eine v1.0-Datenbank oder eine leere Datei. Kein Fehler — das
            // Manifest trägt dann eben keinen Migrationsstand.
            return null;
        }
    }

    /// <summary>
    /// Entfernt die WAL-Begleiter einer Zieldatei. Beim Restore ist das keine Kosmetik: Ein
    /// liegengebliebenes <c>-wal</c> der alten Datenbank würde beim ersten Öffnen auf die neu
    /// eingespielte Datei angewendet — mit einem Ergebnis, das niemand vorhersagen möchte.
    /// </summary>
    public static void RemoveSidecars(string databaseFile)
    {
        foreach (var suffix in (string[])["-wal", "-shm", "-journal"])
        {
            var sidecar = databaseFile + suffix;
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }
    }
}
