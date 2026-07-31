using System.Text.RegularExpressions;

namespace Bifrost.Security.Tests.Infrastructure;

/// <summary>
/// Zugriff auf den Quelltext unter <c>src/</c> — die Grundlage der Architekturtests, die eine
/// Regel ueber <b>Stellen</b> pruefen und nicht ueber Werte.
/// <para>
/// <b>Warum Quelltext und nicht IL:</b> Die Aussage dieser Tests lautet „an dieser Datei muss beim
/// Hinzufuegen etwas auffallen". Sie muss eine Datei benennen koennen, in die jemand hineinsieht.
/// Eine IL-Analyse braeuchte einen Decompiler als Abhaengigkeit und meldete am Ende eine
/// Methode ohne Zeilennummer — die Fundstelle waere schlechter, die Aussage dieselbe.
/// </para>
/// </summary>
public static class RepositorySources
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>Wurzel des Arbeitsbaums, erkannt an <c>Bifrost.slnx</c>.</summary>
    public static string Root => RootPath.Value;

    private static readonly Lazy<IReadOnlyList<SourceFile>> ProductionSources =
        new(() => Load(Path.Combine(Root, "src")));

    /// <summary>Alle <c>.cs</c>-Dateien unter <c>src/</c>, ohne <c>bin/</c> und <c>obj/</c>.</summary>
    public static IReadOnlyList<SourceFile> Production => ProductionSources.Value;

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bifrost.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Arbeitsbaum nicht gefunden — ueber '{AppContext.BaseDirectory}' liegt keine Bifrost.slnx.");
    }

    private static SourceFile[] Load(string directory)
    {
        var files = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => new SourceFile(
                Path.GetRelativePath(Root, path).Replace('\\', '/'),
                File.ReadAllLines(path)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"Unter '{directory}' liegt keine Quelldatei — der Test haette nichts geprueft.");
        }

        return files;
    }

    /// <summary>
    /// Sucht ein Muster in allen Produktionsquellen und liefert die Fundstellen als
    /// <c>pfad:zeile</c> zurueck. Kommentarzeilen werden uebersprungen: Die Regeln dieses Projekts
    /// werden in den Kommentaren <em>beschrieben</em>, und eine Beschreibung ist kein Aufruf.
    /// </summary>
    public static IReadOnlyList<Hit> Find(Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var hits = new List<Hit>();
        foreach (var file in Production)
        {
            for (var index = 0; index < file.Lines.Count; index++)
            {
                var line = file.Lines[index];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith('*')
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (pattern.IsMatch(line))
                {
                    hits.Add(new Hit(file.RelativePath, index + 1, trimmed));
                }
            }
        }

        return hits;
    }
}

/// <param name="RelativePath">Pfad ab Arbeitsbaumwurzel, immer mit <c>/</c>.</param>
public sealed record SourceFile(string RelativePath, IReadOnlyList<string> Lines);

/// <param name="Line">1-basiert, damit die Meldung in einem Editor anspringbar ist.</param>
public sealed record Hit(string File, int Line, string Text)
{
    public override string ToString() => $"{File}:{Line}  {Text}";
}
