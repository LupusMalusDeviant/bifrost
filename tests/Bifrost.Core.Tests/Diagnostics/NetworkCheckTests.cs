using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Checks;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

public class ListenPortCheckTests
{
    private static DiagnosticContext World(string urls, FakePortProbe ports, bool insideGateway = false)
        => DiagnosticWorld.Context(
            new Dictionary<string, string> { ["ASPNETCORE_URLS"] = urls },
            ports: ports) with
        { GatewayRunsInThisProcess = insideGateway };

    [Fact]
    public async Task A_free_port_passes()
    {
        var result = await new ListenPortCheck()
            .RunAsync(World("http://+:8080", new FakePortProbe()), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.SafeDetails!["ports"].Should().Be("8080");
    }

    [Fact]
    public async Task An_occupied_port_fails_when_the_gateway_is_not_running_here()
    {
        var ports = new FakePortProbe();
        ports.States[8080] = PortState.Occupied;

        var result = await new ListenPortCheck()
            .RunAsync(World("http://+:8080", ports), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
        result.Remediation.Should().Contain("ASPNETCORE_URLS");
    }

    [Fact]
    public async Task Inside_the_running_gateway_an_occupied_port_is_expected()
    {
        var ports = new FakePortProbe();
        ports.States[8080] = PortState.Occupied;

        var result = await new ListenPortCheck()
            .RunAsync(World("http://+:8080", ports, insideGateway: true), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task An_undeterminable_port_is_skipped_and_never_reported_as_free()
    {
        var ports = new FakePortProbe();
        ports.States[8080] = PortState.Unknown;

        var result = await new ListenPortCheck()
            .RunAsync(World("http://+:8080", ports), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public async Task Without_a_listen_address_the_check_is_skipped()
    {
        var result = await new ListenPortCheck()
            .RunAsync(DiagnosticWorld.Context(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Theory]
    [InlineData("http://+:8080", 8080)]
    [InlineData("http://0.0.0.0:5000", 5000)]
    [InlineData("https://gateway.example.com", 443)]
    [InlineData("http://[::1]:8080", 8080)]
    public void Ports_are_read_out_of_every_shape_of_listen_address(string url, int expected)
    {
        var context = DiagnosticWorld.Context(new Dictionary<string, string> { ["ASPNETCORE_URLS"] = url });

        context.ListenPorts().Should().Equal(expected);
    }
}

public class InsecureCookieTransportCheckTests
{
    private static DiagnosticContext World(
        string urls, string? trustedProxies = null, string environmentName = "Production")
    {
        var environment = new Dictionary<string, string> { ["ASPNETCORE_URLS"] = urls };
        if (trustedProxies is not null)
        {
            environment["BIFROST_TRUSTED_PROXIES"] = trustedProxies;
        }

        return DiagnosticWorld.Context(environment) with { HostEnvironmentName = environmentName };
    }

    [Fact]
    public async Task Plain_http_without_a_declared_proxy_warns_about_the_silent_login_loop()
    {
        // Der Fall aus dem echten Betrieb: Anmeldung geht durch, nächster Aufruf ist wieder die
        // Login-Maske, und nirgends steht ein Grund.
        var result = await new InsecureCookieTransportCheck()
            .RunAsync(World("http://+:8080"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
        result.Summary.Should().Contain("Login-Maske");
        result.Remediation.Should().Contain("X-Forwarded-Proto");
    }

    [Fact]
    public async Task With_a_declared_proxy_it_is_the_intended_setup()
    {
        var result = await new InsecureCookieTransportCheck()
            .RunAsync(World("http://+:8080", "172.17.0.1"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task Https_passes()
    {
        var result = await new InsecureCookieTransportCheck()
            .RunAsync(World("https://+:8443"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task Loopback_only_passes_because_browsers_treat_it_as_a_secure_origin()
    {
        var result = await new InsecureCookieTransportCheck()
            .RunAsync(World("http://localhost:8080"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.Summary.Should().Contain("localhost");
    }

    [Fact]
    public async Task In_development_the_check_is_skipped()
    {
        var result = await new InsecureCookieTransportCheck()
            .RunAsync(World("http://+:8080", environmentName: "Development"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }
}

public class TrustedProxiesCheckTests
{
    private static DiagnosticContext World(string? value)
        => DiagnosticWorld.Context(value is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["BIFROST_TRUSTED_PROXIES"] = value });

    [Fact]
    public async Task Not_set_is_skipped_and_explained()
    {
        var result = await new TrustedProxiesCheck().RunAsync(World(null), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public async Task Addresses_and_cidr_ranges_pass()
    {
        var result = await new TrustedProxiesCheck()
            .RunAsync(World("172.17.0.1, 10.0.0.0/8, ::1"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Pass);
        result.SafeDetails!["eintraege"].Should().Be("3");
    }

    [Fact]
    public async Task A_typo_fails_because_it_aborts_the_start()
    {
        var result = await new TrustedProxiesCheck()
            .RunAsync(World("172.17.0.1, 10.0.0/8"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
        result.Summary.Should().Contain("10.0.0/8");
    }

    [Fact]
    public async Task Any_warns_because_it_believes_every_sender()
    {
        var result = await new TrustedProxiesCheck().RunAsync(World("any"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Warning);
    }

    [Fact]
    public async Task An_out_of_range_prefix_fails()
    {
        var result = await new TrustedProxiesCheck()
            .RunAsync(World("10.0.0.0/64"), TestContext.Current.CancellationToken);

        result.Status.Should().Be(CheckStatus.Fail);
    }
}
