namespace Faolan.Abstractions;

/// <summary>
/// Die Namen des WASI-Hosts über die Umbenennung (Vertrag umbenennung-faolan §2.3).
/// <para>
/// Bis 0.12.0 schrieb die Doku <c>/usr/local/bin/bifrost-wasi-host</c> in jedes
/// <c>Wasi.HostExecutable</c>; seit der Umbenennung heißt der Host <c>faolan-wasi-host</c>. Der alte
/// Name gilt als Verweis auf den umbenannten Host — als Dateiname und als Pfad im selben
/// Verzeichnis. Die Regel steht hier, damit Connector, Verbindungsdiagnose und <c>doctor</c> dieselbe
/// Antwort geben.
/// </para>
/// <para>
/// Die CLI bindet diese Datei wie <see cref="EnvironmentNameChain"/> per <c>&lt;Compile Include&gt;</c>
/// ein; dort ist die Klasse <c>internal</c>.
/// </para>
/// </summary>
#if FAOLAN_CLI
internal
#else
public
#endif
static class WasiHostNames
{
    /// <summary>Der Name des Hosts bis 0.12.0.</summary>
    public const string Legacy = "bifrost-wasi-host";

    /// <summary>Der Name des Hosts seit der Umbenennung.</summary>
    public const string Current = "faolan-wasi-host";

    /// <summary>Wo das Image bis 0.12.0 den Host ablegte.</summary>
    public const string LegacyBundledPath = "/usr/local/bin/" + Legacy;

    /// <summary>
    /// Welches Programm ein gespeicherter Verweis meint, gemessen an dem, was es gibt: Der Verweis
    /// selbst, wenn er etwas Vorhandenes bezeichnet; sonst, beim alten Namen, der umbenannte Host. So
    /// bleibt ein eigenes Programm, das wirklich <c>bifrost-wasi-host</c> heißt, gemeint.
    /// </summary>
    /// <param name="configured">Der gespeicherte Verweis.</param>
    /// <param name="exists">Bezeichnet der Verweis ein vorhandenes Programm?</param>
    /// <returns>Der wirksame Verweis und ob dafür der alte Name übersetzt werden musste.</returns>
    public static (string Effective, bool Translated) Resolve(string configured, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(exists);
        if (exists(configured) || !UsesLegacyName(configured))
        {
            return (configured, false);
        }

        var translated = Translate(configured);
        return exists(translated) ? (translated, true) : (configured, false);
    }

    /// <summary>
    /// Nennt <paramref name="configured"/> den Host unter seinem alten Namen? Unter Windows ohne
    /// Rücksicht auf Groß- und Kleinschreibung, wie der Dateiname dort auch.
    /// </summary>
    public static bool UsesLegacyName(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        var fileName = Path.GetFileName(configured);
        var withoutExe = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        return withoutExe.Equals(Legacy, NameComparison);
    }

    /// <summary>
    /// Derselbe Verweis unter dem neuen Namen — Verzeichnis und Endung bleiben, nur der Dateiname
    /// wechselt. Für einen Wert ohne alten Namen der Wert selbst.
    /// </summary>
    public static string Translate(string configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (!UsesLegacyName(configured))
        {
            return configured;
        }

        var fileName = Path.GetFileName(configured);
        return configured[..^fileName.Length] + Current + fileName[Legacy.Length..];
    }

    /// <summary>
    /// Nennt <paramref name="configured"/> den Host <paramref name="host"/> der Instanz — genau oder
    /// über den alten Namen? Dieselbe Regel, nach der der Connector entscheidet, ob er startet; die
    /// Verbindungsdiagnose fragt hier, damit sie das Programm prüft, das wirklich liefe.
    /// </summary>
    public static bool NamesHost(string? configured, string? host)
    {
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return NamesHostExactly(configured, host)
            || (UsesLegacyName(configured) && NamesHostExactly(Translate(configured), host));
    }

    /// <summary>
    /// Nennt <paramref name="configured"/> den Host genau so, wie er heißt — als bloßen Dateinamen
    /// (<c>.exe</c> darf fehlen) oder als denselben vollständigen Pfad?
    /// </summary>
    public static bool NamesHostExactly(string configured, string host)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(host);
        if (Path.GetFileName(configured) == configured)
        {
            // Nur ".exe" darf fehlen — GetFileNameWithoutExtension hielte unter Linux jeden Punkt im
            // Namen für den Beginn einer Endung.
            var hostName = Path.GetFileName(host);
            return configured.Equals(hostName, NameComparison)
                || (hostName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && configured.Equals(hostName[..^4], NameComparison));
        }

        return Path.GetFullPath(configured).Equals(Path.GetFullPath(host), NameComparison);
    }

    /// <summary>
    /// Läuft dieser Verweis nur über die Brücke? Er nennt den Host unter dem alten Namen und passt
    /// genau genommen nicht. Ohne eingerichteten Host (<paramref name="host"/> leer) läuft gar nichts —
    /// dann ist es auch keine Brücke, sondern ein fehlender Host, den die Diagnose eigens meldet.
    /// </summary>
    public static bool ReliesOnLegacyName(string? configured, string? host)
        => UsesLegacyName(configured)
            && !string.IsNullOrWhiteSpace(host)
            && !NamesHostExactly(configured!, host)
            && NamesHost(configured, host);

    /// <summary>Dateinamen werden so verglichen, wie das Betriebssystem sie vergleicht.</summary>
    public static StringComparison NameComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
