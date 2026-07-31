using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Checks;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

public class ContainerRuntimeCheckTests
{
    private static DiagnosticContext World(ProcessProbeResult probe, bool? required = null)
        => DiagnosticWorld.Context(processes: new FakeProcessProbe { Result = probe }) with
        { ContainerIsolationConfigured = required };

    [Fact]
    public async Task Docker_in_linux_mode_passes()
    {
        var world = World(new ProcessProbeResult(true, 0, "linux\n", null));

        var result = await new ContainerRuntimeCheck().RunAsync(world, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.SafeDetails!["modus"].Should().Be("linux");
    }

    [Fact]
    public async Task Windows_container_mode_warns_because_the_policy_does_not_hold()
    {
        var world = World(new ProcessProbeResult(true, 0, "windows\n", null));

        var result = await new ContainerRuntimeCheck().RunAsync(world, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Remediation.Should().Contain("WSL2");
    }

    [Fact]
    public async Task A_missing_runtime_that_nobody_needs_is_skipped_and_not_a_warning()
    {
        // Eine Warnung, die beim korrekten Aufbau mitläuft, wird ignoriert.
        var world = World(new ProcessProbeResult(false, -1, string.Empty, "nicht gefunden"));

        var result = await new ContainerRuntimeCheck().RunAsync(world, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public async Task A_missing_runtime_that_an_upstream_needs_fails()
    {
        var world = World(new ProcessProbeResult(false, -1, string.Empty, "nicht gefunden"), required: true);

        var result = await new ContainerRuntimeCheck().RunAsync(world, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
        result.Remediation.Should().Contain("Rückfall auf den Host");
    }

    [Fact]
    public async Task Without_any_container_upstream_the_probe_is_not_even_run()
    {
        var world = World(new ProcessProbeResult(true, 0, "linux", null), required: false);

        var result = await new ContainerRuntimeCheck().RunAsync(world, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }
}

public class WasiHostCheckTests
{
    [Fact]
    public async Task Not_configured_is_skipped_with_a_reason()
    {
        var result = await new WasiHostCheck()
            .RunAsync(DiagnosticWorld.Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
        result.Summary.Should().Contain("Connector-Pakete");
    }

    [Fact]
    public async Task A_present_binary_passes()
    {
        var files = new FakeFileProbe();
        files.Files.Add("/usr/local/bin/bifrost-wasi-host");
        var context = DiagnosticWorld.Context(
            new Dictionary<string, string> { ["BIFROST_WASI_HOST"] = "/usr/local/bin/bifrost-wasi-host" },
            files);

        var result = await new WasiHostCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task A_configured_but_missing_binary_fails()
    {
        var context = DiagnosticWorld.Context(
            new Dictionary<string, string> { ["BIFROST_WASI_HOST"] = "/opt/falsch/host" });

        var result = await new WasiHostCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
        result.Remediation.Should().Contain("ungeprobt wird nichts aktiv");
    }
}

public class UpstreamStatesCheckTests
{
    [Fact]
    public async Task Without_a_probe_it_is_skipped()
    {
        var result = await new UpstreamStatesCheck()
            .RunAsync(DiagnosticWorld.Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public async Task All_healthy_passes()
    {
        var context = DiagnosticWorld.Context() with
        {
            Upstreams = new FakeUpstreamProbe(
                new UpstreamDiagnosticFact("github", "Healthy", true),
                new UpstreamDiagnosticFact("jira", "Healthy", true)),
        };

        var result = await new UpstreamStatesCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task A_broken_upstream_warns_and_names_it()
    {
        var context = DiagnosticWorld.Context() with
        {
            Upstreams = new FakeUpstreamProbe(
                new UpstreamDiagnosticFact("github", "Healthy", true),
                new UpstreamDiagnosticFact("jira", "Failed", false, "Zeitüberschreitung beim Handshake")),
        };

        var result = await new UpstreamStatesCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Summary.Should().Contain("jira").And.Contain("Zeitüberschreitung");
        result.SafeDetails!["nicht_bereit"].Should().Be("1");
    }

    [Fact]
    public async Task No_upstreams_at_all_is_skipped()
    {
        var context = DiagnosticWorld.Context() with { Upstreams = new FakeUpstreamProbe() };

        var result = await new UpstreamStatesCheck().RunAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }
}
