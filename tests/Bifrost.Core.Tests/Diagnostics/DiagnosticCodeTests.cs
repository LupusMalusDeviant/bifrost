using System.Reflection;

using AwesomeAssertions;

using Bifrost.Core.Diagnostics;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

/// <summary>
/// Die Codes sind das, worauf ein Betreiber ein Runbook stützt. Ein Code, den es zweimal gibt,
/// macht jede Suche und jede Alarmregel mehrdeutig — und das fällt im Betrieb erst auf, wenn
/// jemand dem falschen Befund hinterherläuft.
/// </summary>
public class DiagnosticCodeTests
{
    private static IReadOnlyList<(string Name, string Value)> Constants()
        => [.. typeof(DiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (field.Name, (string)field.GetRawConstantValue()!))];

    [Fact]
    public void Every_code_is_handed_out_exactly_once()
    {
        var duplicates = Constants()
            .GroupBy(entry => entry.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(entry => entry.Name))}")
            .ToList();

        duplicates.Should().BeEmpty("ein Diagnosecode gehört zu genau einem Befund");
    }

    [Fact]
    public void The_all_list_matches_the_constants()
    {
        // Sonst gibt es Codes, die im Produkt vorkommen, aber in keiner Übersicht stehen.
        DiagnosticCodes.All.Should().BeEquivalentTo(Constants().Select(entry => entry.Value));
    }

    [Fact]
    public void The_all_list_has_no_duplicates()
    {
        DiagnosticCodes.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_shipped_check_uses_a_registered_code()
    {
        var codes = DiagnosticService.DefaultChecks.Select(check => check.Code).ToList();

        codes.Should().OnlyHaveUniqueItems();
        codes.Should().BeSubsetOf(DiagnosticCodes.All);
    }

    [Theory]
    [InlineData("BFR-CFG-")]
    [InlineData("BFR-DB-")]
    [InlineData("BFR-KEY-")]
    [InlineData("BFR-NET-")]
    [InlineData("BFR-RT-")]
    [InlineData("BFR-UP-")]
    public void Every_area_from_the_contract_is_covered(string prefix)
    {
        DiagnosticCodes.All.Should().Contain(code => code.StartsWith(prefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Codes_follow_the_shape_from_the_contract()
    {
        foreach (var code in DiagnosticCodes.All)
        {
            // BFR-<BEREICH>-<vier Ziffern>, so wie im M2-Vertrag §3 festgelegt.
            var parts = code.Split('-');
            parts.Should().HaveCount(3, "Format ist BFR-<Bereich>-<Nummer>");
            parts[0].Should().Be("BFR");
            parts[2].Should().HaveLength(4);
            parts[2].Should().MatchRegex("^[0-9]{4}$");
        }
    }
}
