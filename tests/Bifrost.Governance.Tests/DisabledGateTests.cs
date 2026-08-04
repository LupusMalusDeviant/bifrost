using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Governance.Tests;

/// <summary>
/// M7/WP7.1: <b>Kein still abgeschaltetes Tor.</b>
/// <para>
/// Das Pflichtenheft verlangt „keine temporär deaktivierten Gates". Ohne Prüfer ist das eine
/// Absichtserklärung — und zwar eine, die genau dann bricht, wenn es eilig ist. Hier steht der
/// Prüfer.
/// </para>
/// <para>
/// <b>Jeder Verstoß ist ein Fehlschlag, keine Warnung.</b> Ein Wächter, der warnt, wird
/// weggeklickt; einer, der den Lauf rot macht, wird entweder befolgt oder begründet geändert. Beides
/// ist besser als eine Regel, an die sich niemand erinnert.
/// </para>
/// </summary>
public sealed class DisabledGateTests
{
    private readonly ITestOutputHelper _output;

    public DisabledGateTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// <b>Ein Skip gehört an eine Bedingung, nicht an ein Attribut.</b>
    /// <para>
    /// <c>[Fact(Skip = "…")]</c> läuft nie wieder an. Es gibt keinen Lauf, der ihn zurückholt, keine
    /// Umgebung, in der er greift, und keine Meldung, die daran erinnert — er ist ein gelöschter
    /// Test, der wie ein vorhandener aussieht. <c>Assert.SkipUnless</c> dagegen nennt die Bedingung
    /// und läuft, sobald sie erfüllt ist.
    /// </para>
    /// </summary>
    [Fact]
    public void No_test_is_switched_off_by_an_attribute()
    {
        var pattern = new Regex(@"\[(Fact|Theory)\s*\(\s*Skip\s*=", RegexOptions.Compiled);

        var violations = LineScanner
            .Scan(RepoTree.CSharpFiles("tests"), pattern, (_, _) => false)
            .ToList();

        violations.Should().BeEmpty(
            "ein Attribut-Skip laeuft nie wieder an — die Bedingung gehoert in den Test "
            + "(Assert.SkipUnless):\n" + string.Join("\n", violations));
    }

    /// <summary>
    /// <b>Jede Gate-Variable wird von einem CI-Job gesetzt.</b>
    /// <para>
    /// Die Regel steht seit dem M5-Vertrag §1.2. Sie ist die Antwort auf einen einfachen
    /// Missbrauch: Wer einen unbequemen Nachweis hinter eine Variable hängt, die niemand setzt, hat
    /// ihn abgeschaltet und es sieht aus wie Sorgfalt. Hier wird sie geprüft statt eingehalten.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_gate_variable_is_set_by_a_workflow()
    {
        var pattern = new Regex(@"BIFROST_(RUN|REQUIRE)_[A-Z0-9_]+", RegexOptions.Compiled);

        var inTests = RepoTree.CSharpFiles("tests")
            .SelectMany(file => pattern.Matches(file.Content).Select(m => m.Value))
            .ToHashSet(StringComparer.Ordinal);

        var inWorkflows = RepoTree.Files(".github/workflows", "*.yml")
            .SelectMany(file => pattern.Matches(file.Content).Select(m => m.Value))
            .ToHashSet(StringComparer.Ordinal);

        _output.WriteLine($"Tore im Testcode: {string.Join(", ", inTests.OrderBy(x => x, StringComparer.Ordinal))}");

        inTests.Except(inWorkflows).Should().BeEmpty(
            "ein Tor, das kein Job setzt, ist ein abgeschalteter Nachweis (M5-Vertrag §1.2)");
    }

    /// <summary>
    /// <b>Und jedes Tor steht im Beitragsleitfaden.</b>
    /// <para>
    /// Ein CI-Job allein reicht nicht: Wer das Repository zum ersten Mal auscheckt, liest keinen
    /// Workflow, sondern <c>CONTRIBUTING</c>. Steht das Tor dort nicht, faehrt er den Nachweis nie
    /// — und haelt einen uebersprungenen Test fuer einen bestandenen. Genau das war hier der Fall:
    /// Die Tabelle nannte drei von neun Toren.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_gate_variable_is_named_in_the_contributing_guide()
    {
        var pattern = new Regex(@"BIFROST_(RUN|REQUIRE)_[A-Z0-9_]+", RegexOptions.Compiled);

        var inTests = RepoTree.CSharpFiles("tests")
            .SelectMany(file => pattern.Matches(file.Content).Select(m => m.Value))
            .ToHashSet(StringComparer.Ordinal);

        var guide = File.ReadAllText(Path.Combine(RepoTree.Root, ".github", "CONTRIBUTING.md"));

        inTests.Where(gate => !guide.Contains(gate, StringComparison.Ordinal))
            .Should().BeEmpty("wer das Repository auscheckt, liest CONTRIBUTING und keinen Workflow");
    }

    /// <summary>
    /// <b>Keine Unterdrückung ohne Begründung.</b> Ein <c>#pragma warning disable</c> ohne einen
    /// Satz daneben ist nicht überprüfbar: Der nächste Leser weiß nicht, ob die Regel hier falsch
    /// ist oder ob jemand es eilig hatte.
    /// </summary>
    [Fact]
    public void Every_suppression_says_why()
    {
        var pattern = new Regex(@"#pragma\s+warning\s+disable", RegexOptions.Compiled);

        var violations = LineScanner
            .Scan(
                RepoTree.CSharpFiles("src", "tests").Where(f => !RepoTree.IsGenerated(f.Path, f.Content)),
                pattern,
                (lines, index) =>
                    lines[index].Split("disable", 2)[1].Contains("//", StringComparison.Ordinal)
                    || (index > 0 && lines[index - 1].TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .ToList();

        violations.Should().BeEmpty(
            "eine Unterdrueckung ohne Begruendung ist nicht nachpruefbar:\n"
            + string.Join("\n", violations));
    }

    /// <summary>
    /// Dasselbe für <c>NoWarn</c> im Projektsystem — dort wirkt eine Unterdrückung sogar über eine
    /// ganze Assembly hinweg, also weiter als jedes Pragma.
    /// </summary>
    [Fact]
    public void Every_project_wide_suppression_says_why()
    {
        var pattern = new Regex(@"<NoWarn>", RegexOptions.Compiled);
        var files = RepoTree.Files(".", "*.csproj").Concat(RepoTree.Files(".", "*.props"));

        var violations = LineScanner
            .Scan(files, pattern, (lines, index) => HasCommentAbove(lines, index))
            .ToList();

        violations.Should().BeEmpty(
            "NoWarn wirkt fuer die ganze Assembly und braucht erst recht einen Grund:\n"
            + string.Join("\n", violations));
    }

    /// <summary>
    /// Ein XML-Kommentar darf mehrere Zeilen über der Angabe beginnen — gesucht wird deshalb nach
    /// oben, bis etwas kommt, das kein Kommentar und keine Leerzeile ist.
    /// </summary>
    private static bool HasCommentAbove(string[] lines, int index)
    {
        for (var i = index - 1; i >= 0 && index - i <= 6; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            return line.StartsWith("<!--", StringComparison.Ordinal)
                || line.EndsWith("-->", StringComparison.Ordinal);
        }

        return false;
    }
}
