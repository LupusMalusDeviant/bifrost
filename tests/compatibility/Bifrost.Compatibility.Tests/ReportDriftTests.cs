using AwesomeAssertions;
using Bifrost.Compatibility.Tests.Matrix;
using Xunit;

namespace Bifrost.Compatibility.Tests;

/// <summary>
/// Der veröffentlichte Bericht ist eine Ableitung der Matrix — und dieser Test ist der Grund, warum
/// er es bleibt. Ohne ihn wäre „generiert" eine Absichtserklärung: Der erste, der die Tabelle von
/// Hand nachzieht, hat eine zweite Wahrheit erzeugt, und auffallen würde es erst, wenn beide sich
/// widersprechen.
/// </summary>
public sealed class ReportDriftTests
{
    private const string UpdateVariable = "BIFROST_UPDATE_COMPAT_REPORT";

    [Fact]
    public void The_published_report_matches_the_matrix()
    {
        var matrix = CompatibilityMatrixFile.Read(CompatibilityPaths.MatrixFile);
        var expected = CompatibilityReportGenerator.Generate(matrix);
        var path = CompatibilityPaths.ReportFile;

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"der Bericht wird erzeugt: {UpdateVariable}=1 dotnet test tests/compatibility/Bifrost.Compatibility.Tests");

        File.ReadAllText(path).ReplaceLineEndings("\n").Should().Be(
            expected,
            $"der Bericht ist eine Ableitung der Matrix und wird nie von Hand gepflegt "
            + $"(neu erzeugen: {UpdateVariable}=1)");
    }
}
