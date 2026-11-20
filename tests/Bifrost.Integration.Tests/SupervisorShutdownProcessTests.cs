using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.TestServers.Common;
using Xunit;

namespace Bifrost.Integration.Tests;

/// <summary>
/// Läuft nicht parallel zu anderen Tests: Die Prozessprüfung sieht alle EchoServer der Maschine, und
/// parallel laufende Tests starten und beenden ständig eigene.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessCountingDefinition
{
    public const string Name = "Prozesse zählen (nicht parallel)";
}

/// <summary>WP1-DoD: Nach dem Herunterfahren des Hosts überlebt kein Kindprozess (ADR-0005).</summary>
[Collection(ProcessCountingDefinition.Name)]
public sealed class SupervisorShutdownProcessTests
{
    private const string EchoServer = "Bifrost.TestServers.EchoServer";

    /// <summary>
    /// Geprüft werden genau die Prozesse, die dieser Supervisor gestartet hat — nicht die Gesamtzahl.
    /// Vorher zählte der Test alle EchoServer der Maschine gegen einen Ausgangswert; startete ein
    /// paralleler Test währenddessen einen eigenen, lief die Prüfung in die Frist (CI auf PR #39).
    /// </summary>
    [Fact]
    public async Task Dispose_leaves_no_zombie_processes()
    {
        var before = UpstreamProcessLookup.FindByExecutableName(EchoServer).ToHashSet();

        var supervisor = IntegrationSupport.CreateSupervisor();
        var id = await supervisor.AddAsync(
            IntegrationSupport.StdioServer("echo", "EchoServer"), TestContext.Current.CancellationToken);
        await IntegrationSupport.WaitUntilAsync(
            () => supervisor.GetStatus(id)?.State == UpstreamState.Healthy);

        var started = UpstreamProcessLookup.FindByExecutableName(EchoServer).Where(pid => !before.Contains(pid)).ToList();
        started.Should().NotBeEmpty("der Upstream-Prozess muss laufen");

        await supervisor.DisposeAsync();

        await IntegrationSupport.WaitUntilAsync(
            () => !UpstreamProcessLookup.FindByExecutableName(EchoServer).Intersect(started).Any(),
            timeoutMs: 10000,
            because: "nach Host-Shutdown darf kein Kindprozess überleben (WP1-DoD, ADR-0005)");
    }
}
