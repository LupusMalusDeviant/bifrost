using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Persistence.Audit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Plan 0005 G6 (Vertrag N33): Verliert die Pin-Annahme den Vergleich-und-Tausch, berichtigt eine
/// Zeile <c>tool-definition-accept-conflict</c> die vorab geschriebene Annahme. Verwirft das Audit
/// diese Berichtigung (voller Kanal), stünde dort eine Annahme, die es nicht gab — dann muss der
/// Verlustzähler des Audits steigen, damit die Lücke sichtbar ist.
/// </summary>
public sealed class ToolDefinitionAcceptLossTests : IClassFixture<ToolDefinitionAcceptLossTests.LossFixture>
{
    private readonly LossFixture _gw;

    public ToolDefinitionAcceptLossTests(LossFixture gw) => _gw = gw;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewHash() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_discarded_conflict_correction_counts_as_audit_loss()
    {
        var (_, apiKey) = await _gw.SeedAdminAsync($"pin-verlust-{Guid.NewGuid():N}");
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var server = ServerId.New();
        var tool = $"werkzeug-{Guid.NewGuid():N}";
        var pending = NewHash();
        _gw.Pins.Set(new ToolDefinitionPin(server, tool, NewHash(), DateTimeOffset.UnixEpoch, pending, DateTimeOffset.UnixEpoch));
        _gw.Pins.ReplacePendingBeforeAccept(server, NewHash());
        var lossBefore = _gw.Channel.DroppedCount;

        _gw.Audit.RefuseOffers = true;
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(
                $"/api/v1/tool-definitions/{server.Value}/{tool}/accept?pendingHash={pending}", null, Ct)
                .WaitAsync(TimeSpan.FromMinutes(1), Ct);
        }
        finally
        {
            _gw.Audit.RefuseOffers = false;
        }

        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        _gw.Audit.Recorded.Where(e => e.Server == server).Select(e => e.Tool)
            .Should().Equal([$"tool-definition-accepted:{tool}@{pending}"],
                "die Annahme-Zeile steht, ihre Berichtigung wurde verworfen");
        _gw.Audit.RefusedOffers.Should().Be(1, "die Berichtigung wurde angeboten und abgewiesen");
        _gw.Channel.DroppedCount.Should().Be(lossBefore + 1,
            "eine verworfene Berichtigung ist ein Verlust — sonst fehlt sie, ohne dass es jemand sieht");
    }

    /// <summary>Der echte Gateway-Host; Pin-Ablage steuerbar, das Audit weist Angebote auf Wunsch ab.</summary>
    public sealed class LossFixture : GatewayFixture
    {
        public ToolDefinitionAcceptTests.ScriptedPinStore Pins
            => Services.GetRequiredService<ToolDefinitionAcceptTests.ScriptedPinStore>();

        public OfferRefusingAudit Audit => Services.GetRequiredService<OfferRefusingAudit>();

        public ChannelAuditSink Channel => Services.GetRequiredService<ChannelAuditSink>();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ToolDefinitionAcceptTests.ScriptedPinStore>();
                services.AddSingleton<IToolDefinitionPinStore>(
                    sp => sp.GetRequiredService<ToolDefinitionAcceptTests.ScriptedPinStore>());
                services.AddSingleton(sp => new OfferRefusingAudit(sp.GetRequiredService<ChannelAuditSink>()));
                services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<OfferRefusingAudit>());
            });
        }
    }

    /// <summary>
    /// Reicht ans echte Audit durch. <see cref="TryRecord"/> weist auf Wunsch ab wie ein voller
    /// Kanal; <see cref="ReportDiscarded"/> geht an den echten Verlustzähler.
    /// </summary>
    public sealed class OfferRefusingAudit(ChannelAuditSink inner) : IAuditSink
    {
        private readonly ConcurrentQueue<AuditEvent> _recorded = new();
        private volatile bool _refuseOffers;
        private int _refusedOffers;

        public bool RefuseOffers
        {
            get => _refuseOffers;
            set => _refuseOffers = value;
        }

        public int RefusedOffers => Volatile.Read(ref _refusedOffers);

        public IReadOnlyList<AuditEvent> Recorded => [.. _recorded];

        public void Record(AuditEvent evt)
        {
            inner.Record(evt);
            _recorded.Enqueue(evt);
        }

        public bool TryRecord(AuditEvent evt)
        {
            if (_refuseOffers)
            {
                Interlocked.Increment(ref _refusedOffers);
                return false;
            }

            if (!inner.TryRecord(evt))
            {
                return false;
            }

            _recorded.Enqueue(evt);
            return true;
        }

        public void ReportDiscarded(int count) => inner.ReportDiscarded(count);
    }
}
