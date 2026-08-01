using Bifrost.Abstractions.Importing;

namespace Bifrost.Core.Tests.Importing.Providers;

/// <summary>
/// Der gemeinsame Aufbau der Providertests: die versionierten Beispielkonfigurationen.
/// <para>
/// <b>Warum die Beispiele als Dateien im Arbeitsbaum liegen und nicht als Zeichenketten im Test:</b>
/// Sie sind Belege. Eine Datei lässt sich mit dem vergleichen, was ein Client wirklich schreibt; ein
/// String in einer Testmethode wird beim nächsten Umbau mitgeändert, bis er nur noch das belegt, was
/// der Parser ohnehin kann. Jede Datei trägt in ihrem Kopf, woher ihr Aufbau stammt und was daran
/// nachgebildet ist.
/// </para>
/// </summary>
internal static class ProviderWorld
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>Das Verzeichnis der Beispielkonfigurationen.</summary>
    public static string Directory => Path.Combine(
        RootPath.Value, "tests", "Bifrost.Core.Tests", "Importing", "Fixtures");

    /// <summary>Der Inhalt einer Beispielkonfiguration.</summary>
    public static string Fixture(string client, string name)
    {
        var path = Path.Combine(Directory, client, name);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Die Beispielkonfiguration '{path}' fehlt. Ein Test, der seinen Beleg nicht findet, "
                + "prueft nichts — das soll auffallen.");
        }

        return File.ReadAllText(path);
    }

    /// <summary>Alle Beispielkonfigurationen eines Clients, in Dateinamensreihenfolge.</summary>
    public static IReadOnlyList<string> Names(string client)
        => [.. System.IO.Directory
            .EnumerateFiles(Path.Combine(Directory, client), "*.json")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)];

    /// <summary>Die vier Clients, für die es Beispielkonfigurationen gibt.</summary>
    public static IReadOnlyList<string> Clients { get; } = ["claude", "codex", "cursor", "vscode"];

    /// <summary>Alle Beispielkonfigurationen aller Clients als <c>client/name</c>.</summary>
    public static IReadOnlyList<string> All()
        => [.. Clients.SelectMany(client => Names(client).Select(name => $"{client}/{name}"))];

    /// <summary>Der Inhalt zu einer Angabe aus <see cref="All"/>.</summary>
    public static string Load(string reference)
    {
        var parts = reference.Split('/', 2);
        return Fixture(parts[0], parts[1]);
    }

    /// <summary>Die Codes aller Befunde eines Plans — die des Plans und die der Kandidaten.</summary>
    public static IReadOnlyList<string> Codes(this ImportPlan plan)
        => [.. plan.Findings.Concat(plan.Candidates.SelectMany(c => c.Findings)).Select(f => f.Code)];

    /// <summary>Alle Befunde eines Plans, egal auf welcher Ebene sie stehen.</summary>
    public static IReadOnlyList<ImportFinding> Everything(this ImportPlan plan)
        => [.. plan.Findings.Concat(plan.Candidates.SelectMany(c => c.Findings))];

    /// <summary>Alle Zugangsdaten, die die Kandidaten eines Plans nennen.</summary>
    public static IReadOnlyList<ImportSecret> Secrets(this ImportPlan plan)
        => [.. plan.Candidates.SelectMany(c => c.Secrets)];

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
}
