using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Governance.Tests;

/// <summary>
/// M7/WP7.1, letzter Punkt: „Changelog, Migration und Rollback vollständig."
/// <para>
/// <b>Die Gegenprüfung hat etwas gefunden</b>, und deshalb steht sie jetzt als Test da: Vier
/// Umgebungsvariablen waren nirgends in der Betriebsdokumentation — darunter
/// <c>BIFROST_ALLOW_HOST_EXECUTION</c>, die schärfste Option des Produkts, und die drei Schalter
/// der Approval-Benachrichtigung samt ihrem Signaturgeheimnis. Sie standen im ADR und im
/// Pflichtenheft, also dort, wo ein Betreiber nicht nachsieht.
/// </para>
/// </summary>
public sealed class ChangelogMigrationRollbackTests
{
    /// <summary>
    /// Wo ein Betreiber nachschlägt. Ein ADR ist eine Entscheidung, kein Handbuch — eine Option,
    /// die nur dort steht, gilt hier als undokumentiert.
    /// </summary>
    private static readonly string[] OperatingDocs =
    [
        "docs/operations.md",
        "docs/en/operations.md",
        "docs/gateway-cli.md",
        "docs/cli-installation.md",
        "docs/security.md",
        "README.md",
        "docker-compose.yml",
    ];

    /// <summary>
    /// Namen, die im Produktcode vorkommen, aber keine Betriebsoption sind: Präfixe, die zur
    /// Laufzeit zusammengesetzt werden, und die Regenerierungsschalter der Testartefakte.
    /// </summary>
    private static readonly string[] NotAnOption =
    [
        "BIFROST_UPDATE_",
        "BIFROST_RUN_",
        "BIFROST_REQUIRE_",
    ];

    private readonly ITestOutputHelper _output;

    public ChangelogMigrationRollbackTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// <b>Jede Betriebsoption steht in der Betriebsdokumentation.</b> Eine sicherheitsrelevante
    /// Option, die nur im Code steht, ist eine Falle: Sie wirkt, und niemand weiß es.
    /// </summary>
    [Fact]
    public void Every_environment_option_is_documented_where_an_operator_looks()
    {
        var pattern = new Regex(@"BIFROST_[A-Z0-9_]+", RegexOptions.Compiled);

        var used = RepoTree.CSharpFiles("src")
            .SelectMany(file => pattern.Matches(file.Content).Select(m => m.Value))
            .Where(name => !NotAnOption.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);

        var documentation = string.Join('\n', OperatingDocs
            .Select(relative => Path.Combine(RepoTree.Root, relative))
            .Where(File.Exists)
            .Select(File.ReadAllText));

        var missing = used.Where(name => !documentation.Contains(name, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        _output.WriteLine($"Betriebsoptionen im Produktcode: {used.Count}");

        missing.Should().BeEmpty(
            "eine Option, die nur im Code steht, wirkt trotzdem — und niemand weiss es:\n"
            + string.Join("\n", missing));
    }

    /// <summary>
    /// <b>Beide Anbieter tragen dieselben Migrationen.</b> Fehlt eine auf PostgreSQL, bricht dort
    /// der Start — und zwar erst beim Betreiber, weil die Entwicklung auf SQLite läuft.
    /// </summary>
    [Fact]
    public void Both_providers_carry_the_same_migrations()
    {
        var sqlite = MigrationNames("Sqlite");
        var postgres = MigrationNames("Postgres");

        _output.WriteLine($"Migrationen: SQLite {sqlite.Count} · PostgreSQL {postgres.Count}");

        sqlite.Should().NotBeEmpty();
        sqlite.Except(postgres).Should().BeEmpty("diese Migration fehlt auf PostgreSQL");
        postgres.Except(sqlite).Should().BeEmpty("diese Migration fehlt auf SQLite");
    }

    /// <summary>
    /// <b>Jede Migration hat einen Rückweg.</b> Ein leeres <c>Down</c> ist kein Rückweg, sondern
    /// eine Einbahnstraße mit einer Methode davor — und sie fällt erst auf, wenn jemand sie braucht.
    /// </summary>
    [Fact]
    public void Every_migration_has_a_rollback()
    {
        var pattern = new Regex(
            @"protected override void Down\(MigrationBuilder migrationBuilder\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var withoutWayBack = new List<string>();
        foreach (var (path, content) in MigrationFiles())
        {
            var match = pattern.Match(content);
            if (!match.Success || match.Groups["body"].Value.Trim().Length < 20)
            {
                withoutWayBack.Add(path);
            }
        }

        withoutWayBack.Should().BeEmpty(
            "ein leeres Down ist kein Rueckweg:\n" + string.Join("\n", withoutWayBack));
    }

    /// <summary>
    /// Der Changelog hat einen offenen Abschnitt, und er ist nicht leer. Mehr lässt sich mechanisch
    /// nicht prüfen — ob er das Richtige nennt, entscheidet ein Mensch. Was dieser Test verhindert,
    /// ist der Fall, in dem eine ganze Welle gar keinen Eintrag bekommen hat.
    /// </summary>
    [Fact]
    public void The_changelog_has_an_unreleased_section_with_content()
    {
        var changelog = File.ReadAllText(Path.Combine(RepoTree.Root, "CHANGELOG.md"));

        var start = changelog.IndexOf("## [Unveröffentlicht]", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "ein Changelog ohne offenen Abschnitt sammelt nichts");

        var next = changelog.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = next < 0 ? changelog[start..] : changelog[start..next];

        section.Should().Contain("### ", "der offene Abschnitt braucht Rubriken (Hinzugefuegt/Behoben/...)");
        section.Length.Should().BeGreaterThan(500, "ein offener Abschnitt mit drei Zeilen ist keiner");
    }

    private static List<string> MigrationNames(string provider)
        => [.. MigrationFiles()
            .Where(f => f.Path.Contains($"Migrations.{provider}", StringComparison.Ordinal))
            .Select(f => Path.GetFileNameWithoutExtension(f.Path).Split('_', 2)[^1])
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Die Migrationsdateien ohne die <c>.Designer.cs</c>-Zwillinge und ohne den Modellabzug — nur
    /// die Klassen mit <c>Up</c> und <c>Down</c>.
    /// </summary>
    private static List<(string Path, string Content)> MigrationFiles()
        => [.. RepoTree.CSharpFiles("src")
            .Where(f => f.Path.Contains("/Migrations/", StringComparison.Ordinal))
            .Where(f => !f.Path.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f.Path).Contains('_', StringComparison.Ordinal))];
}
