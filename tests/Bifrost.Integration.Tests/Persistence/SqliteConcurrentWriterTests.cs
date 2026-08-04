using System.Collections.Concurrent;

using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Persistence;
using Bifrost.Persistence.Audit;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Bifrost.Integration.Tests.Persistence;

/// <summary>
/// Der zweite Schreiber ist die Instanz selbst.
/// <para>
/// Im Betrieb laufen ab dem Moment, in dem der Start fertig ist, drei Hintergrunddienste auf
/// derselben SQLite-Datei: der Audit-Flush (1 s), die Audit-Retention (sofort beim Start) und die
/// Fristprüfung der Vorgänge. Kommt eine Schreibung aus der Oberfläche dazu, treffen sich zwei
/// Schreiber auf einer Datei — und ohne WAL heißt das <c>SQLite Error 5: 'database is locked'</c>.
/// </para>
/// <para>
/// Die Nebenläufigkeit wird hier <b>erzwungen</b> und nicht erhofft: Alle 92 Schreiber melden sich
/// bereit und werden dann von <i>einem</i> Signal gemeinsam losgelassen; der Audit-Writer flusht
/// während derselben Zeitspanne alle 15 ms. Ein Lauf, der nur zufällig überlappt, wäre als Nachweis
/// wertlos.
/// </para>
/// <para>
/// <b>Belegt gegen den Ausgangszustand:</b> Mit <c>journal_mode=delete</c> und ohne
/// <c>busy_timeout</c> — dem Stand bis v0.6 — schlägt dieser Test binnen Sekunden mit
/// <c>SQLite Error 5</c> in <c>AffectedCountModificationCommandBatch.ConsumeAsync</c> fehl, also mit
/// genau dem Stapel aus dem CI-Fehlschlag.
/// </para>
/// </summary>
public sealed class SqliteConcurrentWriterTests : IAsyncLifetime
{
    private const int Rows = 6;
    private const int WritersPerRow = 15;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"bifrost-sqlite-concurrency-{Guid.NewGuid():N}");

    private string _dbPath = string.Empty;
    private IDbContextFactory<BifrostDbContext> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "bifrost.db");
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={_dbPath}")
            .Options);
        await new DatabaseInitializer(_factory).InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen ist kein Testergebnis.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_writers_and_a_running_audit_flush_never_lock_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var tasks = new TaskStore(_factory);

        var ids = new List<Guid>();
        for (var i = 0; i < Rows; i++)
        {
            var record = NewTask($"srv__job{i}");
            await tasks.CreateOrGetAsync(record, ct);
            ids.Add(record.Id);
        }

        // Der Audit-Flush läuft in kurzen Abständen und mit kleinen Batches: So entstehen viele
        // getrennte Schreibvorgänge statt eines einzigen am Ende.
        var sink = new ChannelAuditSink(capacity: 100_000);
        var options = new PersistenceOptions
        {
            AuditFlushInterval = TimeSpan.FromMilliseconds(15),
            AuditMaxBatchSize = 25,
        };
        var writer = new AuditBatchWriter(sink, _factory, options);
        using var writerStop = new CancellationTokenSource();
        var writerLoop = Task.Run(() => writer.RunAsync(writerStop.Token), CancellationToken.None);

        // Der Retention-Job schreibt beim Start sofort (ExecuteDeleteAsync) — im Betrieb ist er der
        // dritte Schreiber, hier ist er der, der die Datei am zuverlässigsten sperrt.
        var retention = new AuditRetentionJob(_factory, options);

        var failures = new ConcurrentBag<Exception>();
        var recorded = 0;

        // Das Startsignal: Jeder Schreiber meldet sich bereit und wartet ASYNCHRON auf denselben
        // Auslöser. Ein blockierendes Barrier täte es auch — es hielte aber 92 Threadpool-Threads
        // fest, und der Pool stellt neue nur im Sekundentakt nach. Der Test wartete dann auf die
        // Threadinjektion statt auf die Datenbank.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent((Rows * WritersPerRow) + 2);
        var workers = new List<Task>();

        foreach (var id in ids)
        {
            for (var w = 0; w < WritersPerRow; w++)
            {
                workers.Add(Task.Run(async () =>
                {
                    ready.Signal();
                    await start.Task;
                    try
                    {
                        // Bis die Fortschreibung wirklich angewendet ist: Eine verlorene Revision ist
                        // das erwartete Ergebnis von Nebenläufigkeit, eine gesperrte Datei nicht.
                        for (var attempt = 0; attempt < 50; attempt++)
                        {
                            var current = await tasks.GetAsync(id, ct);
                            if (current is null)
                            {
                                return;
                            }

                            var outcome = await tasks.UpdateAsync(
                                new TaskUpdate(id, Progress: attempt % 100), current.Revision, ct);
                            if (outcome is TaskUpdateOutcome.Applied or TaskUpdateOutcome.Terminal)
                            {
                                return;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }, ct));
            }
        }

        // Der Audit-Erzeuger: schreibt währenddessen ununterbrochen in den Channel.
        workers.Add(Task.Run(async () =>
        {
            ready.Signal();
            await start.Task;
            for (var i = 0; i < 2_000; i++)
            {
                sink.Record(NewAuditEvent());
                Interlocked.Increment(ref recorded);
                if (i % 50 == 0)
                {
                    await Task.Yield();
                }
            }
        }, ct));

        // Die Retention: ihr ExecuteDeleteAsync nimmt den Schreib-Lock der ganzen Tabelle.
        workers.Add(Task.Run(async () =>
        {
            ready.Signal();
            await start.Task;
            try
            {
                for (var i = 0; i < 25; i++)
                {
                    await retention.ExecuteOnceAsync(ct);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }, ct));

        ready.Wait(TimeSpan.FromSeconds(30), ct).Should().BeTrue("alle Schreiber stehen bereit");
        start.SetResult();
        await Task.WhenAll(workers);
        ready.Dispose();

        // Erst danach den Writer beenden — er drainiert den Rest des Channels.
        sink.Complete();
        await writerStop.CancelAsync();
        await writerLoop;

        failures.Should().BeEmpty(
            "kein Schreibvorgang darf an 'database is locked' scheitern, nur weil die Instanz "
            + "gleichzeitig ihr eigenes Audit schreibt");
        sink.DroppedCount.Should().Be(0, "der Channel war groß genug — hier wird nichts verdrängt");

        await using var db = await _factory.CreateDbContextAsync(ct);
        (await db.AuditEvents.CountAsync(ct)).Should().Be(
            Volatile.Read(ref recorded),
            "jedes aufgezeichnete Ereignis steht in der Datenbank — ein stiller Verlust wäre genau "
            + "der Bruch der Zusage 'lückenloses Audit'");
    }

    /// <summary>
    /// Die Bestandsdatei. <c>journal_mode</c> ist eine Eigenschaft der <b>Datei</b>: Eine
    /// Installation, die bisher ohne WAL lief, wird beim ersten Start dauerhaft umgestellt. Geprüft
    /// wird beides — dass die Umstellung wirklich stattfindet und dass sie nichts kostet.
    /// </summary>
    [Fact]
    public async Task Existing_database_without_wal_is_switched_over_without_losing_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_directory, "bestand.db");

        // 1. Eine Datenbank im alten Verfahren anlegen und füllen.
        IDbContextFactory<BifrostDbContext> legacy = new TestDbContextFactory(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}", SqliteJournalMode.Delete)
            .Options);
        await new DatabaseInitializer(legacy).InitializeAsync(ct);
        await using (var seed = await legacy.CreateDbContextAsync(ct))
        {
            seed.AuditEvents.AddRange(Enumerable.Range(0, 37).Select(_ => NewAuditRow()));
            await seed.SaveChangesAsync(ct);
        }

        SqliteConnection.ClearAllPools();
        ReadJournalMode(path).Should().Be("delete", "Ausgangszustand: das klassische Rollback-Journal");

        // 2. Dieselbe Datei mit der neuen Vorgabe öffnen.
        IDbContextFactory<BifrostDbContext> upgraded = new TestDbContextFactory(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}")
            .Options);
        await using (var db = await upgraded.CreateDbContextAsync(ct))
        {
            (await db.AuditEvents.CountAsync(ct)).Should().Be(
                37, "die Umstellung ist eine Verfahrensänderung, keine Datenmigration");
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
        }

        SqliteConnection.ClearAllPools();
        ReadJournalMode(path).Should().Be(
            "wal", "der erste Start mit der neuen Vorgabe stellt die Datei dauerhaft um");

        // 3. Und wieder zurück — der dokumentierte Rückweg muss auch wirklich zurückführen.
        IDbContextFactory<BifrostDbContext> reverted = new TestDbContextFactory(new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(BifrostDbOptions.Sqlite, $"Data Source={path}", SqliteJournalMode.Delete)
            .Options);
        await using (var db = await reverted.CreateDbContextAsync(ct))
        {
            (await db.AuditEvents.CountAsync(ct)).Should().Be(37);
        }

        SqliteConnection.ClearAllPools();
        ReadJournalMode(path).Should().Be("delete", "BIFROST_SQLITE_JOURNAL_MODE=delete ist der Rückweg");
    }

    /// <summary>Liest das Journalverfahren der Datei über eine eigene, unbeteiligte Verbindung.</summary>
    private static string ReadJournalMode(string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return (string)command.ExecuteScalar()!;
    }

    private static AuditEventRow NewAuditRow() => new()
    {
        Timestamp = DateTimeOffset.UtcNow,
        Origin = (int)CallOrigin.Mcp,
        Kind = (int)AuditEventKind.ToolCall,
        Tool = "srv__tool",
        Status = (int)InvocationStatus.Success,
    };

    private static TaskRecord NewTask(string tool)
    {
        var now = DateTimeOffset.UtcNow;
        return new TaskRecord(
            Guid.NewGuid(), IdentityId.New(), "agent", new NamespacedToolName(tool), null,
            CallOrigin.Rest, Guid.NewGuid(), TaskState.Working, 0, 0,
            Guid.NewGuid().ToString("N"), null, null, null, null,
            TaskCancellation.None, null, now, now, now.AddMinutes(30));
    }

    private static AuditEvent NewAuditEvent() => new(
        DateTimeOffset.UtcNow, IdentityId.New(), CallOrigin.Mcp, AuditEventKind.ToolCall,
        ServerId.New(), "srv__tool", InvocationStatus.Success, null, 1, 2, TimeSpan.FromMilliseconds(1));
}
