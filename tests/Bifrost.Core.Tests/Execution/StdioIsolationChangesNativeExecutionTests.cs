using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Core.Execution;

using Xunit;

namespace Bifrost.Core.Tests.Execution;

/// <summary>
/// Was „nativ" heisst, hat sich mit WP3.2 an genau einer Stelle geändert (ADR-0025 E5): stdio hat
/// jetzt ein Isolationsmodell, und ein stdio-Upstream im Container läuft nicht mehr auf dem Host.
/// <para>
/// Diese Datei prüft <b>beide Richtungen</b>. Nur die eine zu prüfen wäre gefährlich: Ein
/// <c>RunsOnHost</c>, das für stdio pauschal <c>false</c> lieferte, hätte jeden bestehenden
/// stdio-Upstream an der Ausführungs-Policy vorbeigeführt — und damit still die Prüfung
/// abgeschaltet, die dieses Milestone eingeführt hat.
/// </para>
/// </summary>
public class StdioIsolationChangesNativeExecutionTests
{
    private static UpstreamServerConfig Stdio(IsolationOptions? isolation)
        => new("probe", "Probe", UpstreamTransportKind.Stdio, true,
            Stdio: new StdioTransportOptions("/usr/bin/server", [], Isolation: isolation));

    /// <summary>
    /// Der Bestandsfall: keine Isolationsangabe heisst weiterhin Hostausführung — und damit
    /// weiterhin die Frage an die Policy.
    /// </summary>
    [Fact]
    public void A_stdio_upstream_without_isolation_still_runs_on_the_host()
        => NativeExecution.RunsOnHost(Stdio(null)).Should().BeTrue();

    /// <summary>
    /// Ein ausdrücklicher Host-Modus ist derselbe Fall — nur sichtbar. Er darf die Prüfung nicht
    /// umgehen, bloss weil jemand einen Abschnitt hingeschrieben hat.
    /// </summary>
    [Fact]
    public void An_explicit_host_mode_is_still_host_execution()
        => NativeExecution.RunsOnHost(Stdio(new IsolationOptions(IsolationMode.Host)))
            .Should().BeTrue();

    /// <summary>
    /// Und der neue Fall: Im Container läuft das Programm nicht mehr im Prozessraum des Gateways —
    /// die Host-Policy ist nicht mehr betroffen.
    /// </summary>
    [Fact]
    public void A_stdio_upstream_in_a_container_does_not_run_on_the_host()
        => NativeExecution.RunsOnHost(
                Stdio(new IsolationOptions(IsolationMode.Container, Image: "alpine:3.20")))
            .Should().BeFalse();

    /// <summary>
    /// Dieselbe Aussage über den Weg ohne fertige Konfiguration (Paketmanifest, ADR-0025 E4). Ohne
    /// Angabe bleibt es bei „nativ" — ein Paket, das keine Isolation mitbringt, startet nativ.
    /// </summary>
    [Fact]
    public void A_package_manifest_without_isolation_still_counts_as_native()
    {
        NativeExecution.RunsOnHost(UpstreamTransportKind.Stdio, cli: null).Should().BeTrue();
        NativeExecution.RunsOnHost(
                UpstreamTransportKind.Stdio,
                cli: null,
                stdio: new StdioTransportOptions(
                    "x", [], Isolation: new IsolationOptions(IsolationMode.Container, Image: "i")))
            .Should().BeFalse();
    }

    /// <summary>
    /// Die Folge im Zusammenspiel: Auf einer Instanz, die Hostausführung verbietet (Vorgabe einer
    /// frischen Installation, ADR-0025 E2), kommt der stdio-Upstream im Container durch — und der
    /// ohne Isolation nicht. Genau das macht den Container zum gangbaren Weg statt zur Theorie.
    /// </summary>
    [Fact]
    public void On_a_fresh_instance_only_the_isolated_stdio_upstream_is_allowed()
    {
        var policy = HostExecutionPolicy.FreshInstance();

        policy.Evaluate(Stdio(null)).Allowed.Should().BeFalse();
        policy.Evaluate(Stdio(new IsolationOptions(IsolationMode.Container, Image: "alpine:3.20")))
            .Allowed.Should().BeTrue();
    }
}
