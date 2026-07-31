using System.Diagnostics;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Persistence;
using Bifrost.Persistence.Audit;
using Xunit;

namespace Bifrost.Integration.Tests.Persistence;

public class AuditSinkLatencyTests
{
    /// <summary>WP3-DoD: Record() ist nicht-blockierend — p99 unter 50 µs (ohne laufenden Writer, Channel absorbiert).</summary>
    [Fact]
    public void Record_p99_stays_under_50_microseconds()
    {
        var sink = new ChannelAuditSink(capacity: 200_000);
        var evt = new AuditEvent(
            DateTimeOffset.UtcNow, IdentityId.New(), CallOrigin.Mcp, AuditEventKind.ToolCall,
            ServerId.New(), "srv__tool", InvocationStatus.Success, null, 1, 2, TimeSpan.FromMilliseconds(1));

        for (var i = 0; i < 5_000; i++)
        {
            sink.Record(evt); // Warmup (JIT, Channel-Interna)
        }

        const int samples = 50_000;
        var ticks = new long[samples];
        for (var i = 0; i < samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            sink.Record(evt);
            ticks[i] = Stopwatch.GetTimestamp() - start;
        }

        Array.Sort(ticks);
        var p99 = TimeSpan.FromTicks((long)(ticks[(int)(samples * 0.99)] * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

        p99.Should().BeLessThan(TimeSpan.FromMicroseconds(50),
            "der Hot Path darf nie am Audit hängen (ADR-0007, DON'T Nr. 3)");
        sink.DroppedCount.Should().Be(0);
    }

    [Fact]
    public void Full_channel_drops_instead_of_blocking()
    {
        var sink = new ChannelAuditSink(capacity: 10);
        var evt = new AuditEvent(
            DateTimeOffset.UtcNow, null, CallOrigin.Ui, AuditEventKind.ServerLifecycle,
            null, null, null, null, null, null, null);

        for (var i = 0; i < 25; i++)
        {
            sink.Record(evt);
        }

        sink.DroppedCount.Should().Be(15, "bei vollem Channel wird gezählt verworfen, nie blockiert");
    }

    [Fact]
    public void Compliance_mode_never_silently_drops_a_full_channel()
    {
        var sink = new ChannelAuditSink(
            capacity: 1, mode: AuditDeliveryMode.Compliance);
        var evt = new AuditEvent(
            DateTimeOffset.UtcNow, null, CallOrigin.Ui, AuditEventKind.ServerLifecycle,
            null, null, null, null, null, null, null);
        sink.Record(evt);

        Action act = () => sink.Record(evt);

        act.Should().Throw<AuditUnavailableException>();
        sink.IsHealthy.Should().BeFalse();
    }
}
