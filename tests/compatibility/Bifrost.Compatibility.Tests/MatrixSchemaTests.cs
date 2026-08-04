using AwesomeAssertions;
using Bifrost.Compatibility.Tests.Matrix;
using Xunit;

namespace Bifrost.Compatibility.Tests;

/// <summary>
/// PR-Pflicht: Die eingecheckte Matrix hält den Vertrag ein. Dieser Test ist der Grund, warum keine
/// öffentliche Kompatibilitätsaussage ohne Beleg entstehen kann (M5-Vertrag §5, DoD WP5.1).
/// </summary>
public sealed class MatrixSchemaTests
{
    [Fact]
    public void The_checked_in_matrix_satisfies_the_contract()
    {
        var matrix = CompatibilityMatrixFile.Read(CompatibilityPaths.MatrixFile);

        var errors = CompatibilityMatrixValidator.Validate(matrix);

        errors.Should().BeEmpty(
            "die Matrix ist die Quelle jeder Kompatibilitaetsaussage:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Jeder Beleg muss auffindbar sein. Ein Protokollpfad, der ins Leere zeigt, ist genau die Art
    /// Beleg, die man erst dann prüft, wenn jemand widerspricht — also nie.
    /// </summary>
    [Fact]
    public void Every_manual_protocol_reference_points_at_a_file_that_exists()
    {
        var matrix = CompatibilityMatrixFile.Read(CompatibilityPaths.MatrixFile);

        var missing = matrix.Entries
            .Where(e => e.Evidence?.Kind == "manual")
            .Select(e => (e.Key, Path: Path.Combine(CompatibilityPaths.RepoRoot, e.Evidence!.Ref)))
            .Where(x => !File.Exists(x.Path))
            .ToList();

        missing.Should().BeEmpty(
            "ein Protokoll, das es nicht gibt, ist kein Nachweis: "
            + string.Join(", ", missing.Select(x => $"{x.Key} → {x.Path}")));
    }

    [Fact]
    public void Every_mandatory_client_is_listed()
    {
        var matrix = CompatibilityMatrixFile.Read(CompatibilityPaths.MatrixFile);

        foreach (var client in CompatibilityVocabulary.MandatoryClients)
        {
            matrix.Entries.Should().Contain(
                e => e.Client == client,
                $"FR-P061 nennt '{client}' ausdruecklich");
        }
    }
}
