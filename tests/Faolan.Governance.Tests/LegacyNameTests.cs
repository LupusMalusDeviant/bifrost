using AwesomeAssertions;
using Xunit;

namespace Faolan.Governance.Tests;

/// <summary>
/// Vertrag umbenennung-faolan §2.4 und §7: <b>Der alte Name steht nur noch, wo er stehen muss.</b>
/// <para>
/// Nach der Umbenennung bleibt „Bifrost“ an genau zwei Arten von Stellen: wo er Teil eines Formats
/// oder einer Schlüsselableitung ist (§2.4), und wo eine Brücke ihn absichtlich weiter liest
/// (§2.2, §2.3). Jede andere Fundstelle ist ein Rest, den die Umbenennung übersehen hat — oder einer,
/// den jemand später neu einschleppt. Ohne diesen Wächter fällt das erst auf, wenn ein Betreiber
/// den alten Namen in einer Meldung liest und ihn für den richtigen hält.
/// </para>
/// <para>
/// Geprüft werden Produktcode und Betreiber-Artefakte. Tests und Dokumentation sprechen über die
/// alten Namen, um sie zu prüfen und zu erklären; dort wäre eine Allowlist nur eine zweite Kopie
/// der Fundstellen.
/// </para>
/// </summary>
public sealed class LegacyNameTests
{
    /// <summary>
    /// Bezeichner, die an jeder Stelle stehen dürfen: Formate, Brücken und die Repo-Adresse, die
    /// erst mit R7 umzieht.
    /// </summary>
    private static readonly string[] AllowedTokens =
    [
        "bifrost.connector.v1",                                // §2.3 Paketschema
        "bifrost://assets/",                                   // §2.3 Asset-URI
        "${bifrost:secret/",                                   // §2.3 Export-Platzhalter
        "X-Bifrost-",                                          // §2.3 Kopfzeilen
        "__BifrostMigrationJournal",                           // §2.4 Tabelle
        "Bifrost.Server.ApprovalElicitation.RequestState.v1",  // §2.4 Schlüsselableitung
        "bifrost.db",                                          // §2.2 Datenbankdatei, §2.4 Archiveintrag
        "bifrost.dump",                                        // §2.4 Archiveintrag
        "_bifrost_truncated",                                  // §2.4 Marke bis zum Projektionsvertrag
        "bifrost-data",                                        // §3 Name des Bestandsvolumes
        "bifrost-pgdata",                                      // §3 ebenso für PostgreSQL
        "github.com/LupusMalusDeviant/bifrost",                // R7: Repo zieht erst nach dem Merge um
    ];

    /// <summary>Dateien, in denen der alte Name aus einem benannten Grund frei vorkommt.</summary>
    private static readonly Dictionary<string, string> AllowedFiles = new(StringComparer.Ordinal)
    {
        ["src/Faolan.Abstractions/EnvironmentNameChain.cs"] = "§2.2 Namenskette der Variablen",
        ["src/Faolan.Server/LegacyEnvironment.cs"] = "§2.2 Übernahme beim Serverstart",
        ["src/Faolan.Server/Program.cs"] = "§2.2 Startwarnung nennt die alten Präfixe",
        ["src/Faolan.Cli/Program.cs"] = "§2.2 Übernahme in der CLI",
        ["src/Faolan.Cli/Faolan.Cli.csproj"] = "§2.2 Kommentar zur geteilten Namenskette",
        ["src/Faolan.Core/Diagnostics/Checks/ConfigurationChecks.cs"] = "§2.2 BFR-CFG-0002 nennt die alten Präfixe",
        ["src/Faolan.Persistence/Startup/MigrationLock.cs"] = "§2.4 Sperrschlüssel „BIFROST“ + 1",
        ["src/Faolan.Upstream/Isolation/ContainerIdentity.cs"] = "§2.4 Container-Etiketten",
        ["spikes/wasi-component-runtime/src/disk_cache.rs"] = "§2.4 Cache-Kennung BIFROSTC",
        ["docker-compose.yml"] = "§3 Hinweis für Bestandsinstallationen am Volume",
        ["docker-compose.postgres.yml"] ="§3 POSTGRES_DB/POSTGRES_USER bleiben bifrost",
        [".env.example"] = "§3 Hinweise zum Bestandsvolume und zu den Postgres-Vorgaben",
        [".github/workflows/release.yml"] = "R7: cosign-Identität nimmt altes und neues Repo",
    };

    private static readonly string[] TextExtensions =
        [".cs", ".razor", ".csproj", ".props", ".targets", ".json", ".js", ".css", ".html", ".rs", ".toml", ".yml", ".yaml"];

    private static IEnumerable<(string Path, string Content)> ProductFiles()
    {
        foreach (var root in new[] { "src", "spikes/wasi-component-runtime/src", "samples", ".github/workflows" })
        {
            foreach (var file in RepoTree.Files(root, "*"))
            {
                if (TextExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }

        foreach (var name in new[]
        {
            "Dockerfile", "docker-compose.yml", "docker-compose.postgres.yml", "docker-compose.build.yml",
            ".env.example", "build.ps1", "build.sh", "Directory.Build.props", "Directory.Packages.props",
        })
        {
            var path = Path.Combine(RepoTree.Root, name);
            if (File.Exists(path))
            {
                yield return (name, File.ReadAllText(path));
            }
        }
    }

    private static bool MentionsOldName(string line)
        => line.Contains("bifrost", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedLine(string line)
    {
        var rest = line;
        foreach (var token in AllowedTokens)
        {
            rest = rest.Replace(token, string.Empty, StringComparison.Ordinal);
        }

        return !MentionsOldName(rest);
    }

    [Fact]
    public void The_old_name_appears_only_where_the_contract_keeps_it()
    {
        var violations = ProductFiles()
            .Where(file => !AllowedFiles.ContainsKey(file.Path))
            .SelectMany(file => file.Content.Split('\n')
                .Select((line, index) => (file.Path, Line: index + 1, Text: line.Trim()))
                .Where(entry => MentionsOldName(entry.Text) && !IsAllowedLine(entry.Text)))
            .Select(entry => $"{entry.Path}:{entry.Line}: {entry.Text}")
            .ToList();

        violations.Should().BeEmpty(
            "der alte Name gehört nur an die Stellen aus Vertrag umbenennung-faolan §2.2–§2.4; "
            + "ein neuer Grund kommt zuerst in den Vertrag und dann in diese Allowlist");
    }

    /// <summary>
    /// Eine Allowlist, deren Einträge nichts mehr erlauben, verdeckt den nächsten Rest. Fällt eine
    /// Fundstelle weg, fällt ihr Eintrag mit.
    /// </summary>
    [Fact]
    public void Every_allowed_file_still_needs_its_exception()
    {
        var files = ProductFiles().ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);

        var stale = AllowedFiles.Keys
            .Where(path => !files.TryGetValue(path, out var content)
                || !content.Split('\n').Any(line => MentionsOldName(line) && !IsAllowedLine(line)))
            .ToList();

        stale.Should().BeEmpty("ein Eintrag ohne Fundstelle gehört aus der Allowlist entfernt");
    }
}
