using Npgsql;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Die PostgreSQL-Seite von Sicherung und Wiederherstellung (ADR-0024 E2).
/// <para>
/// <b>Format: <c>custom</c> (<c>pg_dump --format=custom</c>).</b> Begründung, weil das eine
/// Entscheidung ist und keine Vorliebe:
/// </para>
/// <list type="bullet">
/// <item><b>Eine Datei.</b> Das Archivformat aus E1 legt genau eine Nutzlast je Bereich ab, bildet
/// Prüfsummen darüber und verschlüsselt sie am Stück. <c>directory</c> wären hunderte Einträge —
/// jeder mit eigener Prüfsumme, eigenem Geheimtext und der Frage, ob die Menge vollständig ist.
/// Das ist derselbe Nutzen mit mehr beweglichen Teilen.</item>
/// <item><b>Es ist Daten, kein Skript.</b> <c>plain</c> ergibt eine SQL-Datei, die beim Restore
/// <em>ausgeführt</em> werden müsste — über <c>psql</c>, das jede Anweisung tut, die drinsteht,
/// einschließlich <c>\!</c>-Shellaufrufen. Ein Archiv ist Fremdeingabe. Ein Format, dessen
/// Wiederherstellung „führe diesen Text aus" heißt, ist die falsche Wahl für Fremdeingabe.</item>
/// <item><b><c>pg_restore</c> ordnet selbst.</b> Das Inhaltsverzeichnis des custom-Formats erlaubt
/// <c>--single-transaction</c>: Entweder die Wiederherstellung ist ganz durch, oder die Datenbank
/// ist unberührt. Genau die Zusage, die E5 für das Ziel verlangt.</item>
/// <item><b>Es komprimiert selbst</b>, was ein Vollbackup einer Gateway-Datenbank deutlich kleiner
/// macht als der Klartext-Dump.</item>
/// </list>
/// <para>
/// <b>Kein Zeilenexport, unter keinen Umständen.</b> Fehlt <c>pg_dump</c>, wirft
/// <see cref="PostgresTools.Require"/> mit einer Meldung, die sagt, was fehlt und wo man es
/// herbekommt (ADR-0024 E2). Ein selbstgebauter Export müsste Sequenzstände, Fremdschlüssel­reihen­folge,
/// Erweiterungen und <c>bytea</c>-Geheimtext selbst beherrschen — eine zweite Sicherung derselben
/// Daten, die niemand so scharf prüft wie die erste.
/// </para>
/// </summary>
internal static class PostgresBackup
{
    /// <summary>
    /// Schreibt den Dump nach <paramref name="destinationFile"/>.
    /// <para>
    /// Bewusst <b>ohne</b> <c>--no-owner</c>/<c>--no-privileges</c>: Was im Dump steht, soll
    /// vollständig sein; ob Eigentümer und Rechte beim Zurückspielen übernommen werden, entscheidet
    /// erst der Restore. Ein an der Quelle beschnittener Dump kann das nicht mehr nachholen.
    /// </para>
    /// </summary>
    public static async Task CreateAsync(
        string connectionString, string destinationFile, string? binDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);

        var tools = PostgresTools.Require(binDirectory);
        var target = PostgresTools.Target.FromConnectionString(connectionString);

        // Die Datenbank als '--dbname=' und nicht als freistehendes Argument: Ein Name, der mit
        // einem Bindestrich beginnt, wäre sonst eine Option.
        var arguments = new List<string>(target.ConnectionArguments())
        {
            "--format=custom",
            "--file=" + destinationFile,
            "--dbname=" + target.Database,
        };

        await PostgresTools.RunAsync(tools.DumpPath, arguments, target, ct).ConfigureAwait(false);

        if (!File.Exists(destinationFile))
        {
            throw new PostgresToolFailedException(
                $"'{PostgresTools.DumpProgram}' meldete Erfolg, hat aber keine Datei geschrieben.");
        }
    }

    /// <summary>
    /// Spielt einen Dump zurück.
    /// <para>
    /// <c>--single-transaction</c> ist hier das tragende Argument und nicht Vorsicht: Es macht die
    /// Wiederherstellung zu einem Ganzen. Bricht sie mittendrin ab, rollt PostgreSQL selbst zurück,
    /// und die Zielinstanz steht danach so da wie vorher — die Zusage aus ADR-0024 E5, die sich für
    /// eine Datenbank nicht durch Umbenennen von Dateien herstellen lässt.
    /// </para>
    /// <para>
    /// <c>--no-owner</c>/<c>--no-privileges</c>: Die Zielinstallation hat in aller Regel eine andere
    /// Rolle als die Quelle (anderer Container, anderer Betreiber). Ohne diese beiden bräche der
    /// Restore an einem <c>role "xyz" does not exist</c> ab — an einer Stelle, die mit den Daten
    /// nichts zu tun hat.
    /// </para>
    /// </summary>
    /// <param name="clean">
    /// Vorhandene Objekte vorher entfernen. Nur für <c>Replace</c>; auf ein leeres Ziel wäre es eine
    /// Aufforderung, Dinge zu löschen, die es nicht gibt.
    /// </param>
    public static async Task RestoreAsync(
        string connectionString, string dumpFile, bool clean, string? binDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpFile);
        if (!File.Exists(dumpFile))
        {
            throw new FileNotFoundException(
                $"Der Dump '{dumpFile}' existiert nicht — es gibt nichts zurückzuspielen.", dumpFile);
        }

        var tools = PostgresTools.Require(binDirectory);
        var target = PostgresTools.Target.FromConnectionString(connectionString);

        var arguments = new List<string>(target.ConnectionArguments())
        {
            "--format=custom",
            "--dbname=" + target.Database,
            "--no-owner",
            "--no-privileges",
            "--single-transaction",
            "--exit-on-error",
        };

        if (clean)
        {
            // '--if-exists' gehört zwingend dazu: '--clean' allein bricht am ersten DROP eines
            // Objekts ab, das es im Ziel nicht gibt.
            arguments.Add("--clean");
            arguments.Add("--if-exists");
        }

        arguments.Add(dumpFile);

        await PostgresTools.RunAsync(tools.RestorePath, arguments, target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Der zuletzt angewendete Migrationsstand. Wie bei SQLite als einfache Abfrage und nicht über
    /// den DbContext: Ein Backup hat keinen Grund, das Schema zu kennen.
    /// </summary>
    public static async Task<string?> ReadLatestMigrationAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1
                """;
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        catch (PostgresException)
        {
            // Keine Historientabelle: eine leere Datenbank. Kein Fehler — das Manifest trägt dann
            // eben keinen Migrationsstand.
            return null;
        }
    }

    /// <summary>
    /// Ist die Zieldatenbank leer? Gezählt werden Tabellen außerhalb der Systemschemata; eine frisch
    /// angelegte Datenbank hat davon keine.
    /// <para>
    /// Ist die Datenbank <b>nicht erreichbar</b>, gilt sie als leer: Der Regelfall aus E5 ist die
    /// Wiederherstellung auf ein frisch aufgesetztes Ziel, und eine noch nicht erreichbare Datenbank
    /// als „nicht leer" zu melden hieße, genau diesen Fall zu blockieren. Erreichbar sein muss sie
    /// spätestens beim Anwenden — dort scheitert der Aufruf dann mit der Meldung des Werkzeugs.
    /// </para>
    /// </summary>
    public static async Task<bool> IsEmptyAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
                """;
            var count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture) == 0;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return true;
        }
    }
}
