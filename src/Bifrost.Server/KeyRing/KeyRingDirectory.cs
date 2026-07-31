using System.Globalization;
using System.Xml.Linq;

using Bifrost.Core.Diagnostics.Checks;

namespace Bifrost.Server.KeyRing;

/// <summary>Eine Schlüsseldatei des Rings, so wie sie auf der Platte liegt.</summary>
/// <param name="Id">Die Schlüssel-Id aus dem Dateinamen (<c>key-&lt;guid&gt;.xml</c>).</param>
/// <param name="Path">Der volle Pfad.</param>
/// <param name="Encrypted">
/// Trägt die Datei verschlüsseltes Material? Das ist die Probe, mit der sich „das Zertifikat wirkt"
/// von „es ist nur konfiguriert" unterscheiden lässt, ohne das Zertifikat zu brauchen.
/// </param>
public sealed record KeyRingKeyFile(string Id, string Path, bool Encrypted);

/// <summary>
/// Der Blick auf das Verzeichnis <c>&lt;datadir&gt;/keys</c> — rein lesend.
/// <para>
/// Bewusst ohne DataProtection: Diese Klasse muss auch dann etwas sagen können, wenn das Zertifikat
/// fehlt oder falsch ist. Genau dann ist die Frage „liegt hier überhaupt Schlüsselmaterial?" die
/// wichtigste, und ein Weg, der über die Entschlüsselung führt, könnte sie nicht beantworten.
/// </para>
/// </summary>
public static class KeyRingDirectory
{
    /// <summary>Das Namensmuster, unter dem DataProtection seine Schlüssel ablegt.</summary>
    public const string KeyFilePattern = KeyRingLayout.KeyFilePattern;

    /// <summary>Der Pfad des Key-Rings zu einem Datenverzeichnis.</summary>
    public static string PathFor(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(dataDirectory, "keys");
    }

    /// <summary>
    /// Die Schlüsseldateien des Rings, nach Id sortiert. Ein fehlendes Verzeichnis ergibt eine leere
    /// Liste — der Unterschied zwischen „fehlt" und „ist leer" wird eine Ebene höher bewertet, weil
    /// er dort erst mit der Datenbank zusammen etwas bedeutet.
    /// </summary>
    public static IReadOnlyList<KeyRingKeyFile> Read(string keyRingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyRingDirectory);

        if (!Directory.Exists(keyRingDirectory))
        {
            return [];
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(keyRingDirectory, KeyFilePattern);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var keys = new List<KeyRingKeyFile>(files.Length);
        foreach (var file in files)
        {
            keys.Add(new KeyRingKeyFile(IdOf(file), file, IsEncrypted(file)));
        }

        keys.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return keys;
    }

    /// <summary>Die Id aus dem Dateinamen; der Name selbst, wenn er nicht dem Muster folgt.</summary>
    private static string IdOf(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return name.StartsWith("key-", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
    }

    /// <summary>
    /// Enthält die Datei verschlüsseltes Material? Erkannt wird das <c>&lt;encryptedKey&gt;</c> von
    /// DataProtection beziehungsweise das <c>EncryptedData</c> aus XML-Encryption. Ein Lesefehler
    /// heißt <c>false</c> — eine Datei, die sich nicht öffnen lässt, als „verschlüsselt" zu melden
    /// wäre eine beruhigende Auskunft ohne Grundlage.
    /// </summary>
    private static bool IsEncrypted(string file)
    {
        try
        {
            var document = XDocument.Load(file);
            return document.Descendants().Any(element =>
                element.Name.LocalName is "encryptedKey" or "EncryptedData" or "encryptedSecret");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>Kurzform für Meldungen: <c>3 Schlüsseldatei(en), davon 3 verschlüsselt</c>.</summary>
    public static string Describe(IReadOnlyList<KeyRingKeyFile> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{keys.Count} Schlüsseldatei(en), davon {keys.Count(key => key.Encrypted)} verschlüsselt");
    }
}
