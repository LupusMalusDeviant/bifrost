using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Configuration;
using Bifrost.Core.Execution;
using Bifrost.Core.Packaging;
using Bifrost.Core.Tests.Configuration;
using Bifrost.Core.Tests.Packaging;
using Bifrost.Core.Tests.Upstreams;
using Bifrost.Core.Upstreams;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace Bifrost.Core.Tests.Execution;

/// <summary>
/// Alle Erzeugungswege gegen dieselbe Policy (ADR-0025 E4): API/Formular (Supervisor),
/// Verbindungstest, Paketimport und Konfigurationsimport.
/// <para>
/// Ein Weg, den man vergisst, ist keine Prüfung — und der Importpfad ist der naheliegende, weil er
/// eine Konfiguration mitbringt, die niemand eingetippt hat.
/// </para>
/// </summary>
public sealed class HostExecutionCreationPathTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);

    // ── Weg 1: API und UI-Formular laufen beide über den Supervisor ─────────────────────────────

    [Fact]
    public async Task Frische_instanz_lehnt_einen_host_upstream_ab()
    {
        await using var supervisor = Supervisor(HostExecutionPolicy.FreshInstance());

        var act = async () => await supervisor.AddAsync(
            HostExecutionAdoptionTests.Stdio("neu"), TestContext.Current.CancellationToken);

        var denied = await act.Should().ThrowAsync<HostExecutionDeniedException>();
        denied.Which.Decision.ReasonCode.Should().Be(HostExecutionReason.Forbidden);
        supervisor.Statuses.Should().BeEmpty("was abgelehnt wird, wird auch nicht angelegt");
    }

    /// <summary>
    /// Die Absage ist eine <see cref="ArgumentException"/> — damit beantworten API und UI sie mit
    /// ihrer vorhandenen Behandlung für Formularfehler statt mit einem Serverfehler.
    /// </summary>
    [Fact]
    public async Task Die_absage_kommt_als_argumentfehler_an_die_oberflaeche()
    {
        await using var supervisor = Supervisor(HostExecutionPolicy.FreshInstance());

        var act = async () => await supervisor.AddAsync(
            HostExecutionAdoptionTests.Stdio("neu"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Ein_isolierter_upstream_bleibt_auch_auf_einer_frischen_instanz_erlaubt()
    {
        await using var supervisor = Supervisor(HostExecutionPolicy.FreshInstance());

        var id = await supervisor.AddAsync(
            HostExecutionAdoptionTests.Cli("sicher", container: true),
            TestContext.Current.CancellationToken);

        supervisor.GetStatus(id).Should().NotBeNull();
    }

    [Fact]
    public async Task Ohne_verdrahtete_policy_startet_nichts_nativ()
    {
        await using var supervisor = Supervisor(policy: null);

        var act = async () => await supervisor.AddAsync(
            HostExecutionAdoptionTests.Stdio("neu"), TestContext.Current.CancellationToken);

        var denied = await act.Should().ThrowAsync<HostExecutionDeniedException>();
        denied.Which.Decision.ReasonCode.Should().Be(HostExecutionReason.Undetermined);
    }

    /// <summary>
    /// Ein Rollback holt seine Konfiguration aus der Historie — also aus der Zeit vor der
    /// Umstellung. Ohne Prüfung wäre er der bequemste Weg zurück zu einem nativ laufenden Programm.
    /// </summary>
    [Fact]
    public async Task Ein_rollback_in_die_zeit_vor_der_umstellung_wird_abgelehnt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryUpstreamConfigStore();
        var connector = new FakeUpstreamConnector();

        await using var permissive = new UpstreamSupervisor(
            [connector], store, new SupervisorOptions(), new FakeTimeProvider(Now),
            hostExecution: HostExecutionPolicy.AllowedByOperator());
        var id = await permissive.AddAsync(HostExecutionAdoptionTests.Stdio("alt"), ct);
        var historic = (await store.GetHistoryAsync(id, ct))[0].Version;
        await permissive.ReconfigureAsync(
            id, HostExecutionAdoptionTests.Cli("alt", container: true), ct);

        // Dieselbe Historie, aber eine Instanz, die native Ausführung verbietet.
        await using var strict = new UpstreamSupervisor(
            [connector], store, new SupervisorOptions(), new FakeTimeProvider(Now),
            hostExecution: HostExecutionPolicy.FreshInstance());
        await strict.RestoreAsync(
            id, new UpstreamConfigVersion(
                historic, HostExecutionAdoptionTests.Cli("alt", container: true), Now),
            ct);

        var act = async () => await strict.RollbackAsync(id, historic, ct);

        await act.Should().ThrowAsync<HostExecutionDeniedException>();
    }

    // ── Weg 2: Verbindung testen ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Der_verbindungstest_startet_nichts_was_nicht_starten_darf()
    {
        var connector = new FakeUpstreamConnector();
        var tester = new UpstreamConnectionTester([connector], HostExecutionPolicy.FreshInstance());

        var result = await tester.TestAsync(
            HostExecutionAdoptionTests.Stdio("probe"), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(HostExecutionReason.Forbidden);
        connector.Connections.Should().BeEmpty("der Test fuehrt das Programm wirklich aus");
    }

    // ── Weg 3: Paketimport ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ein_paket_mit_nativem_transport_kommt_nicht_an_der_policy_vorbei()
    {
        using var workspace = new PackageWorkspace();
        var installer = workspace.Installer(HostExecutionPolicy.FreshInstance());
        using var package = TestPackage.Valid(
            workspace.Publisher, transport: UpstreamTransportKind.Cli);

        var act = async () => await installer.InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        var denied = await act.Should().ThrowAsync<HostExecutionDeniedException>();
        denied.Which.Decision.ReasonCode.Should().Be(HostExecutionReason.Forbidden);
        workspace.Probes.Should().Be(0, "die Probe fuehrt das Paket bereits aus");
    }

    [Fact]
    public async Task Ein_isoliertes_paket_wird_weiterhin_installiert()
    {
        using var workspace = new PackageWorkspace();
        var installer = workspace.Installer(HostExecutionPolicy.FreshInstance());
        using var package = TestPackage.Valid(workspace.Publisher);

        var installed = await installer.InstallAsync(
            package, new ConnectorInstallOptions(), null, TestContext.Current.CancellationToken);

        installed.Package.State.Should().Be(PackageState.Active);
        workspace.Probes.Should().Be(1);
    }

    // ── Weg 4: Konfigurationsimport ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Der_konfigurationsimport_bringt_keinen_verbotenen_upstream_mit()
    {
        var ct = TestContext.Current.CancellationToken;
        var quelle = ConfigurationFixtures.WithSecretsEverywhere();
        var export = await ConfigurationFixtures.ServiceFor(quelle)
            .ExportAsync(new ConfigurationExportRequest(IncludeSecrets: false), ct);

        var ziel = new FakeInstance();
        var plan = await ConfigurationFixtures.ForbiddingServiceFor(ziel)
            .PlanImportAsync(export.Payload, null, ct);

        plan.Conflicts.Should().Contain(entry => entry.Contains(HostExecutionReason.Forbidden, StringComparison.Ordinal));
        plan.Additions.Should().NotContain(entry => entry.Contains("'mit-stdio'", StringComparison.Ordinal));
        plan.Additions.Should().Contain(entry => entry.Contains("'mit-http'", StringComparison.Ordinal),
            "der Rest der Datei ist deshalb nicht falsch");
    }

    private static UpstreamSupervisor Supervisor(IHostExecutionPolicy? policy)
        => new(
            [new FakeUpstreamConnector()],
            new InMemoryUpstreamConfigStore(),
            new SupervisorOptions(),
            new FakeTimeProvider(Now),
            hostExecution: policy);
}

/// <summary>Ein Paketverzeichnis samt Herausgeber für die Importtests.</summary>
internal sealed class PackageWorkspace : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bfr-pol-{Guid.NewGuid():N}");
    private readonly InMemoryPackageStore _store = new();

    public TestPublisher Publisher { get; } = new();

    public int Probes { get; private set; }

    public ConnectorPackageInstaller Installer(IHostExecutionPolicy policy)
        => new(
            _root,
            _store,
            new StaticTrustStore([Publisher.Key]),
            (_, _) =>
            {
                Probes++;
                return Task.CompletedTask;
            },
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero)),
            hostExecution: policy);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
