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

    /// <summary>
    /// Plan 0005 D14: Bricht das Herunterfahren eine Schreibung ab, schreibt der Drain denselben
    /// Batch an seinen Kettenstellen — keine übersprungene Stelle, die wie ein Verlust aussähe.
    /// </summary>
    [Fact]
    public async Task A_write_cancelled_by_shutdown_keeps_its_chain_positions()
    {
        var name = $"file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={name}");
        await keepAlive.OpenAsync(TestContext.Current.CancellationToken);
        var factory = new FailingDbContextFactory(failuresBeforeSuccess: 1, dataSource: name);
        var sink = new ChannelAuditSink(capacity: 100, mode: AuditDeliveryMode.Compliance);
        var options = new PersistenceOptions
        {
            AuditMode = AuditDeliveryMode.Compliance,
            AuditFlushInterval = TimeSpan.FromMilliseconds(10),
            // Lang: Der Abbruch soll die Schreibung mitten im Warten auf den zweiten Versuch treffen.
            AuditRetryInterval = TimeSpan.FromMinutes(5),
        };
        using var shutdown = new CancellationTokenSource();

        sink.Record(NewEvent("erstes"));
        var run = new AuditBatchWriter(sink, factory, options).RunAsync(shutdown.Token);
        await IntegrationSupport.WaitUntilAsync(() => factory.Attempts == 1, because: "der erste Versuch ist gescheitert");
        sink.Record(NewEvent("zweites"));
        sink.Complete();
        await shutdown.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await using var db = factory.CreateDbContext();
        var rows = await db.AuditEvents.OrderBy(r => r.ChainIndex).ToListAsync(TestContext.Current.CancellationToken);
        rows.Select(r => (r.ChainIndex, r.Tool)).Should().Equal(
            [(0L, "erstes"), (1L, "zweites")],
            "der abgebrochene Batch steht an der Stelle, an der er eingehängt war, und nichts dazwischen fehlt");
        rows[1].PreviousHash.Should().Be(rows[0].EntryHash);
    }

    /// <summary>
    /// Plan 0005 E9: Meldet das Schreiben einen Fehler, nachdem die Zeilen schon festgeschrieben sind,
    /// fügt der zweite Versuch sie nicht noch einmal ein — jede Kettenstelle steht genau einmal.
    /// </summary>
    [Fact]
    public async Task A_write_that_failed_after_commit_is_not_written_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={name}");
        await keepAlive.OpenAsync(ct);
        var factory = new FailingDbContextFactory(failuresBeforeSuccess: 0, dataSource: name, failAfterFirstCommit: true);
        var sink = new ChannelAuditSink(capacity: 100, mode: AuditDeliveryMode.Compliance);
        var options = new PersistenceOptions
        {
            AuditMode = AuditDeliveryMode.Compliance,
            AuditFlushInterval = TimeSpan.FromMilliseconds(10),
            AuditRetryInterval = TimeSpan.FromMilliseconds(1),
        };

        sink.Record(NewEvent("eins"));
        sink.Record(NewEvent("zwei"));
        sink.Complete();

        // Begrenzt: Ohne die Prüfung prallt jeder weitere Versuch an den schon geschriebenen Zeilen
        // ab, und der Compliance-Modus versucht es endlos.
        await new AuditBatchWriter(sink, factory, options).RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20), ct);

        await using var db = factory.CreateDbContext();
        var rows = await db.AuditEvents.OrderBy(r => r.ChainIndex).ToListAsync(ct);
        rows.Select(r => (r.ChainIndex, r.Tool)).Should().Equal(
            [(0L, "eins"), (1L, "zwei")], "der geglückte erste Versuch wird nicht wiederholt");
        factory.Attempts.Should().BeGreaterThan(1, "der Fehler nach dem Festschreiben hat einen zweiten Versuch ausgelöst");
        sink.UnwrittenCount.Should().Be(0);
    }

    private static AuditEvent NewEvent(string tool) => new(
        DateTimeOffset.UtcNow, IdentityId.New(), CallOrigin.Mcp, AuditEventKind.ToolCall,
        ServerId.New(), tool, InvocationStatus.Success, null, 1, 2, TimeSpan.FromMilliseconds(1));

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
        private readonly string? _dataSource;
        private readonly FailAfterCommit? _failAfterCommit;
        private int _attempts;

        public FailingDbContextFactory(
            int failuresBeforeSuccess = int.MaxValue, string? dataSource = null, bool failAfterFirstCommit = false)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
            _dataSource = dataSource;
            _failAfterCommit = failAfterFirstCommit ? new FailAfterCommit() : null;
        }

        public int Attempts => Volatile.Read(ref _attempts);

        public BifrostDbContext CreateDbContext()
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= _failuresBeforeSuccess)
            {
                throw new InvalidOperationException("SQLite Error 5: 'database is locked'.");
            }

            // Der gelungene Versuch: eine Datenbank im Arbeitsspeicher, die den Batch annimmt.
            var builder = new DbContextOptionsBuilder<BifrostDbContext>()
                .UseBifrostDatabase(
                    BifrostDbOptions.Sqlite,
                    $"Data Source={_dataSource ?? $"file:{Guid.NewGuid():N}?mode=memory&cache=shared"}");
            if (_failAfterCommit is not null)
            {
                builder.AddInterceptors(_failAfterCommit);
            }

            var db = new BifrostDbContext(builder.Options);
            db.Database.EnsureCreated();
            return db;
        }
    }

    /// <summary>
    /// Wirft einmal, NACHDEM gespeichert ist — wie eine Verbindung, die bei der Bestätigung abreißt.
    /// </summary>
    private sealed class FailAfterCommit : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private int _fired;

        public override ValueTask<int> SavedChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
            => Interlocked.Exchange(ref _fired, 1) == 0
                ? throw new InvalidOperationException("Verbindung nach dem Festschreiben abgerissen (Test).")
                : ValueTask.FromResult(result);
    }
}
