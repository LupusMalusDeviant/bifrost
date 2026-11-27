using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Core.Invocation;
using Xunit;

namespace Faolan.Core.Tests.Invocation;

/// <summary>
/// FR-26: Der Gateway muss Calls, Fehlerquote und Latenzen **pro Server und Tool** messbar machen.
/// Der Export selbst hängt am Host (OTLP); hier wird geprüft, dass überhaupt und mit den richtigen
/// Dimensionen gemessen wird — sonst exportiert der Host nichts Verwertbares.
/// </summary>
public class MetricsTests
{
    private sealed record Measurement(string Instrument, double Value, Dictionary<string, string> Tags);

    private static List<Measurement> Collect(
        Action<InvokerTestWorld> arrange, Func<InvokerTestWorld, Task> act, out InvokerTestWorld world)
    {
        var measurements = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ToolInvoker.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? _)
            where T : struct
        {
            var dict = new Dictionary<string, string>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value?.ToString() ?? "";
            }

            lock (measurements)
            {
                measurements.Add(new Measurement(instrument.Name, Convert.ToDouble(value), dict));
            }
        }

        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.Start();

        world = new InvokerTestWorld();
        arrange(world);
        act(world).GetAwaiter().GetResult();
        listener.Dispose();

        // Nur die eigenen Messungen: der Meter ist prozessweit, parallele Tests messen mit.
        var slug = world.Slug;
        lock (measurements)
        {
            return [.. measurements.Where(m => m.Tags.GetValueOrDefault("server") == slug)];
        }
    }

    [Fact]
    public void Successful_call_is_counted_with_server_tool_and_status()
    {
        IdentityId admin = default;
        var measurements = Collect(
            w => admin = w.RegisterAdmin(),
            async w => await w.Invoker.InvokeAsync(
                InvokerTestWorld.Request(admin, w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken),
            out var world);

        var call = measurements.Should().ContainSingle(m => m.Instrument == "faolan.tool_calls").Subject;
        call.Value.Should().Be(1);
        call.Tags["server"].Should().Be(world.Slug, "FR-26 verlangt Auswertung pro Server");
        call.Tags["tool"].Should().Be(world.Echo.Value);
        call.Tags["status"].Should().Be(nameof(InvocationStatus.Success));
        call.Tags["origin"].Should().Be(nameof(CallOrigin.Mcp));
        call.Tags["fault"].Should().Be("none", "ein gewöhnlicher Aufruf ist kein Gateway-Fehler");

        var duration = measurements.Should().ContainSingle(m => m.Instrument == "faolan.tool_call_duration").Subject;
        duration.Tags["server"].Should().Be(world.Slug);
        duration.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Review-Befund L-3, dreizehnte Runde: Ein Katalogeintrag mit kaputtem Namen ist ein Fehler des
    /// Gateways. Er zählt als <c>fault=gateway</c>, nicht <c>none</c> — sonst lastete eine
    /// Fehlerquote je Upstream ihn dem Server an — und der Upstream wird nicht gerufen.
    /// </summary>
    [Fact]
    public void A_catalog_entry_with_a_broken_name_is_counted_as_a_gateway_fault()
    {
        IdentityId admin = default;
        ToolInvocationResult? result = null;
        var measurements = Collect(
            w => admin = w.RegisterAdmin(),
            async w =>
            {
                var invoker = new ToolInvoker(
                    w.Authorization, w.RateLimiter, new BrokenNameCatalog(w.Catalog), w.Supervisor, w.Audit, w.Redaction, w.Time);
                result = await invoker.InvokeAsync(
                    InvokerTestWorld.Request(admin, w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);
            },
            out var world);

        result!.Status.Should().Be(InvocationStatus.UpstreamError);
        result.ErrorMessage.Should().StartWith("Interner Gateway-Fehler");
        world.Connection.CallCount.Should().Be(0, "der Upstream wird nicht gerufen");
        measurements.Should().ContainSingle(m => m.Instrument == "faolan.tool_calls")
            .Which.Tags["fault"].Should().Be("gateway");
    }

    /// <summary>Gibt jeden Eintrag mit einem Namen ohne Trenner zurück — den es regulär nicht gibt.</summary>
    private sealed class BrokenNameCatalog(IToolCatalog inner) : IToolCatalog
    {
        public IReadOnlyList<CatalogEntry> Snapshot => inner.Snapshot;

        public event EventHandler<CatalogChangedEventArgs>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public ProfileView GetViewFor(IdentityId identity) => inner.GetViewFor(identity);

        public IReadOnlyList<ToolSearchHit> Search(IdentityId identity, string query, int limit)
            => inner.Search(identity, query, limit);

        public CatalogEntry? Find(NamespacedToolName name)
            => inner.Find(name) is { } entry ? entry with { Name = new NamespacedToolName("ohnetrenner") } : null;
    }

    [Fact]
    public void Denied_call_is_counted_so_error_rate_is_derivable()
    {
        IdentityId restricted = default;
        var measurements = Collect(
            w => restricted = w.RegisterAgent(),
            async w => await w.Invoker.InvokeAsync(
                InvokerTestWorld.Request(restricted, w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken),
            out _);

        measurements.Should().ContainSingle(m => m.Instrument == "faolan.tool_calls")
            .Which.Tags["status"].Should().Be(nameof(InvocationStatus.Denied),
                "ohne Status-Dimension ließe sich keine Fehlerquote bilden");
    }
}
