using AwesomeAssertions;
using Bifrost.Compatibility.Tests.Matrix;
using Xunit;

namespace Bifrost.Compatibility.Tests;

/// <summary>
/// Die Negativkontrolle aus M5-Vertrag §1.1: Ein Prüfstand, der nie rot wird, misst nichts. Jeder
/// dieser Fälle ist eine Fassung der Matrix, die durchginge, wenn der Validator nachlässig wäre —
/// und genau die Fassungen, die jemand unter Zeitdruck schreiben würde.
/// </summary>
public sealed class MatrixValidatorNegativeTests
{
    private const string ValidEntry = """
        {
          "schemaVersion": 1,
          "generatedAt": "2026-08-02T00:00:00+00:00",
          "entries": [
            {
              "client": "csharp-sdk",
              "clientVersion": "2.0.0",
              "os": "windows",
              "transport": "http",
              "protocolVersion": "2026-07-28",
              "mode": "stateless",
              "features": {
                "tools": "pass", "resources": "pass", "prompts": "pass", "elicitation": "untested",
                "approval": "untested", "listChanged": "unsupported", "oauth": "untested", "skills": "pass"
              },
              "evidence": { "kind": "automated", "ref": "local://windows", "date": "2026-08-02" },
              "limitations": []
            }
          ]
        }
        """;

    /// <summary>Ohne diese Zusicherung prüfen die Fälle unten nicht das, was sie prüfen sollen.</summary>
    [Fact]
    public void The_baseline_fixture_is_accepted_apart_from_the_missing_mandatory_clients()
    {
        var errors = Validate(ValidEntry);

        errors.Should().OnlyContain(e => e.Contains("Pflichtclient", StringComparison.Ordinal));
    }

    [Fact]
    public void A_claim_without_evidence_is_rejected()
    {
        var errors = Validate(ValidEntry.Replace(
            "\"evidence\": { \"kind\": \"automated\", \"ref\": \"local://windows\", \"date\": \"2026-08-02\" },",
            string.Empty,
            StringComparison.Ordinal));

        errors.Should().Contain(e => e.Contains("nennt aber keinen Beleg", StringComparison.Ordinal));
    }

    [Fact]
    public void A_placeholder_row_may_not_carry_evidence()
    {
        var allUntested = ValidEntry
            .Replace("\"tools\": \"pass\"", "\"tools\": \"untested\"", StringComparison.Ordinal)
            .Replace("\"resources\": \"pass\"", "\"resources\": \"untested\"", StringComparison.Ordinal)
            .Replace("\"prompts\": \"pass\"", "\"prompts\": \"untested\"", StringComparison.Ordinal)
            .Replace("\"skills\": \"pass\"", "\"skills\": \"untested\"", StringComparison.Ordinal)
            .Replace("\"listChanged\": \"unsupported\"", "\"listChanged\": \"untested\"", StringComparison.Ordinal);

        var errors = Validate(allUntested);

        errors.Should().Contain(e => e.Contains("trotzdem hängt ein Beleg daran", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_feature_key_is_rejected()
    {
        var errors = Validate(ValidEntry.Replace("\"skills\":", "\"skils\":", StringComparison.Ordinal));

        errors.Should().Contain(e => e.Contains("geschlossenen Feature-Wortschatz", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("Feature 'skills' fehlt", StringComparison.Ordinal));
    }

    [Fact]
    public void An_invented_value_is_rejected()
    {
        var errors = Validate(ValidEntry.Replace("\"tools\": \"pass\"", "\"tools\": \"works\"", StringComparison.Ordinal));

        errors.Should().Contain(e => e.Contains("Wert 'works'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manual_protocol_without_an_operator_is_rejected()
    {
        var errors = Validate(ValidEntry.Replace(
            "\"kind\": \"automated\", \"ref\": \"local://windows\"",
            "\"kind\": \"manual\", \"ref\": \"tests/compatibility/protocols/x.md\"",
            StringComparison.Ordinal));

        errors.Should().Contain(e => e.Contains("ohne 'evidence.operator'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_dated_in_a_non_iso_format_is_rejected()
    {
        var errors = Validate(ValidEntry.Replace("\"date\": \"2026-08-02\"", "\"date\": \"02.08.2026\"", StringComparison.Ordinal));

        errors.Should().Contain(e => e.Contains("kein ISO-Datum", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_rows_for_the_same_run_are_rejected()
    {
        var doubled = CompatibilityMatrixFile.Parse(ValidEntry);
        var twice = doubled with { Entries = [.. doubled.Entries, doubled.Entries[0]] };

        var errors = CompatibilityMatrixValidator.Validate(twice);

        errors.Should().Contain(e => e.Contains("doppelte Zeile", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> Validate(string json) =>
        CompatibilityMatrixValidator.Validate(CompatibilityMatrixFile.Parse(json));
}
