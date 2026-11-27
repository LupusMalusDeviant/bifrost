using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.TestServers.Common;
using Xunit;

namespace Faolan.Integration.Tests;

/// <summary>WP1-DoD: Nach dem Herunterfahren des Hosts überlebt kein Kindprozess (ADR-0005).</summary>
public sealed class SupervisorShutdownProcessTests
{
    private const string EchoServer = "Faolan.TestServers.EchoServer";

    /// <summary>
    /// Geprüft wird, dass nach dem Herunterfahren <b>kein EchoServer mehr läuft, der während des Tests
    /// gestartet wurde</b> — auch keiner aus einem Neustart. Vorher verglich der Test die Gesamtzahl aller
    /// EchoServer der Maschine mit einem Ausgangswert. Endete in dieser Zeit ein Prozess eines früheren
    /// Tests, sank die Zahl unter den Ausgangswert, und die Gleichheit trat nie ein (CI auf PR #39).
    /// Prozesse, die schon vorher liefen, spielen jetzt keine Rolle mehr.
    /// </summary>
    [Fact]
    public async Task Dispose_leaves_no_zombie_processes()
    {
        var before = UpstreamProcessLookup.FindByExecutableName(EchoServer).ToHashSet();
        IReadOnlyList<int> StartedSinceBefore()
            => [.. UpstreamProcessLookup.FindByExecutableName(EchoServer).Where(pid => !before.Contains(pid))];

        var supervisor = IntegrationSupport.CreateSupervisor();
        try
        {
            var id = await supervisor.AddAsync(
                IntegrationSupport.StdioServer("echo", "EchoServer"), TestContext.Current.CancellationToken);
            await IntegrationSupport.WaitUntilAsync(
                () => supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

            StartedSinceBefore().Should().NotBeEmpty("der Upstream-Prozess muss laufen");
        }
        finally
        {
            await supervisor.DisposeAsync();
        }

        await IntegrationSupport.WaitUntilAsync(
            () => StartedSinceBefore().Count == 0,
            timeoutMs: 10000,
            because: "nach Host-Shutdown darf kein Kindprozess überleben (WP1-DoD, ADR-0005)");
    }
}
