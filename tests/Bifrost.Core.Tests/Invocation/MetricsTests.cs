using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Xunit;

namespace Bifrost.Core.Tests.Invocation;

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

        var call = measurements.Should().ContainSingle(m => m.Instrument == "bifrost.tool_calls").Subject;
        call.Value.Should().Be(1);
        call.Tags["server"].Should().Be(world.Slug, "FR-26 verlangt Auswertung pro Server");
        call.Tags["tool"].Should().Be(world.Echo.Value);
        call.Tags["status"].Should().Be(nameof(InvocationStatus.Success));
        call.Tags["origin"].Should().Be(nameof(CallOrigin.Mcp));

        var duration = measurements.Should().ContainSingle(m => m.Instrument == "bifrost.tool_call_duration").Subject;
        duration.Tags["server"].Should().Be(world.Slug);
        duration.Value.Should().BeGreaterThanOrEqualTo(0);
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

        measurements.Should().ContainSingle(m => m.Instrument == "bifrost.tool_calls")
            .Which.Tags["status"].Should().Be(nameof(InvocationStatus.Denied),
                "ohne Status-Dimension ließe sich keine Fehlerquote bilden");
    }
}
