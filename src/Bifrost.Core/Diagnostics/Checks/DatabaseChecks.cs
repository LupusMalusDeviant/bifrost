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
