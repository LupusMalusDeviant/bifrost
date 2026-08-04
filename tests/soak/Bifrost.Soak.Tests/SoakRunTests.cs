using AwesomeAssertions;
using Bifrost.Soak.Tests.Harness;
using Xunit;

namespace Bifrost.Soak.Tests;

/// <summary>
/// WP5.4: Dauerlast plus Störungen (FR-P051/P052). Der Lauf hängt an
/// <c>BIFROST_RUN_SOAK=smoke|long</c> und wird in <c>soak.yml</c> gesetzt — eine Gate-Variable ohne
/// Job wäre ein Test, der nie läuft und trotzdem grün meldet (M5-Vertrag §1.2).
/// </summary>
public sealed class SoakRunTests
{
    private readonly ITestOutputHelper _output;

    public SoakRunTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task The_gateway_survives_load_and_chaos_without_growing_or_leaking_processes()
    {
        var mode = SoakSettings.ModeFromEnvironment();
        Assert.SkipUnless(mode is not null, $"{SoakSettings.RunVariable}=smoke|long setzen.");

        var settings = SoakSettings.For(mode!.Value);
        var outcome = await SoakRunner.RunAsync(
            settings,
            new SoakScenario("load-and-chaos", Chaos: true),
            _output.WriteLine,
            TestContext.Current.CancellationToken);

        var summary = outcome.Summary;
        Report(summary, outcome.Directory);

        summary.CallsOk.Should().BeGreaterThan(0, "ohne einen einzigen erfolgreichen Aufruf misst der Lauf nichts");
        summary.ChaosEvents.Should().NotBeEmpty("ohne Störung ist es kein Chaos-Lauf");

        summary.RssGrowth.IsUnbounded.Should().BeFalse($"Arbeitsspeicher: {summary.RssGrowth.Reason}");
        summary.HandleGrowth.IsUnbounded.Should().BeFalse($"Handles: {summary.HandleGrowth.Reason}");
        summary.ThreadGrowth.IsUnbounded.Should().BeFalse($"Threads: {summary.ThreadGrowth.Reason}");

        // FR-P050: Der Gateway startet Upstreams neu; wenn dabei jedes Mal einer stehen bliebe,
        // waere genau das ein Leck, das die Speicherkurve nicht zeigt.
        summary.ChildProcessesAtEnd.Should().BeLessThanOrEqualTo(
            summary.MaxChildProcesses,
            "die Zahl der Kindprozesse darf ueber den Lauf nicht anwachsen");
        summary.OrphanProcesses.Should().BeEmpty(
            "nach dem Herunterfahren darf kein Upstream-Prozess uebrig sein: "
            + string.Join(", ", summary.OrphanProcesses));

        summary.RecoveredAfterChaos.Should().BeTrue(
            "nach der letzten Stoerung muss der Gateway wieder bedienen — Fehler zaehlen allein "
            + "reicht nicht, denn unter Chaos sind Fehler erwartet");

        // Ein Lauf, der als Betriebsnachweis gilt, muss auch geurteilt haben. Sonst waere die
        // Zusicherung „kein ungebremstes Wachstum" aus einem Lauf entstanden, dessen Fenster fuer
        // eine Wachstumsaussage gar nicht reichte — gruen, aber ohne Inhalt.
        if (summary.IsEvidence)
        {
            summary.RssGrowth.HasVerdict.Should().BeTrue(
                $"ein Lauf ueber {summary.DurationSeconds:0} s muss ein Urteil hergeben: {summary.RssGrowth.Reason}");
        }
    }

    /// <summary>
    /// Die Gegenprobe am laufenden System: Ein Upstream, der sich bei jedem Schreibaufruf selbst
    /// abschießt, <b>muss</b> als Fehler in der Auswertung erscheinen. Ein Soak-Harness, der auch
    /// das noch grün meldet, meldet alles grün.
    /// </summary>
    [Fact]
    public async Task A_broken_upstream_shows_up_as_failures_and_the_gateway_still_recovers()
    {
        var mode = SoakSettings.ModeFromEnvironment();
        Assert.SkipUnless(mode is not null, $"{SoakSettings.RunVariable}=smoke|long setzen.");

        var settings = SoakSettings.For(SoakMode.Smoke) with
        {
            DurationSeconds = 25,
            WarmupSeconds = 5,
            SampleIntervalSeconds = 5,
            Concurrency = 4,
            ThinkTimeMs = 5,
        };

        var outcome = await SoakRunner.RunAsync(
            settings,
            new SoakScenario("injected-fault", Chaos: false, WriteTool: "crash__crash"),
            _output.WriteLine,
            TestContext.Current.CancellationToken);

        var summary = outcome.Summary;
        Report(summary, outcome.Directory);

        summary.CallsFailed.Should().BeGreaterThan(
            0, "jeder Schreibaufruf zerlegt seinen Upstream — das muss sich in der Auswertung zeigen");
        summary.FailuresByReason.Should().NotBeEmpty("ein Fehler ohne Grund ist keine verwertbare Meldung");
        summary.CallsOk.Should().BeGreaterThan(
            0, "Lesen, Listen und Audit muessen weiterlaufen, waehrend ein Upstream staendig faellt");
        summary.RecoveredAfterChaos.Should().BeTrue("der zweite Upstream traegt weiter");
        summary.OrphanProcesses.Should().BeEmpty(
            "ein staendig neu gestarteter Upstream darf keine Prozesse hinterlassen: "
            + string.Join(", ", summary.OrphanProcesses));
    }

    private void Report(SoakSummary summary, string directory)
    {
        _output.WriteLine($"=== Soak '{summary.Scenario}' ({summary.Mode}, {summary.DurationSeconds:0} s) ===");
        _output.WriteLine($"Artefakte:   {directory}");
        _output.WriteLine($"Aufrufe:     {summary.CallsOk} ok, {summary.CallsFailed} fehlgeschlagen");
        _output.WriteLine($"RSS:         {summary.RssGrowth.First:0} → {summary.RssGrowth.Last:0} MB "
            + $"(Spitze {summary.RssGrowth.Peak:0}) — {summary.RssGrowth.Reason}");
        _output.WriteLine($"Handles:     {summary.HandleGrowth.First:0} → {summary.HandleGrowth.Last:0}");
        _output.WriteLine($"Threads:     {summary.ThreadGrowth.First:0} → {summary.ThreadGrowth.Last:0}");
        _output.WriteLine($"Kindprozesse: max {summary.MaxChildProcesses}, am Ende {summary.ChildProcessesAtEnd}");
        _output.WriteLine($"Erholung:    {(summary.RecoveredAfterChaos ? "ja" : "NEIN")}");

        foreach (var chaosEvent in summary.ChaosEvents)
        {
            _output.WriteLine($"  [{chaosEvent.AtSeconds,7:0.0} s] {chaosEvent.Action}: {chaosEvent.Detail}");
        }

        foreach (var (reason, count) in summary.FailuresByReason.OrderByDescending(f => f.Value))
        {
            _output.WriteLine($"  {count,6}× {reason}");
        }
    }
}
