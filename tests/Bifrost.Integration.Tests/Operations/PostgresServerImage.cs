using Bifrost.Persistence.Backup;

namespace Bifrost.Tests.Postgres;

/// <summary>
/// <b>Die eine Stelle</b>, an der eine Testsuite entscheidet, welchen PostgreSQL-Server sie starten
/// darf (ADR-0024 E2).
///
/// <para>
/// Der Grund ist ein gemessener Fehlschlag: <c>pg_dump</c> weigert sich, einen <b>neueren</b> Server
/// zu sichern („aborting because of server version mismatch"). Ubuntu 24.04 liefert Client 16; ein
/// fest verdrahtetes <c>postgres:17-alpine</c> ist dort rot, und zwar aus einem Grund, der mit dem
/// Prüfling nichts zu tun hat. Geprüft werden soll, ob <i>unser</i> Code die Werkzeuge richtig
/// bedient — also bekommt der Server die Hauptversion des vorhandenen Clients.
/// </para>
///
/// <para>
/// <b>Warum diese Datei in Bifrost.Integration.Tests liegt und nach Bifrost.Upgrade.Tests verlinkt
/// ist:</b> Beide Suiten starten Server, gegen die gesichert wird, und beide brauchen dieselbe
/// Ableitung. Zwei Fassungen wären zwei Wahrheiten darüber, welcher Server hier startbar ist — und
/// die eine bekäme irgendwann eine Korrektur, die die andere nicht kennt. Dieselbe Begründung wie
/// bei <c>UpstreamProcessLookup</c> und <c>SecretCorpus</c>. Die <i>Versionsableitung selbst</i>
/// steht noch eine Stufe tiefer, in <see cref="PostgresTools.ParseMajorVersion"/> — dort, wo auch
/// das Produkt sie liest (BFR-DB-0006).
/// </para>
/// </summary>
internal static class PostgresServerImage
{
    /// <summary>Älteste Hauptversion, für die es ein offizielles Abbild gibt.</summary>
    private const int Oldest = 13;

    /// <summary>Neueste. Ein Abbild, das es nicht gibt, scheitert mit „manifest unknown" — eine
    /// Meldung, die über den Prüfling nichts aussagt.</summary>
    private const int Newest = 18;

    /// <summary>
    /// Der Grund, den eine Suite meldet, wenn sich kein startbarer Server ableiten lässt. Bewusst
    /// ein Überspringen mit Begründung und kein Ausweichen auf irgendeine Version: Ein Feld, das
    /// gegen einen Server läuft, den der Client gar nicht sichern kann, prüft nichts.
    /// </summary>
    public const string UndeterminedReason =
        "Zum vorhandenen 'pg_dump' laesst sich kein startbarer PostgreSQL-Server ableiten: Seine "
        + "Hauptversion ist entweder nicht lesbar oder ausserhalb der Spanne, fuer die es offizielle "
        + "Abbilder gibt (13-18). Ein Server, den dieser Client nicht sichern kann, wuerde nur den "
        + "Versionsunterschied vorfuehren und nicht den Pruefling.";

    /// <summary>Das Abbild zur Hauptversion des <c>pg_dump</c> an <paramref name="dumpPath"/>.</summary>
    public static async Task<string?> ForLocalClientAsync(string dumpPath, CancellationToken ct = default)
        => For(await PostgresTools.ReadMajorVersionAsync(dumpPath, ct));

    /// <summary>
    /// Die Ableitung selbst — ohne Prozess, ohne Container und deshalb wirklich prüfbar.
    /// <para>
    /// Ein <b>neuerer</b> Client darf einen älteren Server sichern; deshalb wird nach oben gekappt
    /// statt abgelehnt: Ein Client 19 bekommt den neuesten verfügbaren Server und nicht „nichts".
    /// Nach unten wird <b>nicht</b> gekappt — ein Client 12 kann einen Server 13 nicht sichern, und
    /// ein Abbild zu wählen, das der Client nicht beherrscht, wäre genau der Fehler, gegen den
    /// diese Datei geschrieben ist.
    /// </para>
    /// </summary>
    public static string? For(int? clientMajor)
    {
        if (clientMajor is null)
        {
            return null;
        }

        var major = Math.Min(clientMajor.Value, Newest);
        return major >= Oldest
            ? $"postgres:{major.ToString(System.Globalization.CultureInfo.InvariantCulture)}-alpine"
            : null;
    }
}
