using System.Globalization;
using System.IO.Compression;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Die Prüfung eines einzelnen Archiveintrags, <b>bevor</b> irgendetwas entpackt wird (ADR-0024 E5).
/// <para>
/// Abgewehrt werden hier drei Dinge, die alle dieselbe Ursache haben — das Archiv ist Fremdeingabe:
/// Zip-Slip (Pfade, die aus dem Zielverzeichnis herausführen), Symlinks (Einträge, die auf etwas
/// anderes zeigen, als sie zu sein vorgeben) und Dekompressionsbomben (Einträge, die entpackt ein
/// Vielfaches ihrer Größe belegen).
/// </para>
/// </summary>
internal static class ArchiveEntryGuard
{
    /// <summary>Unix-Dateitypmaske und der Wert für einen Symlink, so wie ZIP sie in den oberen
    /// 16 Bit der externen Attribute ablegt.</summary>
    private const int UnixFileTypeMask = 0xF000;

    private const int UnixSymbolicLink = 0xA000;

    /// <summary>
    /// Ist der Eintragsname als Ziel unter <paramref name="rootFullPath"/> zulässig? Der Name wird
    /// dazu kanonisiert und gegen das Zielverzeichnis verankert — eine reine Textprüfung auf
    /// <c>..</c> ist zu wenig, weil ein Pfad auch über Verkettung und Groß-/Kleinschreibung
    /// herausführen kann.
    /// </summary>
    public static bool TryResolve(string entryName, string rootFullPath, out string fullPath, out string? problem)
    {
        fullPath = "";
        problem = null;

        if (string.IsNullOrWhiteSpace(entryName))
        {
            problem = "Das Archiv enthält einen Eintrag ohne Namen.";
            return false;
        }

        if (entryName.Any(char.IsControl))
        {
            problem = "Das Archiv enthält einen Eintragsnamen mit Steuerzeichen.";
            return false;
        }

        // Ein ZIP kennt nur '/' als Trenner. Ein '\' im Namen ist entweder ein Angriff auf Windows
        // oder ein legitimer Teil eines Unix-Dateinamens — beides wollen wir hier nicht.
        if (entryName.Contains('\\', StringComparison.Ordinal))
        {
            problem = $"Eintrag '{entryName}' enthält einen Backslash und wird abgelehnt.";
            return false;
        }

        if (entryName.Contains(':', StringComparison.Ordinal))
        {
            problem = $"Eintrag '{entryName}' enthält einen Doppelpunkt (Laufwerk oder Datenstrom) und wird abgelehnt.";
            return false;
        }

        if (Path.IsPathRooted(entryName))
        {
            problem = $"Eintrag '{entryName}' ist ein absoluter Pfad und wird abgelehnt.";
            return false;
        }

        var segments = entryName.Split('/', StringSplitOptions.None);
        if (segments.Any(s => s is ".."))
        {
            problem = $"Eintrag '{entryName}' führt mit '..' aus dem Zielverzeichnis heraus und wird abgelehnt.";
            return false;
        }

        var root = EnsureTrailingSeparator(Path.GetFullPath(rootFullPath));
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"Eintrag '{entryName}' ergibt keinen gültigen Pfad: {ex.Message}";
            return false;
        }

        // Die eigentliche Verankerung: nach der Kanonisierung MUSS der Pfad unter der Wurzel liegen.
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"Eintrag '{entryName}' zeigt aus dem Zielverzeichnis heraus und wird abgelehnt.";
            return false;
        }

        if (!BackupLayout.AllowedZones.Any(z => entryName.StartsWith(z, StringComparison.Ordinal))
            && entryName != BackupLayout.ManifestEntry
            && entryName != BackupLayout.ChecksumEntry)
        {
            problem = $"Eintrag '{entryName}' liegt außerhalb der bekannten Bereiche und wird abgelehnt.";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    public static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink;
    }

    /// <summary>Grenzen gegen Dekompressionsbomben. <paramref name="declaredLength"/> ist die
    /// <b>Behauptung</b> des Archivs — beim Auspacken wird zusätzlich mitgezählt.</summary>
    public static bool IsWithinSizeLimits(
        string entryName, long declaredLength, long compressedLength, out string? problem)
    {
        problem = null;
        if (declaredLength > BackupLayout.MaxEntryUncompressedBytes)
        {
            problem = $"Eintrag '{entryName}' ist entpackt größer als die zulässige Obergrenze.";
            return false;
        }

        if (declaredLength >= BackupLayout.RatioCheckThresholdBytes
            && compressedLength > 0
            && declaredLength / compressedLength > BackupLayout.MaxCompressionRatio)
        {
            problem = string.Format(
                CultureInfo.InvariantCulture,
                "Eintrag '{0}' entpackt sich um den Faktor {1} und gilt als Dekompressionsbombe.",
                entryName,
                declaredLength / compressedLength);
            return false;
        }

        return true;
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
