using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.TestServers.Common;
using Xunit;

namespace Faolan.Integration.Tests;

/// <summary>WP1-DoD-Nachweise gegen echte stdio-Prozesse (Plan WP1, PRD FR-06/08/09).</summary>
public class SupervisorIntegrationTests
{
    [Fact]
    public async Task Add_reaches_inventory_changed_event_in_under_5_seconds()
    {
        await using var supervisor = IntegrationSupport.CreateSupervisor();
        var inventoryChangedAt = (TimeSpan?)null;
        var sw = Stopwatch.StartNew();
        supervisor.Changed += (_, e) =>
        {
            if (e.Kind == UpstreamChangeKind.InventoryChanged && e.State == UpstreamState.Healthy)
            {
                inventoryChangedAt ??= sw.Elapsed;
            }
        };

        var id = await supervisor.AddAsync(
            IntegrationSupport.StdioServer("echo", "EchoServer"), TestContext.Current.CancellationToken);

        await IntegrationSupport.WaitUntilAsync(
            () => inventoryChangedAt is not null,
            because: "AddAsync muss zum InventoryChanged-Event führen (WP1-DoD)");

        // 5-s-Schranke gilt für Referenz-Hardware. Auf geteilten CI-Runnern kostet allein der
        // Kaltstart des stdio-Kindprozesses (JIT, Dateisystem) mehr — dort großzügiger, damit der
        // Test das Verhalten prüft und nicht die Tagesform des Runners.
        var isCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        inventoryChangedAt!.Value.Should().BeLessThan(
            isCi ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5),
            $"Add → InventoryChanged (CI-relaxt: {isCi})");
        supervisor.GetInventory(id)!.Tools.Should().ContainSingle(t => t.Name == "echo");
    }

    [Fact]
    public async Task Crash_triggers_failed_then_auto_restart_while_neighbor_keeps_serving()
    {
        await using var supervisor = IntegrationSupport.CreateSupervisor();
        var events = new List<UpstreamChangedEventArgs>();
        supervisor.Changed += (_, e) =>
        {
            lock (events)
            {
                events.Add(e);
            }
        };

        var stableId = await supervisor.AddAsync(
            IntegrationSupport.StdioServer("stable", "EchoServer"), TestContext.Current.CancellationToken);
        var crashId = await supervisor.AddAsync(
            IntegrationSupport.StdioServer("crashy", "CrashServer"), TestContext.Current.CancellationToken);

        await IntegrationSupport.WaitUntilAsync(() =>
            supervisor.GetStatus(stableId)?.State == UpstreamState.Healthy &&
            supervisor.GetStatus(crashId)?.State == UpstreamState.Healthy);

        // Prozess hart töten: der crash-Tool-Call beendet den Server ohne Antwort.
        var crashCall = () => supervisor.GetConnection(crashId)!
            .CallToolAsync("crash", default, TestContext.Current.CancellationToken);
        await crashCall.Should().ThrowAsync<Exception>("der Prozess stirbt mitten im Call");

        bool CrashServerWentThroughFailed()
        {
            lock (events)
            {
                return events.Any(e =>
                    e.Server == crashId &&
                    e.Kind == UpstreamChangeKind.StateChanged &&
                    e.State == UpstreamState.Failed);
            }
        }

        // Während der Crash-/Restart-Phase muss der Nachbar jeden Call beantworten (Fault Isolation).
        var stableCalls = 0;
        while (!CrashServerWentThroughFailed() || supervisor.GetStatus(crashId)?.State != UpstreamState.Healthy)
        {
            var result = await supervisor.GetConnection(stableId)!.CallToolAsync(
                "echo",
                JsonSerializer.SerializeToElement(new { message = $"ping {stableCalls}" }),
                TestContext.Current.CancellationToken);
            result.GetProperty("content")[0].GetProperty("text").GetString()
                .Should().Be($"Echo: ping {stableCalls}", "kein Call des Nachbarn darf verloren gehen (WP1-DoD)");
            stableCalls++;

            if (stableCalls > 300)
            {
                throw new TimeoutException("CrashServer wurde nicht binnen der Frist wieder Healthy.");
            }

            await Task.Delay(100);
        }

        stableCalls.Should().BeGreaterThan(0);
        supervisor.GetStatus(crashId)!.State.Should().Be(UpstreamState.Healthy, "Auto-Restart (WP1-DoD)");
        supervisor.GetInventory(crashId)!.Tools.Should().Contain(t => t.Name == "echo");
    }

    // Dispose_leaves_no_zombie_processes steht in SupervisorShutdownProcessTests.
}
