using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Core.Execution;
using Bifrost.Server.Execution;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Bifrost.Integration.Tests.Execution;

/// <summary>
/// Der Startschritt der Bestandsübernahme im Serverprozess (ADR-0025 E3): Was sieht ein Betreiber,
/// wenn seine bestehende Instanz nach dem Upgrade hochkommt?
/// <para>
/// Der Unterschied zwischen „läuft weiter" und „läuft weiter, und alle wissen warum" ist der ganze
/// Zweck dieser Entscheidung — und er hängt an genau dem, was hier geprüft wird: Der Wert steht
/// geschrieben da, der Audit-Eintrag nennt die betroffenen Upstreams beim Namen, und die Instanz
/// startet.
/// </para>
/// </summary>
public sealed class HostExecutionStartupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), $"bfr-pol-start-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Eine_bestandsinstanz_laeuft_weiter_und_der_wert_steht_danach_geschrieben()
    {
        var store = new HostExecutionSettingFile(_dataDirectory);
        var audit = new CollectingAuditSink();
        var startup = Startup(store, audit, environmentValue: null);

        var state = startup.Run(Persisted(Stdio("alt-stdio"), Cli("alt-cli")));

        state.Allowed.Should().BeTrue("die Instanz wird nicht stillgelegt");
        state.ReasonCode.Should().Be(HostExecutionReason.AdoptedFromExistingInstance);

        var written = store.Read();
        written.Should().NotBeNull();
        written!.Allowed.Should().BeTrue();
        written.Origin.Should().Be(HostExecutionOrigin.AdoptedFromExistingInstance);
        written.Upstreams.Should().HaveCount(2);
    }

    [Fact]
    public void Der_audit_eintrag_nennt_jeden_betroffenen_upstream_namentlich()
    {
        var audit = new CollectingAuditSink();

        Startup(new HostExecutionSettingFile(_dataDirectory), audit, null)
            .Run(Persisted(Stdio("alt-stdio"), Cli("alt-cli"), Http("web")));

        var entry = audit.Events.Should().ContainSingle().Which;
        entry.Kind.Should().Be(AuditEventKind.ConfigChanged);
        entry.Origin.Should().Be(CallOrigin.System);
        entry.Detail.Should().Contain("alt-stdio");
        entry.Detail.Should().Contain("alt-cli");
        entry.Detail.Should().Contain(HostExecutionReason.AdoptedFromExistingInstance);
        entry.Detail.Should().NotContain("web", "isolierte Upstreams sind nicht betroffen");
    }

    [Fact]
    public void Eine_frische_instanz_verbietet_und_haelt_auch_das_fest()
    {
        var store = new HostExecutionSettingFile(_dataDirectory);
        var audit = new CollectingAuditSink();

        var state = Startup(store, audit, null).Run(Persisted());

        state.Allowed.Should().BeFalse();
        state.Adopted.Should().BeFalse();
        store.Read()!.Allowed.Should().BeFalse();
        audit.Events.Should().ContainSingle().Which.Detail.Should().Contain("verboten");
    }

    [Fact]
    public void Ein_zweiter_start_meldet_die_uebernahme_nicht_erneut()
    {
        var store = new HostExecutionSettingFile(_dataDirectory);
        var audit = new CollectingAuditSink();
        var startup = Startup(store, audit, null);

        startup.Run(Persisted(Stdio("alt-stdio")));
        startup.Run(Persisted(Stdio("alt-stdio")));

        audit.Events.Should().ContainSingle("der Startschritt ist idempotent");
    }

    private static HostExecutionStartup Startup(
        IHostExecutionSettingStore store, IAuditSink audit, string? environmentValue)
    {
        var time = new FixedTime(Now);
        return new HostExecutionStartup(
            new HostExecutionCoordinator(store, environmentValue, time),
            audit,
            time,
            NullLogger<HostExecutionStartup>.Instance);
    }

    private static Dictionary<ServerId, UpstreamConfigVersion> Persisted(
        params UpstreamServerConfig[] configs)
        => configs.ToDictionary(
            _ => ServerId.New(),
            config => new UpstreamConfigVersion(new ConfigVersionId(1), config, Now));

    private static UpstreamServerConfig Stdio(string slug)
        => new(slug, slug, UpstreamTransportKind.Stdio, true,
            Stdio: new StdioTransportOptions("/usr/bin/tool", []));

    private static UpstreamServerConfig Cli(string slug)
        => new(slug, slug, UpstreamTransportKind.Cli, true,
            Cli: new CliTransportOptions("/usr/bin/tool", [new CliToolSpec("lauf")]));

    private static UpstreamServerConfig Http(string slug)
        => new(slug, slug, UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(new Uri("https://example.invalid/mcp")));

    /// <summary>Eine feste Uhr — dieses Projekt bringt kein Test-TimeProvider-Paket mit.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CollectingAuditSink : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public void Record(AuditEvent evt) => Events.Add(evt);
    }
}
