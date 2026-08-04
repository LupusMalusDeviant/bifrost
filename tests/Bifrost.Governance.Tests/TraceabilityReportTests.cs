using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Governance.Tests;

/// <summary>
/// Bericht und Quelle dürfen nicht auseinanderlaufen — dieselbe Regel und dasselbe Mittel wie beim
/// Kompatibilitäts- und beim Findingbericht.
/// </summary>
public sealed class TraceabilityReportTests
{
    private const string UpdateVariable = "BIFROST_UPDATE_TRACEABILITY_REPORT";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Fact]
    public void The_report_on_disk_is_the_generated_one()
    {
        var expected = TraceabilityReport.Render(Load());

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TraceabilityReport.Path)!);
            File.WriteAllText(TraceabilityReport.Path, expected);
            return;
        }

        File.Exists(TraceabilityReport.Path).Should().BeTrue(
            $"der Bericht wird erzeugt: {UpdateVariable}=1 dotnet test tests/Bifrost.Governance.Tests");

        File.ReadAllText(TraceabilityReport.Path).ReplaceLineEndings("\n").Should().Be(expected,
            $"der Bericht ist eine Ableitung von traceability.json (neu erzeugen: {UpdateVariable}=1)");
    }

    /// <summary>
    /// Die Zahl der offenen Must-Anforderungen steht oben. Wer nur den Anfang liest, muss den Stand
    /// daran ablesen können — genau dafür ist eine Rückverfolgung da.
    /// </summary>
    [Fact]
    public void The_report_names_the_open_must_count_up_front()
    {
        var head = string.Join('\n', TraceabilityReport.Render(Load()).Split('\n').Take(16));

        head.Should().Contain("Must ohne");
        head.Should().ContainAny("Alle Must-Anforderungen sind erfüllt", "noch nicht erfüllt");
    }

    private static TraceabilityDocument Load()
        => JsonSerializer.Deserialize<TraceabilityDocument>(
            File.ReadAllText(Path.Combine(RepoTree.Root, "docs", "acceptance", "traceability.json")),
            Json)
            ?? throw new InvalidOperationException("traceability.json ist leer.");
}
