using AwesomeAssertions;
using Bifrost.Performance.Tests.Harness;
using Xunit;

namespace Bifrost.Performance.Tests;

/// <summary>
/// Die Negativkontrolle des Messstands (M5-Vertrag §1.1). Ein Harness, der nie etwas bemerkt, misst
/// nichts — und das fiele bei einer Leistungsmessung besonders spät auf, weil ein zu guter Wert wie
/// ein Erfolg aussieht.
/// </summary>
public sealed class HarnessSelfTests
{
    private const string Slug = "selfup";
    private const string ToolName = $"{Slug}__sleep_measured";

    private readonly ITestOutputHelper _output;

    public HarnessSelfTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("BIFROST_RUN_BENCHMARK") == "1";

    /// <summary>Reine Rechnung, läuft immer: Der Berichtswert ist der Median, nicht der beste Lauf.</summary>
    [Fact]
    public void The_reported_value_is_the_median_and_not_the_best_run()
    {
        var runs = new[]
        {
            Measurement(p95: 90),
            Measurement(p95: 10),
            Measurement(p95: 50),
        };

        var median = PerfArtifacts.Median(runs);

        median.P95.Should().Be(50);
        median.Errors.Should().Be(0);
    }

    /// <summary>Fehler werden summiert, nicht gemittelt — ein Ausfall verschwindet nicht im Median.</summary>
    [Fact]
    public void Errors_are_summed_across_runs()
    {
        var runs = new[] { Measurement(p95: 10, errors: 0), Measurement(p95: 10, errors: 3) };

        PerfArtifacts.Median(runs).Errors.Should().Be(3);
    }

    [Fact]
    public void An_incomplete_run_is_marked_as_incomplete()
    {
        new PerfSettings(DurationSeconds: 5, WarmupSeconds: 1, Runs: 1, Concurrency: 4, UpstreamLatencyMs: 10)
            .IsComplete.Should().BeFalse();

        new PerfSettings(
                PerfSettings.MinimumDurationSeconds, 5, PerfSettings.MinimumRuns, 32, 20)
            .IsComplete.Should().BeTrue();
    }

    /// <summary>
    /// <b>Die eigentliche Gegenprobe.</b> Derselbe Aufbau, nur ein langsamerer Upstream: Wenn der
    /// gemessene Median dieser Verlangsamung nicht folgt, misst der Harness nicht die Strecke,
    /// sondern sich selbst.
    /// </summary>
    [Fact]
    public async Task The_harness_notices_when_the_upstream_gets_slower()
    {
        Assert.SkipUnless(Enabled, "Braucht einen echten Gateway-Prozess; nur mit BIFROST_RUN_BENCHMARK=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var gateway = await GatewayProcess.StartAsync(new GatewayProcessOptions(), ct);
        await gateway.AddStdioUpstreamAsync(Slug, "SlowServer", ct);

        var fast = await LoadDriver.RunAsync(
            gateway, new LoadOptions(ToolName, UpstreamLatencyMs: 10, Concurrency: 8, WarmupSeconds: 2, DurationSeconds: 5), ct);
        var slow = await LoadDriver.RunAsync(
            gateway, new LoadOptions(ToolName, UpstreamLatencyMs: 60, Concurrency: 8, WarmupSeconds: 2, DurationSeconds: 5), ct);

        var shift = slow.P50 - fast.P50;
        _output.WriteLine(
            $"p50 schnell={fast.P50:0.0} ms (Upstream {fast.UpstreamP50:0.0} ms, Overhead {fast.GatewayOverheadP50:0.0} ms), "
            + $"langsam={slow.P50:0.0} ms (Upstream {slow.UpstreamP50:0.0} ms, Overhead {slow.GatewayOverheadP50:0.0} ms), "
            + $"Verschiebung={shift:0.0} ms");

        shift.Should().BeInRange(
            35, 75,
            "eine um 50 ms langsamere Gegenstelle muss sich im Messwert zeigen — sonst misst der Harness nicht die Strecke");

        // Und das ist die zweite Hälfte der Gegenprobe: Der Fremdanteil verschiebt sich mit, der
        // Gateway-Anteil nicht. Genau dafür werden die Spans getrennt.
        (slow.UpstreamP50 - fast.UpstreamP50).Should().BeInRange(
            35, 75, "der gemessene Upstream-Span folgt der Verlangsamung");
        Math.Abs(slow.GatewayOverheadP50 - fast.GatewayOverheadP50).Should().BeLessThan(
            10, "der Gateway-Anteil haengt nicht an der Antwortzeit des Upstreams");
    }

    private static LoadMeasurement Measurement(double p95, int errors = 0) => new(
        Calls: 100,
        Errors: errors,
        P50: p95 / 2,
        P95: p95,
        P99: p95 * 1.2,
        Max: p95 * 1.5,
        ThroughputPerSecond: 100,
        WallSeconds: 1,
        UpstreamP50: p95 / 4,
        UpstreamP95: p95 / 3,
        GatewayOverheadP50: p95 / 4,
        GatewayOverheadP95: p95 / 2);
}
