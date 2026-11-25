using AwesomeAssertions;
using Faolan.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Faolan.Integration.Tests.Gateway;

/// <summary>
/// Codex M7 (Plan 0005, C14): Der Host stoppt seine Dienste in umgekehrter Reihenfolge. Steht der
/// Audit-Schreiber hinter dem Gateway, schließt er seinen Kanal, bevor der Supervisor beim
/// Herunterfahren seine zurückgestellten Zustandszeilen nachreicht — sie wären verloren, obwohl der
/// Kanal sie hätte aufnehmen können.
/// </summary>
public sealed class HostedServiceOrderTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public HostedServiceOrderTests(GatewayFixture gw) => _gw = gw;

    [Fact]
    public void The_audit_writer_stops_after_the_gateway()
    {
        var services = _gw.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();

        var audit = services.IndexOf(typeof(AuditWriterService));
        var gateway = services.IndexOf(typeof(GatewayStartupService));
        audit.Should().BeGreaterThanOrEqualTo(0);
        gateway.Should().BeGreaterThan(audit, "gestoppt wird rückwärts: erst das Gateway, dann das Audit");
    }
}
