using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Faolan.Governance.Tests;

/// <summary>
/// Die Pfad-Ausnahmen des Secret-Gates (<c>.github/gitleaks.toml</c>) sind so eng wie beschrieben.
/// <para>
/// Zwei Fehler, die erst ein Lauf auf main zeigt, weil gitleaks im Pull Request nur den Diff prüft:
/// Ein Muster, das nur den heutigen Projektnamen kennt, lässt die Commits vor der Umbenennung rot
/// werden (27. November 2026). Und ein Muster ohne Anker am Anfang nimmt auch Kopien unter einem
/// tieferen Pfad aus, die niemand geprüft hat — gitleaks sucht mit einer Teilstring-Suche.
/// </para>
/// </summary>
public sealed class GitleaksAllowlistTests
{
    /// <summary>Die Dateien, die einzeln geprüft und ausgenommen sind — relativ zu <c>tests/&lt;Projektname&gt;.</c>.</summary>
    private static readonly string[] ReviewedFiles =
    [
        "Core.Tests/Configuration/ConfigurationFixtures.cs",
        "Core.Tests/Diagnostics/DiagnosticRedactionTests.cs",
        "Integration.Tests/Operations/OperationsApiTests.cs",
        "Core.Tests/Guardrails/SecretGuardTests.cs",
        "Core.Tests/Webhooks/WebhookSignatureTests.cs",
        "Core.Tests/Invocation/TracingTests.cs",
        "Integration.Tests/Gateway/ContainerIsolationE2ETests.cs",
        "Integration.Tests/Persistence/PersistenceTestsBase.cs",
    ];

    [Fact]
    public void Every_pattern_is_anchored_at_both_ends()
    {
        var patterns = Patterns();

        patterns.Should().HaveCount(ReviewedFiles.Length, "jede geprüfte Datei hat genau einen Eintrag");
        patterns.Should().OnlyContain(p => p.StartsWith('^') && p.EndsWith('$'),
            "gitleaks sucht den Treffer irgendwo im Pfad; ohne beide Anker ist die Ausnahme weiter als die Prüfung");
    }

    [Theory]
    [InlineData("Bifrost")]
    [InlineData("Faolan")]
    public void The_reviewed_files_are_exempt_under_both_project_names(string project)
    {
        var patterns = Compiled();

        foreach (var file in ReviewedFiles)
        {
            var path = $"tests/{project}.{file}";
            patterns.Count(p => p.IsMatch(path)).Should().Be(1, $"{path} ist geprüft und genau einmal ausgenommen");
        }
    }

    [Theory]
    [InlineData("archiv/tests/Bifrost.Core.Tests/Configuration/ConfigurationFixtures.cs")]
    [InlineData("src/tests/Faolan.Core.Tests/Guardrails/SecretGuardTests.cs")]
    [InlineData("tests/Faolan.Core.Tests/Configuration/ConfigurationFixtures.cs.bak")]
    [InlineData("tests/Faolan.Core.Tests/Configuration/Andere.cs")]
    [InlineData("tests/McpMcp.Core.Tests/Configuration/ConfigurationFixtures.cs")]
    [InlineData("tests/FaolanXCore.Tests/Configuration/ConfigurationFixtures.cs")]
    public void Nothing_else_is_exempt(string path)
        => Compiled().Should().NotContain(p => p.IsMatch(path), $"{path} wurde nie geprüft");

    private static List<Regex> Compiled()
        => Patterns().Select(p => new Regex(p, RegexOptions.CultureInvariant)).ToList();

    /// <summary>Die Einträge von <c>[allowlist] paths</c>: TOML-Literale in drei einfachen Anführungszeichen.</summary>
    private static List<string> Patterns()
    {
        var text = File.ReadAllText(Path.Combine(RepoTree.Root, ".github", "gitleaks.toml"));
        var start = text.IndexOf("paths = [", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "die Konfiguration hat eine Pfad-Ausnahmeliste");
        var end = text.IndexOf("\n]", start, StringComparison.Ordinal);

        return Regex.Matches(text[start..end], "^\\s*'''(.+)''',\\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.TrimEnd('\r'))
            .ToList();
    }
}
