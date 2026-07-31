using Bifrost.Core.Diagnostics.Checks;

namespace Bifrost.Server.KeyRing;

/// <summary>Woher ein Geheimwert stammt. Die <b>Herkunft</b> darf in jeden Bericht, der Wert nie.</summary>
public enum SecretSource
{
    /// <summary>Nicht gesetzt.</summary>
    NotSet = 0,

    /// <summary>Direkt aus der Konfiguration — im Container also aus der Prozessumgebung.</summary>
    Environment = 1,

    /// <summary>Aus einer Datei (Docker-/Compose-Secret, Kubernetes-Secret-Mount).</summary>
    File = 2,
}

/// <summary>Ein Geheimwert mit seiner Herkunft.</summary>
/// <param name="Value">Der Wert. <c>null</c>, wenn nichts gesetzt ist.</param>
/// <param name="Source">Woher er kam.</param>
public sealed record SecretValue(string? Value, SecretSource Source)
{
    public static SecretValue NotSet { get; } = new(null, SecretSource.NotSet);
}

/// <summary>
/// Der <c>_FILE</c>-Zusatz: Jede Einstellung, die ein Geheimnis trägt, lässt sich statt als Wert
/// auch als <b>Pfad auf eine Datei</b> angeben (FR-P048).
/// <para>
/// <b>Warum <c>_FILE</c> und nicht <c>AddKeyPerFile</c>:</b> <c>AddKeyPerFile</c> hängt ein ganzes
/// Verzeichnis als Konfigurationsquelle ein — jede Datei darin wird zu einer Einstellung, mit
/// eigener Namensübersetzung (<c>__</c> → <c>:</c>) und eigener Rangfolge gegenüber Umgebung und
/// <c>appsettings</c>. Das ist eine <i>zweite</i>, anders geformte Konfigurationsoberfläche neben
/// dem dokumentierten <c>BIFROST_*</c>-Vertrag, und sie liest alles, was jemand in das Verzeichnis
/// legt. <c>_FILE</c> gilt dagegen je Einstellung, ist ausdrücklich, ändert an der bestehenden
/// Rangfolge nichts und ist die Konvention, die die Container-Welt ohnehin spricht
/// (<c>POSTGRES_PASSWORD_FILE</c> und Verwandte). Ein Betreiber, der ein Compose-Secret einhängt,
/// muss dafür nichts Neues lernen.
/// </para>
/// <para>
/// Sind <b>beide</b> Formen gesetzt, ist das ein Fehler und keine Rangfolge. Welche gewinnt, wäre
/// eine Regel, die man nachlesen muss — und die Person, die sie falsch erinnert, betreibt danach
/// eine Instanz mit dem falschen Geheimnis.
/// </para>
/// </summary>
public static class FileSecret
{
    /// <summary>Das Namenssuffix. Stabil — Compose-Dateien und Runbooks zeigen darauf.</summary>
    public const string Suffix = KeyRingLayout.FileSuffix;

    /// <summary>
    /// Liest <paramref name="name"/> oder <c><paramref name="name"/>_FILE</c>.
    /// </summary>
    /// <exception cref="KeyRingConfigurationException">
    /// Wenn beide gesetzt sind, oder wenn die Datei fehlt beziehungsweise nicht lesbar ist. Eine
    /// fehlende Secret-Datei still als „kein Passwort" zu lesen wäre der schlimmste Ausgang: Das
    /// PFX ließe sich dann nicht öffnen, und die Ursache stünde nirgends.
    /// </exception>
    public static SecretValue Read(Func<string, string?> configuration, string name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var direct = configuration(name);
        var fileName = name + Suffix;
        var path = configuration(fileName);

        var hasDirect = !string.IsNullOrEmpty(direct);
        var hasPath = !string.IsNullOrWhiteSpace(path);

        if (hasDirect && hasPath)
        {
            throw new KeyRingConfigurationException(
                $"'{name}' und '{fileName}' sind beide gesetzt. Es gibt bewusst keine Rangfolge "
                + "zwischen ihnen — genau eine der beiden Angaben entfernen.");
        }

        if (hasPath)
        {
            return new SecretValue(ReadFile(path!.Trim(), fileName), SecretSource.File);
        }

        return hasDirect ? new SecretValue(direct, SecretSource.Environment) : SecretValue.NotSet;
    }

    private static string ReadFile(string path, string settingName)
    {
        string content;
        try
        {
            content = System.IO.File.ReadAllText(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException)
        {
            throw new KeyRingConfigurationException(
                $"'{settingName}' zeigt auf '{path}'; die Datei ist nicht lesbar "
                + $"({exception.GetType().Name}). Unter Compose muss das Secret deklariert UND "
                + "vorhanden sein.",
                exception);
        }

        // Genau EIN abschließender Zeilenumbruch fällt weg — 'echo geheim > secret.txt' schreibt
        // einen, und ein Passwort mit angehängtem '\n' öffnet kein PFX. Weiter wird nicht getrimmt:
        // Ein Passwort darf mit einem Leerzeichen enden, und das leise wegzuschneiden wäre dieselbe
        // Sorte Fehler mit umgekehrtem Vorzeichen.
        if (content.EndsWith("\r\n", StringComparison.Ordinal))
        {
            content = content[..^2];
        }
        else if (content.EndsWith('\n') || content.EndsWith('\r'))
        {
            content = content[..^1];
        }

        return content.Length == 0
            ? throw new KeyRingConfigurationException(
                $"'{settingName}' zeigt auf '{path}'; die Datei ist leer.")
            : content;
    }
}
