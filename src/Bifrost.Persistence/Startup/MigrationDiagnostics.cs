using Bifrost.Abstractions.Operations;

namespace Bifrost.Persistence.Startup;

/// <summary>
/// Diagnosecodes der Start- und Migrationskoordination (M2-Vertrag §3, Präfix <c>BFR-DB-</c>).
/// <para>
/// <b>Reservierter Bereich: BFR-DB-0100 bis BFR-DB-0199.</b> Der Bereich darunter (0001–0099) bleibt
/// für die allgemeine Datenbankdiagnose (WP2.4) frei. Der Vertrag verlangt, dass ein Code nie
/// zweimal vergeben wird; ohne eine solche Aufteilung hätten zwei parallel arbeitende Pakete beide
/// bei 0001 angefangen.
/// </para>
/// Codes sind stabil: Der Text darf umformuliert werden, der Code nicht — er ist das, worauf ein
/// Betreiber ein Runbook stützt.
/// </summary>
public static class MigrationDiagnosticCodes
{
    /// <summary>Der Migrationslock war innerhalb der Wartezeit nicht zu bekommen — eine andere Instanz migriert.</summary>
    public const string LockNotAcquired = "BFR-DB-0100";

    /// <summary>Ein früherer Migrationslauf ist abgebrochen; der Zustand ist unbekannt. Schreibbetrieb verweigert.</summary>
    public const string InterruptedMigration = "BFR-DB-0101";

    /// <summary>Die Datenbank trägt Migrationen, die dieser Stand nicht kennt — also ein neueres Schema.</summary>
    public const string UnknownNewerSchema = "BFR-DB-0102";

    /// <summary>Die Migration selbst ist gescheitert.</summary>
    public const string MigrationFailed = "BFR-DB-0103";

    /// <summary>Das verlangte Vor-Migrationsbackup war nicht zu erstellen.</summary>
    public const string PreMigrationBackupMissing = "BFR-DB-0104";

    /// <summary>Journal oder Lock-Verfahren stehen nicht zur Verfügung — ohne sie wird nicht migriert.</summary>
    public const string SafetyMechanismUnavailable = "BFR-DB-0105";

    /// <summary>Bestanden: Das Schema ist auf dem Stand dieses Builds.</summary>
    public const string SchemaUpToDate = "BFR-DB-0110";

    /// <summary>Warnung: Es stehen Migrationen aus, die beim nächsten Start angewendet werden.</summary>
    public const string SchemaPending = "BFR-DB-0111";

    /// <summary>Übersprungen: Die Datenbank existiert noch nicht, es gibt nichts zu beurteilen.</summary>
    public const string DatabaseAbsent = "BFR-DB-0112";
}

/// <summary>
/// Abbruch des Datenbankstarts mit einem stabilen Code und einer nächsten Handlung.
/// <para>
/// Diese Ausnahme ist die <b>Verweigerung des Schreibbetriebs</b> aus ADR-0024 E7: Sie fliegt aus
/// <see cref="DatabaseInitializer.InitializeAsync"/> heraus und damit aus dem Start des Gateways.
/// Ein Dienst, der gar nicht erst hochkommt, kann auf eine unklare Datenbank nicht schreiben — das
/// ist die einzige Form der Verweigerung, die keine zweite Durchsetzungsstelle braucht.
/// </para>
/// </summary>
public sealed class DatabaseInitializationException : Exception
{
    private const string GenericRemediation =
        "Zustand der Datenbank prüfen (bifrost doctor) und im Zweifel aus der letzten Sicherung wiederherstellen.";

    public DatabaseInitializationException()
        : this(MigrationDiagnosticCodes.MigrationFailed, "Die Datenbank konnte nicht initialisiert werden.", GenericRemediation)
    {
    }

    public DatabaseInitializationException(string message)
        : this(MigrationDiagnosticCodes.MigrationFailed, message, GenericRemediation)
    {
    }

    public DatabaseInitializationException(string message, Exception? innerException)
        : this(MigrationDiagnosticCodes.MigrationFailed, message, GenericRemediation, null, innerException)
    {
    }

    public DatabaseInitializationException(
        string code,
        string message,
        string remediation,
        IReadOnlyDictionary<string, string>? safeDetails = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Remediation = remediation;
        SafeDetails = safeDetails ?? new Dictionary<string, string>();
    }

    /// <summary>Stabiler Code aus <see cref="MigrationDiagnosticCodes"/>.</summary>
    public string Code { get; }

    /// <summary>Die nächste Handlung des Betreibers — nie ein automatischer Reparaturversuch.</summary>
    public string Remediation { get; }

    /// <summary>Zusatzangaben zur Einordnung. Enthält nie Verbindungszeichenfolgen oder Credentials.</summary>
    public IReadOnlyDictionary<string, string> SafeDetails { get; }

    /// <summary>Übersetzung in das Diagnosemodell des M2-Vertrags, damit WP2.4 nichts nachbaut.</summary>
    public DiagnosticCheck ToCheck()
        => new(Code, CheckStatus.Fail, Message, Remediation, SafeDetails);
}
