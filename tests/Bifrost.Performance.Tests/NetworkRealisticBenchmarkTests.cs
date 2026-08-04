using AwesomeAssertions;
using Bifrost.Performance.Tests.Harness;
using Xunit;

namespace Bifrost.Performance.Tests;

/// <summary>
/// WP5.3: die Messung über die Strecke, die im Betrieb tatsächlich davorsteht — echter Prozess,
/// echtes TCP, echte TLS-Terminierung, ein Upstream mit definierter Latenz und Dauerlast statt
/// eines Stoßes.
/// <para>
/// Sie ersetzt keine der bisherigen Zahlen, sie schließt die vier Lücken, die
/// <c>docs/acceptance/performance.md</c> selbst benannt hat: kein Durchsatz aus einem
/// 0,1-Sekunden-Fenster, kein In-Memory-Host, keine Trennung von Gateway- und Fremdanteil, kein
/// Vergleich der Betriebsarten.
/// </para>
/// </summary>
public sealed class NetworkRealisticBenchmarkTests
{
    private const string Slug = "perfup";
    private const string ToolName = $"{Slug}__sleep_measured";

    private readonly ITestOutputHelper _output;

    public NetworkRealisticBenchmarkTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("BIFROST_RUN_BENCHMARK") == "1";

    [Fact]
    public async Task Gateway_overhead_over_a_real_network_path_stateless_session_and_tls()
    {
        Assert.SkipUnless(Enabled, "Lastmessung nur mit BIFROST_RUN_BENCHMARK=1 (Referenz-Hardware).");

        var ct = TestContext.Current.CancellationToken;
        var settings = PerfSettings.FromEnvironment();
        var startedAt = DateTimeOffset.UtcNow;
        var scenarios = new List<PerfScenarioResult>();

        foreach (var (name, stateless, transport) in new[]
        {
            ("stateless/tcp", true, GatewayTransport.Http),
            ("session/tcp", false, GatewayTransport.Http),
            ("stateless/tls", true, GatewayTransport.Https),
        })
        {
            scenarios.Add(await MeasureAsync(name, stateless, transport, settings, ct));
        }

        var notes = new List<string>
        {
            $"Upstream: SlowServer, angefordert {settings.UpstreamLatencyMs} ms. Abgezogen wird der "
            + "vom Upstream SELBST gemessene Span ('sleep_measured'), nicht die angeforderte Zeit — "
            + "Task.Delay(20) dauert unter Windows regelmäßig gut 31 ms, und diese Differenz wäre "
            + "sonst als Gateway-Overhead erschienen.",
            "Der Gateway lief als eigener Prozess hinter Kestrel; die TLS-Variante terminiert im "
            + "Gateway selbst — ein vorgeschalteter Reverse Proxy ist damit NICHT gemessen.",
            "Der Durchsatz ist KEINE Kapazitätsgrenze: Der Lastlauf ist ein geschlossener "
            + "Regelkreis mit fester Nebenläufigkeit, der Wert ergibt sich also aus "
            + "Nebenläufigkeit geteilt durch Antwortzeit. Was der Gateway trägt, sagt der "
            + "Overhead je Aufruf zusammen mit der CPU-Zeit — nicht diese Zahl.",
        };

        if (!settings.IsComplete)
        {
            notes.Add(
                $"UNVOLLSTÄNDIG: {settings.DurationSeconds} s × {settings.Runs} Läufe. Für eine "
                + $"Abnahmeaussage sind {PerfSettings.MinimumDurationSeconds} s × "
                + $"{PerfSettings.MinimumRuns} Läufe verlangt.");
        }

        var result = new PerfRunResult(
            SchemaVersion: 1,
            RunId: PerfArtifacts.NewRunId(startedAt),
            StartedAt: startedAt,
            Environment: PerfArtifacts.Environment(),
            Settings: settings,
            Complete: settings.IsComplete,
            Scenarios: scenarios,
            Notes: notes);

        var path = PerfArtifacts.Write(result);

        _output.WriteLine($"=== WP5.3 — netzrealistische Messung ({(settings.IsComplete ? "vollständig" : "UNVOLLSTÄNDIG")}) ===");
        _output.WriteLine($"Artefakt: {path}");
        foreach (var scenario in scenarios)
        {
            var m = scenario.Median;
            _output.WriteLine(
                $"{scenario.Name,-16} p50={m.P50:0.0} ms  p95={m.P95:0.0} ms  p99={m.P99:0.0} ms  "
                + $"| Upstream p50={m.UpstreamP50:0.0} ms  Gateway p50={m.GatewayOverheadP50:0.0} ms  "
                + $"p95={m.GatewayOverheadP95:0.0} ms  | {m.ThroughputPerSecond:0} Calls/s  "
                + $"Fehler={m.Errors}  CPU={scenario.Process.CpuPercentOfOneCore:0.0} % eines Kerns  "
                + $"RSS={scenario.Process.EndWorkingSetMb:0} MB");
        }

        foreach (var scenario in scenarios)
        {
            scenario.Median.Errors.Should().Be(0, $"{scenario.Name}: unter Dauerlast darf kein Call scheitern (NFR-02)");
            scenario.Median.GatewayOverheadP95.Should().BeLessThan(
                50,
                $"{scenario.Name}: NFR-01 verlangt p95 ≤ 50 ms Gateway-Overhead — gemessen als Median "
                + "über alle Läufe, damit ein einzelner Ausreißer auf geteilter Hardware nicht zählt");
        }
    }

    private static async Task<PerfScenarioResult> MeasureAsync(
        string name, bool stateless, GatewayTransport transport, PerfSettings settings, CancellationToken ct)
    {
        await using var gateway = await GatewayProcess.StartAsync(
            new GatewayProcessOptions(stateless, transport), ct);
        await gateway.AddStdioUpstreamAsync(Slug, "SlowServer", ct);

        var before = gateway.Snapshot();
        var runs = new List<LoadMeasurement>();
        for (var i = 0; i < settings.Runs; i++)
        {
            runs.Add(await LoadDriver.RunAsync(gateway, settings.ToLoadOptions(ToolName), ct));
        }

        var after = gateway.Snapshot();

        return new PerfScenarioResult(
            Name: name,
            Mode: stateless ? "stateless" : "session",
            Transport: transport == GatewayTransport.Https ? "https" : "http",
            UpstreamLatencyMs: settings.UpstreamLatencyMs,
            Runs: runs,
            Median: PerfArtifacts.Median(runs),
            Process: PerfArtifacts.Metrics(before, after, runs.Sum(r => r.WallSeconds)));
    }
}
