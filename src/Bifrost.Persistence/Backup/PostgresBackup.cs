namespace Bifrost.Persistence.Backup;

/// <summary>
/// Die PostgreSQL-Seite von Sicherung und Wiederherstellung (ADR-0024 E2).
/// <para>
/// <b>Nicht implementiert — und zwar ausdrücklich.</b> Vorgesehen ist <c>pg_dump</c>/<c>pg_restore</c>
/// in einem dokumentierten Format. Solange das nicht gebaut und geprüft ist, wird jeder Aufruf mit
/// einer klaren Meldung abgewiesen. Es gibt <b>keinen</b> stillen Rückfall auf einen Zeilenexport:
/// Das wäre eine zweite, schlechter geprüfte Implementierung derselben Aufgabe — und ein Betreiber
/// erführe erst beim Zurückspielen, dass seine Sicherung nie eine war.
/// </para>
/// </summary>
internal static class PostgresBackup
{
    public const string NotImplementedMessage =
        "PostgreSQL-Sicherungen laufen laut ADR-0024 E2 über pg_dump und sind in dieser Ausbaustufe " +
        "nicht implementiert. Es gibt bewusst keinen Rückfall auf einen Zeilenexport. " +
        "Für PostgreSQL bitte pg_dump/pg_restore direkt verwenden; SQLite-Instanzen sichert " +
        "'bifrost backup' vollständig.";

    public static NotSupportedException NotImplemented() => new(NotImplementedMessage);
}
