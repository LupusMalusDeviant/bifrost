using System.Collections;

namespace Bifrost.Server;

/// <summary>
/// Übergang von <c>MCPMCP_*</c> auf <c>BIFROST_*</c> (Umbenennung am 2026-07-31).
/// <para>
/// <b>Warum das existiert und nicht einfach die Doku umgeschrieben wurde:</b> Die
/// Umgebungsvariablen sind die <em>gesamte</em> Konfiguration dieses Gateways — Datenverzeichnis,
/// Datenbank, Key-Ring-Zertifikat, Audit-Modus, Guardrails. Eine Installation, die nach dem Update
/// mit unbekannten Variablennamen startet, verliert sie nicht mit einer Fehlermeldung, sondern
/// <b>lautlos</b>: Sie fällt auf alle Vorgabewerte zurück, legt eine leere Datenbank neben der
/// vollen an und meldet fröhlich „bereit". Genau diese Sorte Ausfall ist die teuerste, weil sie
/// wie ein Erfolg aussieht.
/// </para>
/// <para>
/// Die Übernahme passiert <b>vor</b> dem Bau der Konfiguration und im Prozessumfeld selbst. Damit
/// erreicht sie beide Lesearten, die es im Code gibt — <c>IConfiguration</c> und den direkten Griff
/// zu <see cref="Environment"/> — ohne dass an 30 Stellen ein Zweitname abgefragt werden muss.
/// </para>
/// <para>
/// <b>Der neue Name gewinnt immer.</b> Wer beide gesetzt hat, ist gerade beim Umstellen; dann ist
/// der alte Wert der zurückgelassene.
/// </para>
/// </summary>
internal static class LegacyEnvironment
{
    private const string OldPrefix = "MCPMCP_";
    private const string NewPrefix = "BIFROST_";

    /// <summary>
    /// Übernimmt alle noch alt benannten Variablen und liefert deren Namen — sortiert, damit die
    /// Warnung beim Start bei jedem Neustart gleich aussieht und nicht wie eine neue Meldung wirkt.
    /// </summary>
    public static IReadOnlyList<string> Adopt()
    {
        var adopted = new List<string>();

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key
                || !key.StartsWith(OldPrefix, StringComparison.Ordinal)
                || entry.Value is not string value)
            {
                continue;
            }

            var current = NewPrefix + key[OldPrefix.Length..];
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(current)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(current, value);
            adopted.Add(key);
        }

        adopted.Sort(StringComparer.Ordinal);
        return adopted;
    }

    /// <summary>
    /// Findet die Datenbankdatei einer Installation, die noch unter dem alten Namen liegt.
    /// <para>
    /// Der Vorgabename ist <c>bifrost.db</c>. Läge im Datenverzeichnis nur eine <c>mcpmcp.db</c>,
    /// würde der Gateway daneben eine leere neue Datei anlegen — ohne Server, ohne Rollen, ohne
    /// Schlüssel, und ohne einen einzigen Fehler. Deshalb gewinnt hier die vorhandene Datei über
    /// den schöneren Namen. Umbenannt wird nichts: Eine Datei zu verschieben, die gerade die
    /// gesamte Konfiguration enthält, ist keine Aufgabe für einen Programmstart.
    /// </para>
    /// </summary>
    public static string ResolveSqliteFile(string dataDirectory)
    {
        var current = Path.Combine(dataDirectory, "bifrost.db");
        var legacy = Path.Combine(dataDirectory, "mcpmcp.db");
        return !File.Exists(current) && File.Exists(legacy) ? legacy : current;
    }
}
