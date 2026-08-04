using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Governance.Tests;

/// <summary>
/// Der Bericht und seine Quelle dürfen nicht auseinanderlaufen.
/// <para>
/// <b>Warum ein Drifttest und kein Generatorlauf in der CI:</b> Ein Lauf, der die Datei still
/// nachzieht, versteckt die Änderung im nächsten Commit von jemand anderem. Ein Test, der rot wird,
/// zwingt die Änderung dorthin, wo sie hingehört — in die Quelle, mit einem Blick darauf.
/// </para>
/// </summary>
public sealed class FindingsReportTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// <b>Der Bericht auf der Platte ist der erzeugte.</b> Schlägt das hier fehl, wurde entweder die
    /// Quelle geändert und der Bericht nicht erzeugt — oder jemand hat den Bericht von Hand
    /// angefasst.
    /// </summary>
    /// <summary>
    /// Dasselbe Muster wie beim Kompatibilitätsbericht (WP5.1): Der Test erzeugt den Bericht nur,
    /// wenn jemand es ausdrücklich verlangt — sonst vergleicht er. Ein Lauf, der still nachzieht,
    /// versteckt die Änderung im nächsten Commit von jemand anderem.
    /// </summary>
    private const string UpdateVariable = "BIFROST_UPDATE_FINDINGS_REPORT";

    [Fact]
    public void The_report_on_disk_is_the_generated_one()
    {
        var expected = FindingsReport.Render(Load());

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FindingsReport.Path)!);
            File.WriteAllText(FindingsReport.Path, expected);
            return;
        }

        File.Exists(FindingsReport.Path).Should().BeTrue(
            $"der Bericht wird erzeugt: {UpdateVariable}=1 dotnet test tests/Bifrost.Governance.Tests");

        File.ReadAllText(FindingsReport.Path).ReplaceLineEndings("\n").Should().Be(expected,
            $"der Bericht ist eine Ableitung von findings.json und wird nie von Hand gepflegt "
            + $"(neu erzeugen: {UpdateVariable}=1)");
    }

    /// <summary>
    /// Die Zahl im Bericht ist keine Zierde: Wer nur die erste Zeile liest, muss den Zustand daran
    /// ablesen können.
    /// </summary>
    [Fact]
    public void The_report_names_the_blocking_count_in_its_first_lines()
    {
        var report = FindingsReport.Render(Load());
        var head = string.Join('\n', report.Split('\n').Take(14));

        head.Should().Contain("Offen:").And.Contain("Behoben:").And.Contain("Abdeckung ab:");
        head.Should().ContainAny("Kein offener Critical- oder High-Befund", "offene(r) Releaseblocker");
    }

    private static FindingsDocument Load()
        => JsonSerializer.Deserialize<FindingsDocument>(
            File.ReadAllText(Path.Combine(RepoTree.Root, "docs", "acceptance", "findings.json")),
            Json)
            ?? throw new InvalidOperationException("findings.json ist leer.");
}
