using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Integration.Tests;

/// <summary>WP1.4: Per-Call-Timeout (FR-09) und Drain-Semantik gegen den echten SlowServer.</summary>
public class DrainAndTimeoutIntegrationTests
{
    [Fact]
    public async Task Call_timeout_cancels_slow_call_but_connection_survives()
    {
        await using var supervisor = IntegrationSupport.CreateSupervisor();
        // 2-s-Timeout statt 500 ms: Die Aussage (langer Call läuft in den Timeout, kurzer danach nicht)
        // bleibt durch den Abstand 30 s ≫ 2 s ≫ 10 ms erhalten, aber der kurze Folge-Call hat auch auf
        // einem ausgelasteten CI-Runner genug Luft für seinen MCP-Roundtrip.
        var id = await supervisor.AddAsync(
            IntegrationSupport.StdioServer("slow", "SlowServer", callTimeout: TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);
        await IntegrationSupport.WaitUntilAsync(
            () => supervisor.GetStatus(id)?.State == UpstreamState.Healthy);
        var connection = supervisor.GetConnection(id)!;

        var slowCall = () => connection.CallToolAsync(
            "sleep", JsonSerializer.SerializeToElement(new { milliseconds = 30000 }), TestContext.Current.CancellationToken);
        await slowCall.Should().ThrowAsync<TimeoutException>("2s CallTimeout gegen 30s Tool-Laufzeit");

        var quick = await connection.CallToolAsync(
            "sleep", JsonSerializer.SerializeToElement(new { milliseconds = 10 }), TestContext.Current.CancellationToken);
        quick.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Be("Slept 10 ms", "die Verbindung übersteht einen Timeout unbeschadet");
    }

    [Fact]
    public async Task Remove_with_short_drain_does_not_wait_for_long_call()
    {
        await using var supervisor = IntegrationSupport.CreateSupervisor();
        var id = await supervisor.AddAsync(
            IntegrationSupport.StdioServer("slow", "SlowServer"), TestContext.Current.CancellationToken);
        await IntegrationSupport.WaitUntilAsync(
            () => supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var hangingCall = supervisor.GetConnection(id)!.CallToolAsync(
            "sleep", JsonSerializer.SerializeToElement(new { milliseconds = 30000 }), TestContext.Current.CancellationToken);
        await Task.Delay(100);

        var sw = Stopwatch.StartNew();
        await supervisor.RemoveAsync(id, DrainPolicy.Graceful(TimeSpan.FromMilliseconds(300)), TestContext.Current.CancellationToken);
        sw.Stop();

        // Obergrenze = Drain-Frist (0,3s) + SDK-interne Prozess-Shutdown-Frist (~5s) + Puffer.
        // Entscheidend: deutlich unter den 30s des laufenden Calls.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "die Drain-Frist deckelt die Remove-Dauer");
        supervisor.GetStatus(id).Should().BeNull();

        var observeHangingCall = () => hangingCall;
        await observeHangingCall.Should().ThrowAsync<Exception>("der abgebrochene Call endet mit Fehler, nicht mit Erfolg");
    }
}
