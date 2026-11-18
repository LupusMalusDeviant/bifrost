using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Persistence;
using Bifrost.Persistence.Audit;
using Bifrost.Server.Diagnostics;

using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Bifrost.Integration.Tests.Persistence;

/// <summary>
/// Ein verworfener Audit-Batch hinterlässt eine Spur — und zwar eine, die jemand findet.
/// <para>
/// Bis v0.6 verwarf <see cref="AuditBatchWriter"/> im Modus <c>best-effort</c> einen nicht
/// schreibbaren Batch nach einer einzigen Logzeile: kein zweiter Versuch, kein Zähler in einem
/// Bericht. Für ein Produkt, dessen erstes Verkaufsargument ein lückenloses Audit-Log ist, war das
/// die unangenehmste Möglichkeit — nicht der Aufruf schlug fehl, sondern nur sein Nachweis.
/// </para>
/// </summary>
public sealed class AuditLossVisibilityTests
{
    [Fact]
    public async Task Best_effort_tries_again_before_it_gives_up()
    {
        var factory = new FailingDbContextFactory();
        var sink = new ChannelAuditSink(capacity: 100);
        var options = new PersistenceOptions
        {
            AuditFlushInterval = TimeSpan.FromMilliseconds(10),
            AuditRetryInterval = TimeSpan.FromMilliseconds(1),
            AuditMaxWriteAttempts = 3,
        };

        sink.Record(NewEvent());
        sink.Record(NewEvent());
        sink.Complete();

        await new AuditBatchWriter(sink, factory, options).RunAsync(CancellationToken.None);

        factory.Attempts.Should().Be(3,
            "ein gesperrter Schreib-Lock ist beim zweiten Anlauf meist weg — der Verzicht auf jeden "
            + "zweiten Versuch war nie durch den Vertrag der Hot-Path-Seite gedeckt, denn diese "
            + "Schleife läuft im Hintergrunddienst und hält keinen Aufruf auf");
        sink.UnwrittenCount.Should().Be(2, "und danach ist der Verlust gezählt, nicht verschwiegen");
        sink.Loss.FirstLossAt.Should().NotBeNull("der Bericht muss sagen können, seit wann");
        sink.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task Compliance_never_gives_up_on_a_batch()
    {
        var factory = new FailingDbContextFactory(failuresBeforeSuccess: 6);
        var sink = new ChannelAuditSink(capacity: 100, mode: AuditDeliveryMode.Compliance);
        var options = new PersistenceOptions
        {
            AuditMode = AuditDeliveryMode.Compliance,
            AuditFlushInterval = TimeSpan.FromMilliseconds(10),
            AuditRetryInterval = TimeSpan.FromMilliseconds(1),
            // Ausdrücklich kleiner als die Zahl der Fehlschläge: Im Compliance-Modus zählt dieser
            // Wert nicht, und genau das hält der Test fest.
            AuditMaxWriteAttempts = 2,
        };

        sink.Record(NewEvent());
        sink.Complete();

        await new AuditBatchWriter(sink, factory, options).RunAsync(CancellationToken.None);

        factory.Attempts.Should().Be(7, "wiederholt wird, bis es gelingt");
        sink.UnwrittenCount.Should().Be(0, "im Compliance-Modus wird nichts aufgegeben");
    }

    /// <summary>
    /// Der Weg vom Zähler in den Bericht. Ohne dieses Stück wäre der Verlust weiterhin nur eine
    /// Logzeile — der eigentliche Befund war nicht der fehlende Zähler, sondern der fehlende Weg.
    /// </summary>
    [Fact]
    public async Task A_discarded_batch_shows_up_in_the_diagnostic_report_with_its_number()
    {
        var factory = new FailingDbContextFactory();
        var sink = new ChannelAuditSink(capacity: 100);
        var options = new PersistenceOptions
        {
            AuditFlushInterval = TimeSpan.FromMilliseconds(10),
            AuditRetryInterval = TimeSpan.FromMilliseconds(1),
            AuditMaxWriteAttempts = 2,
        };

        for (var i = 0; i < 5; i++)
        {
            sink.Record(NewEvent());
        }

        sink.Complete();
        await new AuditBatchWriter(sink, factory, options).RunAsync(CancellationToken.None);

        var context = new DiagnosticContext
        {
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Audit = new ChannelAuditDiagnosticProbe(sink),
        };

        var report = await DiagnosticService
            .CreateDefault(context)
            .RunAsync(DiagnosticScope.Database, TestContext.Current.CancellationToken);

        var check = report.Checks.Single(c => c.Code == DiagnosticCodes.AuditCompleteness);
        check.Status.Should().Be(CheckStatus.Fail);
        check.Summary.Should().Contain("5", "der Bericht nennt die Zahl der verlorenen Einträge");
        check.SafeDetails!["aufgegeben_beim_schreiben"].Should().Be("5");
        check.SafeDetails["erster_verlust"].Should().NotBe("nicht vermerkt", "und seit wann");
    }

    /// <summary>
    /// V3 (Vertrag „Selbsttätige Wiederverbindung"): Ein Angebot, das abgewiesen und später erneut
    /// gemacht wird, ist kein Verlust. Sonst zählte bei vollem Kanal jeder Nachreich-Versuch, und
    /// der Bericht nennte eine Zahl, die mit den Lebenszeichen wächst statt mit den Lücken.
    /// </summary>
    [Fact]
    public void An_offer_that_is_refused_is_not_a_loss_until_it_is_given_up()
    {
        var sink = new ChannelAuditSink(capacity: 1, mode: AuditDeliveryMode.Compliance);
        IAuditSink offered = sink;
        offered.TryRecord(NewEvent()).Should().BeTrue();

        for (var i = 0; i < 10; i++)
        {
            offered.TryRecord(NewEvent()).Should().BeFalse("der Kanal ist voll");
        }

        sink.Loss.Total.Should().Be(0, "abgewiesen, aber noch beim Aufrufer — nicht verloren");
        sink.Loss.FirstLossAt.Should().BeNull();
        sink.IsHealthy.Should().BeTrue();

        offered.ReportDiscarded(3);

        sink.Loss.LostAtIntake.Should().Be(3, "verloren ist, was der Aufrufer aufgibt");
        sink.Loss.FirstLossAt.Should().NotBeNull();
        sink.IsHealthy.Should().BeFalse("im Compliance-Modus ist ein Verlust ein Befund");
    }

    private static AuditEvent NewEvent() => new(
        DateTimeOffset.UtcNow, IdentityId.New(), CallOrigin.Mcp, AuditEventKind.ToolCall,
        ServerId.New(), "srv__tool", InvocationStatus.Success, null, 1, 2, TimeSpan.FromMilliseconds(1));

    /// <summary>
    /// Eine Fabrik, die den Schreibversuch scheitern lässt — und mitzählt, wie oft es versucht wurde.
    /// Ein echter Sperrfehler wäre hier der falsche Aufbau: Er hinge an Zeitfenstern und würde den
    /// Test von der Maschine abhängig machen.
    /// </summary>
    private sealed class FailingDbContextFactory : IDbContextFactory<BifrostDbContext>
    {
        private readonly int _failuresBeforeSuccess;
        private int _attempts;

        public FailingDbContextFactory(int failuresBeforeSuccess = int.MaxValue)
            => _failuresBeforeSuccess = failuresBeforeSuccess;

        public int Attempts => Volatile.Read(ref _attempts);

        public BifrostDbContext CreateDbContext()
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= _failuresBeforeSuccess)
            {
                throw new InvalidOperationException("SQLite Error 5: 'database is locked'.");
            }

            // Der gelungene Versuch: eine Datenbank im Arbeitsspeicher, die den Batch annimmt.
            var options = new DbContextOptionsBuilder<BifrostDbContext>()
                .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
                .Options;
            var db = new BifrostDbContext(options);
            db.Database.EnsureCreated();
            return db;
        }
    }
}
