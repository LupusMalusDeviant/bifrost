using System.Globalization;

using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Diagnostics.Checks;

/// <summary>
/// BFR-DB-0001 — Provider bekannt und zur Verbindungsangabe passend.
/// <para>
/// Ein unbekannter Wert wird <b>nicht</b> still auf SQLite zurückgesetzt: Neue Konfiguration ist
/// fail-closed (M2-Vertrag §6, Invariante 3). Wer <c>postgre</c> schreibt und eine leere
/// SQLite-Datei bekommt, sucht den Fehler an der falschen Stelle.
/// </para>
/// </summary>
public sealed class DatabaseProviderCheck : IDiagnosticCheck
{
    private static readonly string[] Known = ["sqlite", "postgres"];

    public string Code => DiagnosticCodes.DatabaseProvider;

    public DiagnosticScope Scope => DiagnosticScope.Database;

    public TimeSpan Timeout => TimeSpan.FromSeconds(2);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var provider = context.DatabaseProvider;
        // Der Provider-Name ist eine Auswahl aus zwei Werten und damit kein Geheimnis. Die
        // Verbindungszeichenfolge steht NIE in den Details — nur, ob es eine gibt.
        var details = CheckOutcome.Details(
            ("provider", provider),
            ("verbindung_gesetzt", DetailFormat.YesNo(context.HasDatabaseConnectionString)));

        if (!Known.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(CheckOutcome.Fail(
                Code,
                $"BIFROST_DB_PROVIDER nennt '{provider}' — bekannt sind nur 'sqlite' und 'postgres'.",
                "Wert korrigieren. Ein unbekannter Provider fällt nicht still auf SQLite zurück.",
                details));
        }

        if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            && !context.HasDatabaseConnectionString)
        {
            return Task.FromResult(CheckOutcome.Fail(
                Code,
                "Der Provider ist 'postgres', aber BIFROST_DB_CONNECTION fehlt.",
                "Bei PostgreSQL ist die Verbindungszeichenfolge Pflicht — es gibt keinen Vorgabewert.",
                details));
        }

        return Task.FromResult(CheckOutcome.Pass(
            Code, $"Datenbank-Provider '{provider}' ist konfiguriert.", details));
    }
}

/// <summary>BFR-DB-0002 — ist die Datenbank überhaupt erreichbar?</summary>
public sealed class DatabaseReachabilityCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.DatabaseReachable;

    public DiagnosticScope Scope => DiagnosticScope.Database;

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database is null)
        {
            return DatabaseCheckSupport.NoProbe(Code);
        }

        var facts = await context.Database.DescribeAsync(ct).ConfigureAwait(false);
        return facts.CanConnect
            ? CheckOutcome.Pass(Code, "Die Datenbank ist erreichbar.")
            : CheckOutcome.Fail(
                Code,
                $"Die Datenbank ist nicht erreichbar: {facts.Failure ?? "kein Grund gemeldet"}",
                "Bei SQLite: Datenverzeichnis und Dateirechte prüfen (siehe BFR-CFG-0001). Bei "
                + "PostgreSQL: Erreichbarkeit des Servers und die Verbindungszeichenfolge prüfen.");
    }
}

/// <summary>
/// BFR-DB-0003 — angewendete Migrationen.
/// <para>
/// Eine leere Historie auf einer Datenbank, die Daten enthält, ist der Alt-Schema-Fall: Der Start
/// stempelt dann die Initial-Migration als Baseline (<c>BaselinedLegacySchema</c>). Auf einer
/// wirklich leeren Datenbank ist sie erwartbar.
/// </para>
/// </summary>
public sealed class AppliedMigrationsCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.DatabaseAppliedMigrations;

    public DiagnosticScope Scope => DiagnosticScope.Database;

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database is null)
        {
            return DatabaseCheckSupport.NoProbe(Code);
        }

        var facts = await context.Database.DescribeAsync(ct).ConfigureAwait(false);
        if (!facts.CanConnect)
        {
            return DatabaseCheckSupport.Unreachable(Code);
        }

        var applied = facts.AppliedMigrations ?? [];
        if (applied.Count == 0)
        {
            return CheckOutcome.Warning(
                Code,
                "Die Datenbank hat keine Migrationshistorie.",
                "Auf einer neuen Installation ist das erwartbar — der Start legt das Schema an. Auf "
                + "einer bestehenden ist es der Alt-Schema-Fall: Der Start stempelt die "
                + "Initial-Migration als Baseline und meldet 'BaselinedLegacySchema'. Vorher sichern.",
                CheckOutcome.Details(("angewendet", "0")));
        }

        return CheckOutcome.Pass(
            Code,
            $"{applied.Count} Migration(en) angewendet, zuletzt '{applied[^1]}'.",
            CheckOutcome.Details(
                ("angewendet", DetailFormat.Count(applied.Count)),
                ("letzte", applied[^1])));
    }
}

/// <summary>BFR-DB-0004 — ausstehende Migrationen.</summary>
public sealed class PendingMigrationsCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.DatabasePendingMigrations;

    public DiagnosticScope Scope => DiagnosticScope.Database;

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Database is null)
        {
            return DatabaseCheckSupport.NoProbe(Code);
        }

        var facts = await context.Database.DescribeAsync(ct).ConfigureAwait(false);
        if (!facts.CanConnect)
        {
            return DatabaseCheckSupport.Unreachable(Code);
        }

        var pending = facts.PendingMigrations ?? [];
        if (pending.Count == 0)
        {
            return CheckOutcome.Pass(Code, "Keine ausstehenden Migrationen.");
        }

        return CheckOutcome.Warning(
            Code,
            $"{pending.Count} Migration(en) stehen aus.",
            "Der nächste Start wendet sie an. Vorher Datenbank und Datenverzeichnis sichern — eine "
            + "Migration ist nicht rückwärts fahrbar (docs/operations.md, Abschnitt Backup).",
            CheckOutcome.Details(
                ("ausstehend", DetailFormat.Count(pending.Count)),
                ("namen", string.Join(", ", pending))));
    }
}

/// <summary>
/// BFR-DB-0005 — liegt im Datenverzeichnis überhaupt eine SQLite-Datei?
/// <para>
/// Das ist der Check zum teuersten dokumentierten Fehlerbild: Ein umbenanntes Volume, ein
/// verschobenes Compose-Verzeichnis oder ein geänderter Projektname zeigen auf ein <b>anderes</b>,
/// leeres Volume. Docker legt es stillschweigend an, der Gateway richtet sich darin ein und meldet
/// sich als bereit. Der Ausfall sieht aus wie ein gelungener Start.
/// </para>
/// </summary>
public sealed class SqliteDatabaseFileCheck : IDiagnosticCheck
{
    private const string CurrentName = "bifrost.db";
    private const string LegacyName = "mcpmcp.db";

    public string Code => DiagnosticCodes.SqliteDatabaseFile;

    public DiagnosticScope Scope => DiagnosticScope.Database;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.DatabaseProvider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code, "Der Provider ist nicht SQLite; im Datenverzeichnis liegt keine Datenbankdatei."));
        }

        if (context.HasDatabaseConnectionString)
        {
            // Der Pfad ergibt sich dann aus der Verbindungszeichenfolge, und die wird hier weder
            // ausgewertet noch ausgegeben.
            return Task.FromResult(CheckOutcome.Skipped(
                Code,
                "BIFROST_DB_CONNECTION ist ausdrücklich gesetzt — die Datenbankdatei ergibt sich "
                + "daraus und nicht aus dem Datenverzeichnis."));
        }

        var directory = context.DataDirectory;
        var current = Path.Combine(directory, CurrentName);
        var legacy = Path.Combine(directory, LegacyName);
        var hasCurrent = context.Files.FileExists(current);
        var hasLegacy = context.Files.FileExists(legacy);
        var details = CheckOutcome.Details(
            ("verzeichnis", directory),
            (CurrentName, DetailFormat.YesNo(hasCurrent)),
            (LegacyName, DetailFormat.YesNo(hasLegacy)));

        if (hasCurrent && hasLegacy)
        {
            return Task.FromResult(CheckOutcome.Warning(
                Code,
                $"Im Datenverzeichnis liegen beide Datenbankdateien ('{CurrentName}' und '{LegacyName}').",
                $"Benutzt wird '{CurrentName}'. Die alte Datei liegt nur noch daneben — nach einer "
                + "Prüfung ihres Inhalts wegräumen, sonst spielt beim nächsten Vorfall jemand die "
                + "falsche zurück.",
                details));
        }

        if (hasCurrent)
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code, $"Die SQLite-Datenbank '{current}' ist vorhanden.", details));
        }

        if (hasLegacy)
        {
            return Task.FromResult(CheckOutcome.Warning(
                Code,
                $"Im Datenverzeichnis liegt nur die alt benannte '{LegacyName}'. Sie wird "
                + "weiterverwendet; es entsteht keine leere neue Datei daneben.",
                "Kein Handlungsdruck. Wer umbenennen will, tut das bei gestopptem Gateway und mit "
                + "Sicherung — die Datei enthält die gesamte Konfiguration.",
                details));
        }

        return Task.FromResult(CheckOutcome.Warning(
            Code,
            $"Im Datenverzeichnis '{directory}' liegt keine SQLite-Datenbank.",
            "Bei einer Neuinstallation ist das richtig. Bei einer bestehenden zeigt das Volume "
            + "woanders hin: Der Gateway legt dann eine leere Datenbank an und meldet sich "
            + "fehlerfrei als bereit — ohne Server, ohne Rollen, ohne Key-Ring. Vor dem Start "
            + "'docker compose config --volumes' gegen die vorhandenen Volumes halten.",
            details));
    }
}

/// <summary>
/// BFR-DB-0006 — <b>kann der vorhandene <c>pg_dump</c> diesen Server sichern?</b> (ADR-0024 E2)
/// <para>
/// Der Anlass ist ein gemessener: Ubuntu 24.04 liefert das Clientpaket 16, ein aktueller Server ist
/// 17 oder 18. <c>pg_dump</c> sichert nur Server bis zu seiner eigenen Hauptversion und bricht sonst
/// mit „aborting because of server version mismatch" ab. Ein Betreiber in dieser Lage <b>hat keine
/// Sicherung</b> — und erfährt es ohne diesen Befund erst im Ernstfall oder beim ersten Upgrade, weil
/// die Vor-Migrationssicherung aus E7 an derselben Stelle scheitert.
/// </para>
/// <para>
/// <b>Dieser Check rät nicht.</b> Fehlt eine der beiden Zahlen — kein Client, keine Verbindung, eine
/// Version, die sich nicht lesen lässt —, sagt er genau das. Eine unbelegte Verträglichkeitszusage
/// wäre schlimmer als gar keine: Sie beruhigt den, der sich gerade auf seinen Rückweg verlässt.
/// </para>
/// </summary>
public sealed class PostgresBackupToolVersionCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.PostgresBackupToolVersion;

    public DiagnosticScope Scope => DiagnosticScope.Database;

    // Ein Prozessstart (pg_dump --version) und eine Abfrage an den Server.
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public async Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.DatabaseProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
        {
            return CheckOutcome.Skipped(
                Code,
                "Der Provider ist nicht PostgreSQL; die Sicherung läuft dann ohne pg_dump "
                + "(ADR-0024 E2).");
        }

        if (context.PostgresBackupTools is null)
        {
            return CheckOutcome.Skipped(
                Code,
                "Keine Werkzeugsonde verdrahtet. Dieser Befund entsteht nur dort, wo die "
                + "Installation wirklich untersucht wird — im Serverprozess.");
        }

        var tools = await context.PostgresBackupTools.DescribeAsync(ct).ConfigureAwait(false);
        if (!tools.Located)
        {
            return CheckOutcome.Warning(
                Code,
                "Auf dieser Instanz sind 'pg_dump' und 'pg_restore' nicht erreichbar — es gibt "
                + "damit keine Sicherung der PostgreSQL-Datenbank.",
                "Das PostgreSQL-Clientpaket installieren oder BIFROST_POSTGRES_BIN auf sein "
                + "Verzeichnis setzen. Es gibt bewusst keinen Rückfall auf einen selbstgebauten "
                + "Zeilenexport (ADR-0024 E2); ohne die Werkzeuge migriert der Start zwar weiter, "
                + "aber ohne Rückweg.",
                CheckOutcome.Details(("werkzeuge_gefunden", DetailFormat.YesNo(false))));
        }

        var client = tools.ClientMajorVersion;
        var server = await ServerMajorAsync(context, ct).ConfigureAwait(false);

        // Reihenfolge ist Absicht: erst die Zahl, die auf DIESEM Rechner steht — sie fehlt, wenn das
        // gefundene Programm gar keines ist.
        if (client is null)
        {
            return CheckOutcome.Warning(
                Code,
                $"'{tools.DumpPath ?? "pg_dump"}' antwortet nicht mit einer lesbaren "
                + "Versionsangabe. Ob dieser Client den Server sichern kann, ist damit unbeantwortet.",
                "Das Programm von Hand mit '--version' aufrufen. Antwortet es nicht, ist es kein "
                + "brauchbarer Client — dann steht die Sicherung nur auf dem Papier.",
                CheckOutcome.Details(("client_version", "nicht lesbar")));
        }

        if (server is null)
        {
            return CheckOutcome.Warning(
                Code,
                $"Die Serverversion ließ sich nicht ermitteln; der Client ist Hauptversion "
                + $"{client.Value.ToString(CultureInfo.InvariantCulture)}. Ob er diesen Server "
                + "sichern kann, ist damit unbeantwortet.",
                $"Erreichbarkeit der Datenbank prüfen (siehe {DiagnosticCodes.DatabaseReachable}). "
                + "Solange die Serverversion unbekannt ist, sagt dieser Befund bewusst nichts über "
                + "die Verträglichkeit — 'unbekannt' ist kein 'passt'.",
                CheckOutcome.Details(
                    ("client_hauptversion", DetailFormat.Count(client.Value)),
                    ("server_hauptversion", "nicht ermittelt")));
        }

        var details = CheckOutcome.Details(
            ("client_hauptversion", DetailFormat.Count(client.Value)),
            ("server_hauptversion", DetailFormat.Count(server.Value)));

        if (client.Value < server.Value)
        {
            return CheckOutcome.Fail(
                Code,
                $"Client {client.Value.ToString(CultureInfo.InvariantCulture)}, Server "
                + $"{server.Value.ToString(CultureInfo.InvariantCulture)} — dieser Client kann "
                + $"diesen Server nicht sichern; gebraucht wird >= "
                + $"{server.Value.ToString(CultureInfo.InvariantCulture)}. Jede Sicherung bricht mit "
                + "'aborting because of server version mismatch' ab, auch die vor einer Migration "
                + "(ADR-0024 E7).",
                "Ein PostgreSQL-Clientpaket ab der Hauptversion des Servers installieren "
                + "(Debian/Ubuntu: den PGDG-Apt-Spiegel einbinden, weil die Distribution nur ihre "
                + "eigene Version führt; Alpine: 'postgresql"
                + $"{server.Value.ToString(CultureInfo.InvariantCulture)}-client') — oder "
                + "BIFROST_POSTGRES_BIN auf ein Verzeichnis setzen, in dem ein passender Client "
                + "liegt. Ein selbstgebauter Zeilenexport ist ausdrücklich kein Ersatz (ADR-0024 E2).",
                details);
        }

        return CheckOutcome.Pass(
            Code,
            $"Client {client.Value.ToString(CultureInfo.InvariantCulture)} kann Server "
            + $"{server.Value.ToString(CultureInfo.InvariantCulture)} sichern.",
            details);
    }

    private static async Task<int?> ServerMajorAsync(DiagnosticContext context, CancellationToken ct)
    {
        if (context.Database is null)
        {
            return null;
        }

        var facts = await context.Database.DescribeAsync(ct).ConfigureAwait(false);
        return facts.CanConnect ? facts.ServerMajorVersion : null;
    }
}

internal static class DatabaseCheckSupport
{
    public static DiagnosticCheck NoProbe(string code) => CheckOutcome.Skipped(
        code,
        "Kein Datenbankzugang verdrahtet. Dieser Befund entsteht nur dort, wo eine Verbindung "
        + "besteht — im Serverprozess.");

    public static DiagnosticCheck Unreachable(string code) => CheckOutcome.Skipped(
        code,
        $"Nicht beantwortbar, solange die Datenbank nicht erreichbar ist (siehe "
        + $"{DiagnosticCodes.DatabaseReachable}).");
}
