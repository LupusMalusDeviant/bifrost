using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Upstreams;
using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

/// <summary>
/// Was die Diagnose über das Protokoll der Gegenstelle sagt.
/// <para>
/// Vorher stand dort die Fassungs<em>familie</em> („2026-07-28 oder neuer") und für den transienten
/// Verbindungstest gar nichts. Beides war ehrlich, aber unbrauchbar: Bei einem Upstream, der sich
/// anders verhält als erwartet, ist die ausgehandelte Fassung oft die erste Angabe, mit der jemand
/// weiterkommt (ADR-0023).
/// </para>
/// </summary>
public sealed class UpstreamProtocolReportingTests
{
    // ───────────────────────── Die Fassung selbst ─────────────────────────

    /// <summary>
    /// Der eigentliche Befund: Was der transiente Versuch abgelesen hat, steht im Bericht. Der Test
    /// hält seine Verbindung nicht — die Angabe wird gelesen, solange sie noch da ist.
    /// </summary>
    [Fact]
    public async Task The_negotiated_version_from_the_transient_attempt_reaches_the_report()
    {
        var report = await RunAsync(Http(), new UpstreamTestResult(
            true, 2, null, UpstreamProtocolInfo.Negotiated("2026-07-28", ["tools", "tools.listChanged"])));

        report.Negotiation.Should().NotBeNull();
        var negotiation = report.Negotiation!;
        negotiation.Availability.Should().Be(UpstreamProtocolAvailability.Negotiated);
        negotiation.ProtocolVersion.Should().Be("2026-07-28");
        negotiation.ProtocolLabel.Should().Be("2026-07-28", "die Familie beantwortet die Frage nicht");
        negotiation.Capabilities.Should().Contain(["tools", "tools.listChanged"]);
    }

    /// <summary>
    /// Steht die Angabe da, sagt der Bericht auch, <b>woher</b> sie stammt. Eine Zahl ohne Herkunft
    /// ist im Störungsfall schwer zu glauben — und die Verwechslung mit einem Konfigurationswert
    /// ist genau die, die WP4.6 vermeiden wollte.
    /// </summary>
    [Fact]
    public async Task A_present_version_says_where_it_came_from()
    {
        var report = await RunAsync(Http(), new UpstreamTestResult(
            true, 1, null, UpstreamProtocolInfo.Negotiated("2025-11-25")));

        report.Negotiation!.Note.Should().Contain("Abgelesen", "sonst sieht die Zahl aus wie eine Vorgabe");
    }

    // ───────────────────────── Der Unterschied ─────────────────────────

    /// <summary>
    /// „Nicht zutreffend" ist eine Antwort. Bei einem CLI-Upstream gibt es keine Fassung, und wer
    /// das liest, hört auf zu suchen.
    /// </summary>
    [Fact]
    public async Task A_transport_without_mcp_says_not_applicable()
    {
        var report = await RunAsync(Http(), new UpstreamTestResult(
            true, 3, null,
            UpstreamProtocolInfo.NotApplicable("Ein CLI-Upstream spricht kein MCP.")));

        report.Negotiation.Should().NotBeNull();
        var negotiation = report.Negotiation!;
        negotiation.Availability.Should().Be(UpstreamProtocolAvailability.NotApplicable);
        negotiation.ProtocolVersion.Should().BeNull("es gibt keine, nicht: sie fehlt");
        negotiation.ProtocolLabel.Should().Be("kein MCP — nicht zutreffend");
        negotiation.Note.Should().Contain("spricht kein MCP");
    }

    /// <summary>
    /// „Unbekannt" ist die andere Antwort — und sie nennt den Grund. Beides gleich zu melden wäre
    /// eine Auskunft, mit der niemand etwas anfangen kann.
    /// </summary>
    [Fact]
    public async Task An_unavailable_version_says_why()
    {
        var report = await RunAsync(Http(), new UpstreamTestResult(
            true, 1, null, UpstreamProtocolInfo.Unknown("Das SDK nennt keine ausgehandelte Fassung.")));

        report.Negotiation.Should().NotBeNull();
        var negotiation = report.Negotiation!;
        negotiation.Availability.Should().Be(UpstreamProtocolAvailability.Unknown);
        negotiation.ProtocolLabel.Should().Be("nicht ermittelt");
        negotiation.Note.Should().Contain("keine ausgehandelte Fassung");
    }

    /// <summary>
    /// Kein Ersatzwert, wenn gar nichts kam. Was aus der Konfiguration abgeleitet wäre, sähe aus wie
    /// eine Messung — die Regel aus WP4.6 bleibt.
    /// </summary>
    [Fact]
    public async Task Without_any_observation_nothing_is_invented()
    {
        var report = await RunAsync(Http(), new UpstreamTestResult(true, 4, null));

        report.Negotiation.Should().NotBeNull();
        var negotiation = report.Negotiation!;
        negotiation.ProtocolVersion.Should().BeNull();
        negotiation.Availability.Should().Be(UpstreamProtocolAvailability.Unknown);
        negotiation.Note.Should().Contain("Kein Ersatzwert");
        negotiation.ToolCount.Should().Be(4, "was beobachtet wurde, steht trotzdem da");
    }

    /// <summary>
    /// Die stehende Verbindung schlägt den transienten Versuch: Sie ist die, die den Verkehr trägt.
    /// </summary>
    [Fact]
    public async Task The_standing_connection_wins_over_the_transient_attempt()
    {
        var probe = new FakeNegotiationProbe(new UpstreamNegotiation(
            "StreamableHttp", "2025-11-25", ["tools"], 9, null,
            UpstreamProtocolAvailability.Negotiated));

        var report = await RunAsync(
            Http(),
            new UpstreamTestResult(true, 2, null, UpstreamProtocolInfo.Negotiated("2026-07-28")),
            probe);

        report.Negotiation!.ProtocolVersion.Should().Be("2025-11-25");
        probe.SeenKind.Should().Be(UpstreamTransportKind.StreamableHttp,
            "die Sonde sieht eine Verbindung, nicht deren Bauart — der Transport kommt vom Aufrufer");
    }

    // ───────────────────────── Die Redaktion ─────────────────────────

    /// <summary>
    /// Ein Capability-Objekt kann Felder tragen, die niemand vorhergesehen hat — die Namen kommen
    /// von der Gegenstelle. Sie laufen deshalb durch dieselbe Redaktion wie jeder andere Fremdtext
    /// (M2-Vertrag §6, Invariante 2). Vorher lief hier gar nichts durch.
    /// </summary>
    [Theory]
    [InlineData("experimental:token=gLbQ7fZ2rXm")]
    [InlineData("extensions:api_key: gLbQ7fZ2rXm")]
    public async Task Capability_names_run_through_the_redaction(string name)
    {
        var report = await RunAsync(Http(), new UpstreamTestResult(
            true, 1, null, UpstreamProtocolInfo.Negotiated("2026-07-28", [name])));

        report.Negotiation!.Capabilities.Should().NotContain(
            entry => entry.Contains("gLbQ7fZ2rXm", StringComparison.Ordinal),
            "was hier durchkommt, verlaesst das Haus");
        report.Negotiation!.Capabilities.Should().ContainSingle(entry =>
            entry.Contains(DiagnosticRedaction.Mask, StringComparison.Ordinal));
    }

    /// <summary>Auch die Begründung ist Fremdtext — sie kommt aus der Verbindung.</summary>
    [Fact]
    public async Task The_reason_runs_through_the_redaction_as_well()
    {
        var report = await RunAsync(Http(), new UpstreamTestResult(
            true, 0, null,
            UpstreamProtocolInfo.Unknown("Abgewiesen mit Authorization: Bearer gLbQ7fZ2rXm")));

        report.Negotiation!.Note.Should().NotContain("gLbQ7fZ2rXm");
        report.Negotiation!.Note.Should().Contain(DiagnosticRedaction.Mask);
    }

    /// <summary>
    /// Der Bericht ist eine Auskunft für einen Menschen, kein Abbild der Gegenstelle: Eine Flut von
    /// Namen wird gekürzt — und die Kürzung steht da, statt still zu geschehen.
    /// </summary>
    [Fact]
    public async Task A_flood_of_capability_names_is_capped_in_the_report()
    {
        var flood = Enumerable.Range(0, 90).Select(index => $"extensions:e{index:D3}").ToList();

        var report = await RunAsync(Http(), new UpstreamTestResult(
            true, 1, null, UpstreamProtocolInfo.Negotiated("2026-07-28", flood)));

        report.Negotiation!.Capabilities.Should().HaveCount(41);
        report.Negotiation!.Capabilities[^1].Should().Contain("weitere");
    }

    // ───────────────────────── Aufbau ─────────────────────────

    private static Task<UpstreamDiagnosticReport> RunAsync(
        UpstreamServerConfig config,
        UpstreamTestResult result,
        IUpstreamNegotiationProbe? negotiation = null)
        => new UpstreamConnectionDiagnostics(
                new StaticTester(result),
                hostExecution: null,
                resolution: new StaticResolution(),
                files: new FakeFileProbe(),
                processes: new FakeProcessProbe(),
                negotiation: negotiation)
            .DiagnoseAsync(config, TestContext.Current.CancellationToken);

    private static UpstreamServerConfig Http() => new(
        "ziel", "Ziel", UpstreamTransportKind.StreamableHttp, true,
        Http: new HttpTransportOptions(
            new Uri("https://example.invalid/mcp"), AllowPrivateTargets: false));

    private sealed class StaticTester(UpstreamTestResult result) : IUpstreamConnectionTester
    {
        public Task<UpstreamTestResult> TestAsync(UpstreamServerConfig config, CancellationToken ct)
            => Task.FromResult(result);
    }

    private sealed class StaticResolution : IHostResolutionProbe
    {
        public Task<HostResolution> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult(new HostResolution(true, ["93.184.216.34"]));
    }

    private sealed class FakeNegotiationProbe(UpstreamNegotiation negotiation) : IUpstreamNegotiationProbe
    {
        public UpstreamTransportKind? SeenKind { get; private set; }

        public Task<UpstreamNegotiation?> DescribeAsync(
            string slug, UpstreamTransportKind kind, CancellationToken ct)
        {
            SeenKind = kind;
            return Task.FromResult<UpstreamNegotiation?>(negotiation);
        }
    }
}
