using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Integration.Tests.Upstreams;
using Bifrost.Persistence.Audit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// <c>POST /api/v1/tool-definitions/{server}/{tool}/accept</c> (Vertrag „Selbsttätige
/// Wiederverbindung", N24; Plan 0005 F4): Vergleich-und-Tausch, Audit vor der Annahme, 202 bei
/// gescheiterter Neuabfrage. Pin-Ablage und Audit sind hier steuerbar; die echte Ablage ist an
/// ihrem eigenen Ort geprüft.
/// </summary>
public sealed class ToolDefinitionAcceptTests : IClassFixture<ToolDefinitionAcceptTests.AcceptFixture>
{
    private readonly AcceptFixture _gw;

    public ToolDefinitionAcceptTests(AcceptFixture gw) => _gw = gw;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewHash() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync($"pin-annahme-{Guid.NewGuid():N}");
        var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        return client;
    }

    private (ServerId Server, string Tool, string Pending) Pending(ServerId? server = null)
    {
        var id = server ?? ServerId.New();
        var tool = $"werkzeug-{Guid.NewGuid():N}";
        var pending = NewHash();
        _gw.Pins.Set(new ToolDefinitionPin(id, tool, NewHash(), DateTimeOffset.UnixEpoch, pending, DateTimeOffset.UnixEpoch));
        return (id, tool, pending);
    }

    private IReadOnlyList<string> AuditSubjects(ServerId server)
        => [.. _gw.Audit.Recorded.Where(e => e.Server == server).Select(e => e.Tool ?? string.Empty)];

    [Fact]
    public async Task The_shown_version_is_accepted_and_audited_with_its_hash()
    {
        using var client = await AdminClientAsync();
        var (server, tool, pending) = Pending();

        using var response = await client.PostAsync(
            $"/api/v1/tool-definitions/{server.Value}/{tool}/accept?pendingHash={pending}", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _gw.Pins.Get(server, tool).AcceptedHash.Should().Be(pending);
        AuditSubjects(server).Should().Equal($"tool-definition-accepted:{tool}@{pending}");
    }

    [Fact]
    public async Task Another_pending_version_is_a_conflict_and_nothing_is_accepted()
    {
        using var client = await AdminClientAsync();
        var (server, tool, pending) = Pending();

        using var response = await client.PostAsync(
            $"/api/v1/tool-definitions/{server.Value}/{tool}/accept?pendingHash={NewHash()}", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _gw.Pins.Get(server, tool).PendingHash.Should().Be(pending, "die anstehende Fassung bleibt zurückgehalten");
        AuditSubjects(server).Should().BeEmpty("es wurde nichts angenommen");
    }

    /// <summary>
    /// F4: Weist das Audit ab (Compliance-Modus, voller Kanal), wird nichts vertraut. Vorher galt
    /// die Fassung schon als angenommen, als das Audit warf.
    /// </summary>
    [Fact]
    public async Task A_refusing_audit_leaves_the_version_withheld()
    {
        using var client = await AdminClientAsync();
        var (server, tool, pending) = Pending();

        _gw.Audit.Refuse = true;
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(
                $"/api/v1/tool-definitions/{server.Value}/{tool}/accept?pendingHash={pending}", null, Ct);
        }
        finally
        {
            _gw.Audit.Refuse = false;
        }

        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct))
                .GetProperty("error").GetString().Should().Contain("nicht angenommen");
        }

        _gw.Pins.AcceptCalls(server).Should().Be(0, "ohne Beleg im Audit wird nicht angenommen");
        _gw.Pins.Get(server, tool).PendingHash.Should().Be(pending);
    }

    /// <summary>
    /// F4: Steht zwischen Prüfung und Tausch eine neue Fassung an, ist die vorab geschriebene
    /// Audit-Zeile durch eine zweite berichtigt — keine Zeile behauptet eine Annahme, die es nicht gab,
    /// ohne dass die nächste es sagt.
    /// </summary>
    [Fact]
    public async Task A_version_replaced_during_the_acceptance_is_a_conflict_and_the_audit_says_so()
    {
        using var client = await AdminClientAsync();
        var (server, tool, pending) = Pending();
        _gw.Pins.ReplacePendingBeforeAccept(server, NewHash());

        using var response = await client.PostAsync(
            $"/api/v1/tool-definitions/{server.Value}/{tool}/accept?pendingHash={pending}", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _gw.Pins.Get(server, tool).AcceptedHash.Should().NotBe(pending);
        AuditSubjects(server).Should().Equal(
            $"tool-definition-accepted:{tool}@{pending}",
            $"tool-definition-accept-conflict:{tool}@{pending}");
    }

    /// <summary>N24: Scheitert die Neuabfrage nach der Annahme, bleibt sie angenommen — 202 mit Warnung.</summary>
    [Fact]
    public async Task A_failed_rediscovery_after_the_acceptance_answers_202()
    {
        await using var upstream = new ScriptedMcpUpstream();
        await upstream.StartAsync();
        var id = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                $"annahme-{Guid.NewGuid():N}"[..20], "Annahme", UpstreamTransportKind.StreamableHttp, Enabled: true,
                Http: new HttpTransportOptions(upstream.Endpoint)),
            Ct);
        try
        {
            await IntegrationSupport.WaitUntilAsync(
                () => _gw.Supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
                because: "der Upstream muss zuerst verbunden sein");
            using var client = await AdminClientAsync();
            var (server, tool, pending) = Pending(id);
            upstream.RejectWith = 401;

            using var response = await client.PostAsync(
                $"/api/v1/tool-definitions/{server.Value}/{tool}/accept?pendingHash={pending}", null, Ct);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await response.Content.ReadFromJsonAsync<JsonElement>(Ct))
                .GetProperty("warning").GetString().Should().Contain("Angenommen");
            _gw.Pins.Get(server, tool).AcceptedHash.Should().Be(pending);
            AuditSubjects(server).Should().Contain($"tool-definition-accepted:{tool}@{pending}");
        }
        finally
        {
            await _gw.Supervisor.RemoveAsync(id, DrainPolicy.Immediate, CancellationToken.None);
        }
    }

    /// <summary>Der echte Gateway-Host mit steuerbarer Pin-Ablage und abschaltbarem Audit.</summary>
    public sealed class AcceptFixture : GatewayFixture
    {
        public ScriptedPinStore Pins => Services.GetRequiredService<ScriptedPinStore>();

        public SwitchableAudit Audit => Services.GetRequiredService<SwitchableAudit>();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ScriptedPinStore>();
                services.AddSingleton<IToolDefinitionPinStore>(sp => sp.GetRequiredService<ScriptedPinStore>());
                services.AddSingleton(sp => new SwitchableAudit(sp.GetRequiredService<ChannelAuditSink>()));
                services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<SwitchableAudit>());
            });
        }
    }

    /// <summary>Reicht ans echte Audit durch — oder weist ab wie ein voller Kanal im Compliance-Modus.</summary>
    public sealed class SwitchableAudit(IAuditSink inner) : IAuditSink
    {
        private readonly ConcurrentQueue<AuditEvent> _recorded = new();
        private volatile bool _refuse;

        public bool Refuse
        {
            get => _refuse;
            set => _refuse = value;
        }

        public IReadOnlyList<AuditEvent> Recorded => [.. _recorded];

        public void Record(AuditEvent evt)
        {
            if (_refuse)
            {
                throw new AuditUnavailableException("Audit-Channel ist voll (Test).");
            }

            inner.Record(evt);
            _recorded.Enqueue(evt);
        }
    }

    /// <summary>Eine Pin-Ablage mit Vergleich-und-Tausch wie die echte, aber im Arbeitsspeicher.</summary>
    public sealed class ScriptedPinStore : IToolDefinitionPinStore
    {
        private readonly ConcurrentDictionary<(ServerId, string), ToolDefinitionPin> _pins = new();
        private readonly ConcurrentDictionary<ServerId, int> _acceptCalls = new();
        private readonly ConcurrentDictionary<ServerId, string> _replaceBeforeAccept = new();

        public IReadOnlyList<ToolDefinitionPin> All => [.. _pins.Values];

        public event EventHandler<ToolDefinitionPinChangedEventArgs>? Changed;

        public void Set(ToolDefinitionPin pin) => _pins[(pin.Server, pin.Tool)] = pin;

        public ToolDefinitionPin Get(ServerId server, string tool) => _pins[(server, tool)];

        public int AcceptCalls(ServerId server) => _acceptCalls.TryGetValue(server, out var calls) ? calls : 0;

        /// <summary>Kurz vor dem Tausch meldet der Upstream noch eine Fassung — das Rennen aus E7.</summary>
        public void ReplacePendingBeforeAccept(ServerId server, string hash) => _replaceBeforeAccept[server] = hash;

        public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<ToolDefinitionVerdict> VerifyAsync(ServerId server, string tool, string hash, CancellationToken ct)
            => Task.FromResult(ToolDefinitionVerdict.FirstSeen);

        public Task<bool> AcceptAsync(ServerId server, string tool, string expectedPendingHash, CancellationToken ct)
        {
            _acceptCalls.AddOrUpdate(server, 1, (_, calls) => calls + 1);
            var key = (server, tool);
            if (_replaceBeforeAccept.TryRemove(server, out var replaced))
            {
                _pins[key] = _pins[key] with { PendingHash = replaced };
            }

            var pin = _pins[key];
            if (!string.Equals(pin.PendingHash, expectedPendingHash, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            _pins[key] = pin with { AcceptedHash = pin.PendingHash!, PendingHash = null, PendingSince = null };
            Changed?.Invoke(this, new ToolDefinitionPinChangedEventArgs(server));
            return Task.FromResult(true);
        }

        public Task ForgetServerAsync(ServerId server, CancellationToken ct) => Task.CompletedTask;
    }
}
