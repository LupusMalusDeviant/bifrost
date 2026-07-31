namespace Bifrost.Upstream.Isolation;

/// <summary>
/// Prüft und kanonisiert die Pfade, die in einen Container eingehängt werden (ADR-0018).
/// <para>
/// <b>Standardmäßig read-only.</b> Schreibbar wird ein Pfad nur, weil er ausdrücklich in der
/// Schreib-Allowlist steht — nicht, weil er in beiden Listen auftaucht oder weil die Reihenfolge
/// günstig war.
/// </para>
/// <para>
/// <b>Warum kanonisiert wird:</b> Ein <c>--volume</c>-Argument mit <c>..</c> darin hängt etwas
/// anderes ein, als dasteht, und ein Symlink verschiebt den Mount an sein Ziel. Beides sind Wege,
/// eine Allowlist einzuhalten und trotzdem woanders zu landen.
/// </para>
/// </summary>
internal static class ContainerMountPolicy
{
    /// <summary>
    /// Baut die <c>--volume</c>-Argumente. Ein Pfad, der in beiden Listen steht, wird
    /// <b>einmal</b> und schreibbar eingehängt — zwei Mounts auf dasselbe Ziel lehnt die Runtime
    /// ab, und die Lesefassung zu bevorzugen wäre eine stille Abweichung von der Konfiguration.
    /// </summary>
    public static IReadOnlyList<string> Build(
        IReadOnlyList<string>? readOnlyRoots, IReadOnlyList<string>? writableRoots)
    {
        var arguments = new List<string>();
        var writable = new HashSet<string>(
            (writableRoots ?? []).Select(root => Canonicalize(root, "Schreib-Wurzel")),
            StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in readOnlyRoots ?? [])
        {
            var canonical = Canonicalize(root, "Lese-Wurzel");
            if (writable.Contains(canonical) || !seen.Add(canonical))
            {
                continue;
            }

            arguments.Add("--volume");
            arguments.Add($"{canonical}:{canonical}:ro");
        }

        foreach (var canonical in writable.OrderBy(value => value, StringComparer.Ordinal))
        {
            arguments.Add("--volume");
            arguments.Add($"{canonical}:{canonical}:rw");
        }

        return arguments;
    }

    /// <summary>
    /// Kanonische Prüfung eines Mount-Pfades. Wirft bei allem, was nicht eindeutig auf genau ein
    /// Verzeichnis zeigt.
    /// </summary>
    public static string Canonicalize(string root, string subject)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException(
                $"{subject}: ein leerer Mount-Pfad ist kein Pfad.", nameof(root));
        }

        var value = root.Trim();

        // Absolut heisst hier: POSIX-absolut (/daten) ODER plattform-absolut (C:\daten). Die erste
        // Form ist die im Container gueltige und muss auch dann durchgehen, wenn der Gateway unter
        // Windows laeuft — sonst waere eine Linux-Konfiguration auf einem Windows-Host nicht mehr
        // pruefbar, nur noch ungeprueft.
        var posixAbsolute = value.StartsWith('/');
        if (!posixAbsolute && !Path.IsPathFullyQualified(value))
        {
            throw new UnauthorizedAccessException(
                $"{subject} '{root}' ist nicht absolut. Ein relativer Pfad bezoege sich auf das "
                + "Arbeitsverzeichnis der Runtime und traefe im Container etwas anderes als gedacht.");
        }

        foreach (var segment in value.Split('/', '\\'))
        {
            if (segment == "..")
            {
                throw new UnauthorizedAccessException(
                    $"{subject} '{root}' enthaelt '..'. Ein Mount, der aus sich herauszeigt, haelt "
                    + "die Allowlist ein und landet trotzdem woanders.");
            }
        }

        if (!posixAbsolute)
        {
            // Nur fuer Pfade dieses Hosts: Symlinks aufloesen, damit der Mount dorthin zeigt, wo er
            // wirklich hinzeigt. Ein POSIX-Pfad gehoert dem Container und laesst sich hier nicht
            // aufloesen — ihn gegen das Wirtsdateisystem zu pruefen waere eine erfundene Aussage.
            value = ResolveOnThisHost(value);
        }

        return value.TrimEnd('/', '\\') is { Length: > 0 } trimmed ? trimmed : value;
    }

    private static string ResolveOnThisHost(string value)
    {
        var full = Path.GetFullPath(value);
        if (!Directory.Exists(full))
        {
            // Nicht vorhanden ist kein Fehler dieser Pruefung: Die Runtime sagt es deutlicher, und
            // eine Mount-Wurzel darf auf einem anderen Rechner liegen als der Pruefflauf.
            return full;
        }

        var info = new DirectoryInfo(full);
        return info.LinkTarget is not null
            && info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? Path.GetFullPath(target.FullName)
                : full;
    }
}
