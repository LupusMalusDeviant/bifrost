namespace Bifrost.Core.Operations;

/// <summary>
/// Wo Sicherungen liegen dürfen, wenn jemand von außen einen Pfad nennt — über REST, die CLI oder die
/// Oberfläche. Eine Stelle für alle drei Wege, damit nicht einer davon weiter überall schreibt.
/// <para>
/// <b>Warum es diese Grenze gibt.</b> Ohne sie liest „Archiv prüfen" jede Datei, die der Gateway
/// lesen darf, und „Sicherung anlegen" schreibt überallhin, wo er schreiben darf. Das ist nur einem
/// Admin möglich, aber die Antwort verriet trotzdem, ob es eine Datei gibt und ob sie ein Zip ist —
/// und eine Sicherung über eine fremde Datei zu legen, ist kein Bedienfehler, den man zulassen muss.
/// </para>
/// <para>
/// Der Dienst darunter (<c>IBackupService</c>) bleibt bei vollen Pfaden: Er ist das Werkzeug, das
/// auch die Vor-Migrations-Sicherung und die Upgrade-Prüfungen benutzen. Die Grenze gehört an den
/// Rand, an dem Pfade von außen hereinkommen.
/// </para>
/// </summary>
public sealed class BackupLocation
{
    /// <summary>Die Betreiber-Einstellung für das Sicherungsverzeichnis.</summary>
    public const string SettingName = "BIFROST_BACKUP_DIR";

    private readonly IReadOnlyList<string> _readRoots;

    /// <param name="directory">Das Sicherungsverzeichnis; hier entstehen neue Sicherungen.</param>
    /// <param name="additionalReadDirectories">
    /// Weitere Verzeichnisse, aus denen nur <em>gelesen</em> wird — die Sicherungen, die vor einem
    /// Restore oder einer Migration von selbst entstehen. Wer <see cref="SettingName"/> woandershin
    /// zeigen lässt, soll diese trotzdem prüfen und zurückspielen können.
    /// </param>
    public BackupLocation(string directory, params string[] additionalReadDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
        _readRoots = [Directory, .. additionalReadDirectories
            .Where(extra => !string.IsNullOrWhiteSpace(extra))
            .Select(Path.GetFullPath)
            .Where(extra => !SamePath(extra, Directory))];
    }

    /// <summary>Das Sicherungsverzeichnis.</summary>
    public string Directory { get; }

    /// <summary>
    /// Das Ziel einer neuen Sicherung. Ein bloßer Name landet im Sicherungsverzeichnis; ein Pfad muss
    /// darin liegen.
    /// </summary>
    /// <exception cref="ArgumentException">Der Pfad liegt außerhalb.</exception>
    public string ResolveTarget(string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        var full = Path.GetFullPath(Path.Combine(Directory, requested));
        if (!IsUnder(full, Directory))
        {
            throw new ArgumentException(Outside(requested, [Directory]), nameof(requested));
        }

        return full;
    }

    /// <summary>
    /// Ein vorhandenes Archiv. Zurück kommt der Pfad, wie ihn das Dateisystem beim Durchsuchen der
    /// Sicherungsverzeichnisse liefert — nicht der übergebene Wert. Was danach geöffnet wird, stammt
    /// damit nachweislich aus dem Verzeichnis und nicht aus der Anfrage.
    /// </summary>
    /// <exception cref="ArgumentException">Außerhalb oder nicht vorhanden.</exception>
    public string ResolveExisting(string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        var candidates = _readRoots
            .Select(root => (Root: root, Full: Path.GetFullPath(Path.Combine(root, requested))))
            .Where(candidate => IsUnder(candidate.Full, candidate.Root))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new ArgumentException(Outside(requested, _readRoots), nameof(requested));
        }

        foreach (var (root, full) in candidates)
        {
            if (!System.IO.Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (SamePath(file, full))
                {
                    return file;
                }
            }
        }

        throw new ArgumentException(
            $"Das Archiv '{requested}' gibt es im Sicherungsverzeichnis nicht.", nameof(requested));
    }

    private static string Outside(string requested, IEnumerable<string> roots) =>
        $"'{requested}' liegt außerhalb des Sicherungsverzeichnisses ({string.Join(", ", roots)}). "
        + $"Sicherungen entstehen und liegen nur dort; das Verzeichnis legt {SettingName} fest.";

    private static bool IsUnder(string full, string root)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Comparison);
    }

    private static bool SamePath(string left, string right)
        => Path.GetFullPath(left).Equals(Path.GetFullPath(right), Comparison);

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
