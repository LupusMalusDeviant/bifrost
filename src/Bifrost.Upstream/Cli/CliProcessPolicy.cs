using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Bifrost.Abstractions;
using Bifrost.Upstream.Isolation;

namespace Bifrost.Upstream.Cli;

internal sealed record ResolvedCliProcess(
    string Executable,
    string WorkingDirectory,
    Encoding Encoding);

internal static class CliProcessPolicy
{
    public static ResolvedCliProcess Resolve(CliTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Im Container-Modus liegt das Programm IM IMAGE, nicht auf diesem Dateisystem. Es gegen
        // Host-Pfade zu prüfen wäre nicht nur sinnlos, sondern falsch: Die Pfad-Policy des
        // Host-Modus (absolute Wurzeln, Hash-Pin) ist gerade das, was der Container ersetzt — die
        // Isolation kommt hier vom Container, nicht vom Pfad.
        if (options.Isolation is { Mode: IsolationMode.Container } container)
        {
            if (string.IsNullOrWhiteSpace(container.Image))
            {
                throw new ArgumentException("Container-Modus verlangt ein Image.", nameof(options));
            }

            if (options.ExecutableSha256 is not null)
            {
                throw new ArgumentException(
                    "ExecutableSha256 prüft eine Datei auf DIESEM Host; im Container-Modus liegt das "
                    + "Programm im Image. Der passende Pin ist ein Image-Digest.");
            }

            return new ResolvedCliProcess(
                options.Executable,
                // Das Arbeitsverzeichnis gilt im Container und wird dort gesetzt.
                options.WorkingDirectory ?? string.Empty,
                Encoding.GetEncoding(
                    options.OutputEncoding,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ReplacementFallback));
        }

        var executable = options.AllowPathLookup
            ? options.Executable
            : ResolveExistingPath(options.Executable, isDirectory: false);
        if (!options.AllowPathLookup)
        {
            var executableRoots = options.AllowedExecutableRoots is { Count: > 0 }
                ? ResolveRoots(options.AllowedExecutableRoots)
                : [Path.GetDirectoryName(executable)!];
            EnsureUnderRoots(executable, executableRoots, "Executable");
            VerifyExecutableHash(executable, options.ExecutableSha256);
        }
        else if (options.ExecutableSha256 is not null)
        {
            throw new ArgumentException(
                "ExecutableSha256 kann nicht zusammen mit unsicherer PATH-Auflösung verwendet werden.");
        }

        var workingDirectory = options.WorkingDirectory is { Length: > 0 } configuredDirectory
            ? ResolveExistingPath(configuredDirectory, isDirectory: true)
            : options.AllowPathLookup
                ? ResolveExistingPath(Path.GetTempPath(), isDirectory: true)
                : Path.GetDirectoryName(executable)!;
        var workingRoots = options.AllowedWorkingDirectoryRoots is { Count: > 0 }
            ? ResolveRoots(options.AllowedWorkingDirectoryRoots)
            : [workingDirectory];
        EnsureUnderRoots(workingDirectory, workingRoots, "WorkingDirectory");

        return new ResolvedCliProcess(
            executable,
            workingDirectory,
            Encoding.GetEncoding(
                options.OutputEncoding,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ReplacementFallback));
    }

    /// <summary>
    /// Baut den Start. Im Container-Modus gehört <paramref name="identity"/> dazu: Ohne Namen wäre
    /// der Container später nicht wiederfindbar, und das Abräumen bei Zeitüberschreitung oder
    /// Abbruch bliebe Raten.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(
        CliTransportOptions options, ResolvedCliProcess resolved, ContainerIdentity? identity = null)
    {
        var container = options.Isolation is { Mode: IsolationMode.Container } isolation
            ? isolation
            : null;
        if (container is not null && identity is null)
        {
            throw new ArgumentNullException(
                nameof(identity),
                "Ein Container ohne Namen liesse sich nicht abraeumen — 'docker run' ist ein Client, "
                + "kein Elternprozess.");
        }

        var startInfo = new ProcessStartInfo
        {
            // Im Container-Modus wird die Runtime gestartet, nicht das Programm — das Programm
            // steht am Ende der Argumentliste, hinter dem Image.
            FileName = container is null ? resolved.Executable : container.Runtime,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Das Arbeitsverzeichnis gilt im Container-Modus *innen* und wird dort gesetzt; der
            // Runtime-Prozess selbst startet neutral.
            WorkingDirectory = container is null ? resolved.WorkingDirectory : null,
            StandardOutputEncoding = resolved.Encoding,
            StandardErrorEncoding = resolved.Encoding,
        };

        startInfo.Environment.Clear();
        AddMinimalEnvironment(startInfo.Environment, options.AllowPathLookup);
        foreach (var (key, value) in options.EnvironmentVariables
                 ?? new Dictionary<string, string>())
        {
            ValidateEnvironmentName(key);
            // Auch im Container-Modus steht der Wert in der Umgebung des Runtime-Prozesses: Von
            // dort liest die Runtime ihn zu `--env NAME`, ohne dass er je in einer Kommandozeile
            // auftaucht.
            startInfo.Environment[key] = value;
        }

        if (container is not null)
        {
            // Ein Job je Aufruf: Der Container endet mit dem Kommando. Gebaut wird er von
            // derselben Stelle wie die stehende stdio-Sitzung — was die beiden unterscheidet, ist
            // die Lebensdauer, und die ist ein Parameter (ADR-0025 E5).
            var request = new ContainerLaunchRequest(
                container,
                identity!,
                ContainerLifetime.PerInvocation,
                ReadOnlyRoots: options.AllowedReadRoots,
                WritableRoots: options.AllowedWriteRoots,
                WorkingDirectory: options.WorkingDirectory,
                EnvironmentNames: [.. (options.EnvironmentVariables ?? new Dictionary<string, string>()).Keys]);
            foreach (var argument in ContainerLaunchPolicy.BuildRunArguments(request))
            {
                startInfo.ArgumentList.Add(argument);
            }

            // Hinter dem Image folgt das Programm; die Tool-Argumente hängt der Aufrufer an.
            startInfo.ArgumentList.Add(resolved.Executable);
        }

        return startInfo;
    }

    public static string ResolveArgumentPath(
        string value, CliPathAccess access, CliTransportOptions options)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"CLI-Pfad '{value}' muss absolut sein.");
        }

        var expectExisting = access == CliPathAccess.ReadOnly;
        var resolved = ResolvePath(value, expectExisting);
        var configuredRoots = access switch
        {
            CliPathAccess.ReadOnly => options.AllowedReadRoots,
            CliPathAccess.Write => options.AllowedWriteRoots,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };
        if (configuredRoots is not { Count: > 0 })
        {
            throw new ArgumentException(
                $"CLI-Pfadparameter '{value}' hat keine konfigurierte "
                + $"{(access == CliPathAccess.ReadOnly ? "Lese-" : "Schreib-")}Allowlist.");
        }

        EnsureUnderRoots(resolved, ResolveRoots(configuredRoots), "Pfadparameter");
        return resolved;
    }

    private static void AddMinimalEnvironment(
        IDictionary<string, string?> environment, bool allowPathLookup)
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        environment["TEMP"] = temp;
        environment["TMP"] = temp;

        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0)
            {
                environment["SystemRoot"] = windows;
                environment["WINDIR"] = windows;
            }
        }
        else
        {
            environment["LANG"] = "C.UTF-8";
            environment["LC_ALL"] = "C.UTF-8";
        }

        if (allowPathLookup && Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } path)
        {
            environment["PATH"] = path;
        }
    }

    private static void VerifyExecutableHash(string executable, string? expectedHash)
    {
        if (expectedHash is null)
        {
            return;
        }

        using var stream = File.OpenRead(executable);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Executable '{executable}' stimmt nicht mit dem konfigurierten SHA-256 überein.");
        }
    }

    private static void ValidateEnvironmentName(string name)
    {
        if (name.Length == 0
            || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Skip(1).Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException($"CLI-Environment-Name '{name}' ist ungültig.");
        }
    }

    private static string[] ResolveRoots(IReadOnlyList<string> roots)
        => [.. roots.Select(root => ResolveExistingPath(root, isDirectory: true))];

    private static string ResolveExistingPath(string value, bool isDirectory)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"Pfad '{value}' muss absolut sein.");
        }

        var full = Path.GetFullPath(value);
        if (isDirectory ? !Directory.Exists(full) : !File.Exists(full))
        {
            throw new FileNotFoundException($"CLI-Pfad '{full}' wurde nicht gefunden.", full);
        }

        return ResolveLinks(full);
    }

    private static string ResolvePath(string value, bool mustExist)
    {
        var full = Path.GetFullPath(value);
        if (mustExist && !File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException($"CLI-Pfad '{full}' wurde nicht gefunden.", full);
        }

        if (File.Exists(full) || Directory.Exists(full))
        {
            return ResolveLinks(full);
        }

        var parent = Path.GetDirectoryName(full)
            ?? throw new ArgumentException($"CLI-Pfad '{full}' hat kein Elternverzeichnis.");
        var resolvedParent = ResolveExistingPath(parent, isDirectory: true);
        return Path.Combine(resolvedParent, Path.GetFileName(full));
    }

    private static string ResolveLinks(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException($"CLI-Pfad '{fullPath}' hat keine Wurzel.");
        var current = root;
        foreach (var segment in fullPath[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!info.Exists)
            {
                continue;
            }

            if (info.LinkTarget is not null
                && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = Path.GetFullPath(target.FullName);
            }
        }

        return Path.GetFullPath(current);
    }

    private static void EnsureUnderRoots(
        string candidate, IReadOnlyList<string> roots, string subject)
    {
        if (roots.Any(root => IsUnder(candidate, root)))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            $"{subject} '{candidate}' liegt außerhalb der erlaubten Wurzeln.");
    }

    private static bool IsUnder(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (candidate.Equals(root, comparison))
        {
            return true;
        }

        var relative = Path.GetRelativePath(root, candidate);
        return relative.Length > 0
            && !Path.IsPathFullyQualified(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison);
    }
}
