using AwesomeAssertions;
using Bifrost.Compatibility.Tests.Matrix;
using Bifrost.Compatibility.Tests.Probe;
using Bifrost.Integration.Tests.Gateway;
using Xunit;

namespace Bifrost.Compatibility.Tests;

/// <summary>
/// Der automatisierte Pfad der Matrix: Was die Zeile <c>csharp-sdk</c> behauptet, wird hier gegen
/// einen laufenden Gateway nachgefahren.
/// <para>
/// <b>Die Richtung ist Absicht.</b> Der Lauf schreibt die Matrix nicht fort, sondern prüft sie:
/// Damit ist jedes <c>pass</c> in der veröffentlichten Tabelle eine Zusicherung, die rot wird, wenn
/// sie nicht mehr stimmt. Wer eine Zeile neu aufnimmt, lässt sie einmal mit
/// <c>BIFROST_UPDATE_COMPAT_MATRIX=1</c> aufzeichnen und liest nach, was dabei herauskam.
/// </para>
/// </summary>
public sealed class SdkClientCompatibilityTests : IClassFixture<GatewayFixture>
{
    private const string RunVariable = "BIFROST_RUN_COMPAT";
    private const string RecordVariable = "BIFROST_UPDATE_COMPAT_MATRIX";
    private const string Client = "csharp-sdk";

    private readonly GatewayFixture _gw;

    public SdkClientCompatibilityTests(GatewayFixture gw) => _gw = gw;

    [Fact]
    public async Task The_official_sdk_client_behaves_as_the_matrix_claims()
    {
        if (Environment.GetEnvironmentVariable(RunVariable) != "1")
        {
            Assert.Skip($"{RunVariable}=1 setzen — der Probelauf startet einen echten Gateway-Host.");
        }

        var ct = TestContext.Current.CancellationToken;
        var matrix = CompatibilityMatrixFile.Read(CompatibilityPaths.MatrixFile);
        var os = CurrentOs();
        var row = matrix.Entries.SingleOrDefault(e =>
            e.Client == Client && e.Os == os && e.Transport == "http" && e.Mode == "stateless");

        var outcome = await SdkClientProbe.RunAsync(_gw, ct);
        var notes = string.Join("\n  ", outcome.Notes);

        if (Environment.GetEnvironmentVariable(RecordVariable) == "1")
        {
            Record(matrix, row, os, outcome);
            return;
        }

        if (row is null)
        {
            // Kein stiller Durchmarsch: Ohne Zeile hat dieser Lauf nichts geprueft, und das steht
            // als Skip da statt als Gruen. Genau so wurde BIFROST_RUN_BENCHMARK wochenlang
            // uebersprungen, ohne dass es jemandem auffiel (M5-Vertrag §1.2).
            Assert.Skip(
                $"Fuer {Client}/{os}/http/stateless gibt es keine Matrixzeile. "
                + $"Einmal mit {RecordVariable}=1 aufzeichnen. Beobachtet:\n  {notes}");
            return;
        }

        foreach (var feature in CompatibilityFeatures.All)
        {
            outcome.Features[feature].Should().Be(
                row.Features[feature],
                $"die Matrix sagt fuer '{feature}': {row.Features[feature]}.\n  {notes}");
        }
    }

    private static void Record(
        CompatibilityMatrix matrix, CompatibilityEntry? row, string os, ProbeOutcome outcome)
    {
        var updated = (row ?? NewRow(os)) with
        {
            Features = outcome.Features.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            Evidence = new CompatibilityEvidence
            {
                Kind = "automated",
                Ref = RunReference(os),
                Date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            },
        };

        var entries = matrix.Entries.Where(e => e.Key != updated.Key).Append(updated).ToList();
        CompatibilityMatrixFile.Write(
            CompatibilityPaths.MatrixFile,
            matrix with { GeneratedAt = DateTimeOffset.UtcNow, Entries = entries });
    }

    private static CompatibilityEntry NewRow(string os) => new()
    {
        Client = Client,
        ClientVersion = "2.0.0",
        Os = os,
        Transport = "http",
        ProtocolVersion = "2026-07-28",
        Mode = "stateless",
        Limitations =
        [
            "In-Process-Host: kein TCP, kein TLS, kein Reverse Proxy — die Netzstrecke belegt WP5.3.",
        ],
    };

    /// <summary>
    /// In der CI die Lauf-URL, lokal eine als lokal erkennbare Referenz. Ein lokaler Lauf ist ein
    /// zulaessiger Beleg — aber er soll im Bericht auch als solcher dastehen.
    /// </summary>
    private static string RunReference(string os)
    {
        var server = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");

        return server is not null && repo is not null && run is not null
            ? $"{server}/{repo}/actions/runs/{run}"
            : $"local://{os}";
    }

    private static string CurrentOs() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "linux";
}
