namespace Bifrost.Core.Importing;

/// <summary>
/// Die <b>Form</b> eines Pfades, beurteilt ohne das Dateisystem und ohne die Plattform, auf der
/// dieser Prozess gerade läuft.
/// <para>
/// <b>Warum nicht <see cref="Path.IsPathFullyQualified(string)"/>:</b> Diese Methode antwortet
/// plattformabhängig. Unter Linux ist <c>C:\Programme\server.exe</c> ein <em>relativer</em> Pfad mit
/// einem ungewöhnlichen Namen, unter Windows ist <c>/usr/local/bin/server</c> nicht
/// vollqualifiziert. Eine importierte Konfiguration kommt aber von einem fremden Rechner: Ein
/// Gateway auf Linux, das eine Windows-Konfiguration prüft, würde jede absolute Angabe als
/// „relativer Pfad" melden — ein Befund, der nur vom Betriebssystem des Prüfers handelt und nicht
/// von der Konfiguration.
/// </para>
/// <para>
/// Die Beurteilung ist bewusst rein syntaktisch. Ob der Pfad existiert, ist eine Frage an die
/// Zielinstanz und nicht an den Importplan — der Import fasst kein Dateisystem an.
/// </para>
/// </summary>
public static class ImportPathShape
{
    /// <summary>Endungen, die unter Windows an einem Programmnamen hängen dürfen.</summary>
    private static readonly string[] ExecutableSuffixes = [".exe", ".cmd", ".bat", ".com", ".ps1"];

    /// <summary>
    /// Entfernt umschließende Anführungszeichen und Leerraum. In fremden Konfigurationen steht ein
    /// Programm mit Leerzeichen im Pfad oft in Anführungszeichen — die gehören zur Schreibweise der
    /// Quelldatei, nicht zum Pfad.
    /// </summary>
    public static string Unquote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    /// <summary>
    /// Zeigt dieser Pfad von einer Wurzel aus — egal welcher Plattform?
    /// <para>
    /// Erkannt werden die POSIX-Wurzel (<c>/opt/…</c>), ein Windows-Laufwerk (<c>C:\…</c>,
    /// <c>C:/…</c>) und ein UNC-Pfad (<c>\\server\share</c>). <b>Nicht</b> als absolut gilt der
    /// laufwerksrelative Windows-Pfad <c>\Programme\…</c>: Welches Laufwerk gemeint ist, entscheidet
    /// dort der Prozesszustand — genau die Unbestimmtheit, wegen der relative Pfade gemeldet werden.
    /// </para>
    /// </summary>
    public static bool IsAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var value = path.Trim();

        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        if (value[0] == '/')
        {
            return true;
        }

        if (value[0] == '\\')
        {
            return false;
        }

        return value.Length >= 3
            && char.IsAsciiLetter(value[0])
            && value[1] == ':'
            && value[2] is '/' or '\\';
    }

    /// <summary>
    /// Trägt die Angabe überhaupt einen Verzeichnisanteil? Ohne einen entscheidet die
    /// <c>PATH</c>-Variable des Dienstes, welches Programm startet.
    /// </summary>
    public static bool HasDirectoryPart(string path)
        => !string.IsNullOrEmpty(path)
            && (path.Contains('/', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal));

    /// <summary>
    /// Hängt der Pfad an einem Heimatverzeichnis oder einer Umgebungsvariablen (<c>~/…</c>,
    /// <c>$HOME/…</c>, <c>%USERPROFILE%\…</c>)? Solche Angaben sind auf der Zielinstanz etwas
    /// anderes als auf der Quellmaschine — und sie werden hier ausdrücklich <b>nicht</b> aufgelöst.
    /// </summary>
    public static bool IsEnvironmentRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var value = path.Trim();
        return value[0] == '~'
            || value.Contains('%', StringComparison.Ordinal)
            || value.Contains('$', StringComparison.Ordinal);
    }

    /// <summary>
    /// Der reine Programmname einer Kommandoangabe, klein geschrieben und ohne
    /// Windows-Programmendung — <c>C:\Program Files\nodejs\npx.cmd</c> wird zu <c>npx</c>.
    /// <para>
    /// Damit trifft die Erkennung von <c>npx</c> und <c>uvx</c> denselben Fall auch dann, wenn er
    /// mit vollem Pfad dasteht. Eine Erkennung, die nur auf <c>"npx"</c> vergleicht, sähe genau die
    /// sorgfältig geschriebene Konfiguration nicht.
    /// </para>
    /// </summary>
    public static string Program(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var value = Unquote(command);
        var cut = value.LastIndexOfAny(['/', '\\']);
        if (cut >= 0 && cut < value.Length - 1)
        {
            value = value[(cut + 1)..];
        }

        value = value.ToLowerInvariant();
        foreach (var suffix in ExecutableSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }
}
