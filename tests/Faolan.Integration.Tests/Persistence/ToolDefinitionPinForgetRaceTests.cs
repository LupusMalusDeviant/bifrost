using System.Data.Common;

using AwesomeAssertions;

using Faolan.Abstractions;
using Faolan.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Xunit;

namespace Faolan.Integration.Tests.Persistence;

/// <summary>
/// H5: <see cref="ToolDefinitionPinStore.ForgetServerAsync"/> darf keinen Pin im Cache
/// zurücklassen, den ein gleichzeitig laufender Schreibvorgang nach dem Löschen nachträgt.
/// <para>
/// Die Verschränkung wird <b>erzwungen</b>, nicht erhofft: Ein Interceptor hält den
/// Schreibvorgang genau zwischen Datenbankschritt und Cache-Veröffentlichung an, dann läuft das
/// Vergessen, dann wird losgelassen. Ohne Schutz trägt der angehaltene Vorgang die alte Zeile in
/// den Cache zurück — ein Phantom-Pin in <see cref="ToolDefinitionPinStore.All"/> bis zum
/// Neustart, während die Datenbank keine Zeile mehr hat.
/// </para>
/// </summary>
public sealed class ToolDefinitionPinForgetRaceTests : IAsyncLifetime
{
    private static readonly TimeSpan ForgetGrace = TimeSpan.FromSeconds(2);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"bifrost-pin-race-{Guid.NewGuid():N}");

    private readonly PinGate _gate = new();
    private IDbContextFactory<FaolanDbContext> _factory = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var dbPath = Path.Combine(_directory, "bifrost.db");
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<FaolanDbContext>()
            .UseFaolanDatabase(FaolanDbOptions.Sqlite, $"Data Source={dbPath}")
            .AddInterceptors(_gate.Reader, _gate.Save)
            .Options);
        await new DatabaseInitializer(_factory).InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _gate.Release();
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
    public async Task Forget_during_accept_leaves_no_phantom_pin_in_the_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = ServerId.New();
        var store = new ToolDefinitionPinStore(_factory, TimeProvider.System);
        (await store.VerifyAsync(server, "read_file", new string('a', 64), ct))
            .Should().Be(ToolDefinitionVerdict.FirstSeen);
        (await store.VerifyAsync(server, "read_file", new string('b', 64), ct))
            .Should().Be(ToolDefinitionVerdict.Changed);

        // Angehalten wird das Nachlesen der Zeile — nach der Annahme, vor dem Cache.
        _gate.ArmSelect();
        var accept = store.AcceptAsync(server, "read_file", new string('b', 64), ct);
        await _gate.Reached.WaitAsync(ct);

        await ForgetThenReleaseAsync(store, server, ct);
        await accept;

        store.All.Should().NotContain(p => p.Server == server,
            "der Upstream ist vergessen; ein Pin im Cache wäre ein Phantom bis zum Neustart (H5)");
        await AssertDatabaseEmptyAsync(server, ct);
    }

    [Fact]
    public async Task Forget_during_first_sighting_leaves_no_phantom_pin_in_the_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = ServerId.New();
        var store = new ToolDefinitionPinStore(_factory, TimeProvider.System);

        // Angehalten wird nach dem Einfügen der Erstsichtung, vor dem Cache.
        _gate.ArmSave();
        var verify = store.VerifyAsync(server, "read_file", new string('a', 64), ct);
        await _gate.Reached.WaitAsync(ct);

        await ForgetThenReleaseAsync(store, server, ct);
        await verify;

        store.All.Should().NotContain(p => p.Server == server,
            "der Upstream ist vergessen; ein Pin im Cache wäre ein Phantom bis zum Neustart (H5)");
        await AssertDatabaseEmptyAsync(server, ct);
    }

    /// <summary>
    /// Plan 0005 I1: Die Bedingung eines späten Aufräumens wird erst unter der Sperre des Upstreams
    /// gefragt. Wartet das Vergessen auf eine laufende Erstsichtung und gehört die Id inzwischen einem
    /// Nachfolger, bleibt dessen Pin stehen — in der Datenbank und im Cache.
    /// </summary>
    [Fact]
    public async Task A_conditional_forget_asks_its_condition_under_the_lock()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = ServerId.New();
        var store = new ToolDefinitionPinStore(_factory, TimeProvider.System);

        _gate.ArmSave();
        var verify = store.VerifyAsync(server, "read_file", new string('a', 64), ct);
        await _gate.Reached.WaitAsync(ct);

        var retired = true;
        var forget = store.ForgetServerAsync(server, () => Volatile.Read(ref retired), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        forget.IsCompleted.Should().BeFalse("das Vergessen wartet auf den laufenden Datenbankschritt");

        // Die Id ist jetzt neu eingetragen; der wartende Vorgang räumt nichts mehr.
        Volatile.Write(ref retired, false);
        _gate.Release();
        await verify;
        await forget;

        store.All.Should().ContainSingle(p => p.Server == server);
        await using var db = await _factory.CreateDbContextAsync(ct);
        (await db.ToolDefinitionPins.CountAsync(r => r.ServerId == server.Value, ct)).Should().Be(1);
    }

    /// <summary>
    /// Startet das Vergessen und lässt den angehaltenen Vorgang los, sobald es fertig ist — oder
    /// nach einer Frist, falls es (wie mit Schutz) auf den angehaltenen Vorgang warten muss. Die
    /// Frist entscheidet nur die Reihenfolge, nicht das Ergebnis: Mit Schutz ist der Ausgang in
    /// beiden Reihenfolgen derselbe.
    /// </summary>
    private async Task ForgetThenReleaseAsync(ToolDefinitionPinStore store, ServerId server, CancellationToken ct)
    {
        var forget = store.ForgetServerAsync(server, ct);
        await Task.WhenAny(forget, Task.Delay(ForgetGrace, ct));
        _gate.Release();
        await forget;
    }

    private async Task AssertDatabaseEmptyAsync(ServerId server, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        (await db.ToolDefinitionPins.CountAsync(r => r.ServerId == server.Value, ct)).Should().Be(0);
    }

    /// <summary>Hält genau einen scharf geschalteten Datenbankschritt an, bis <see cref="Release"/>.</summary>
    private sealed class PinGate
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armedSelect;
        private int _armedSave;

        public PinGate()
        {
            Reader = new ReaderGate(this);
            Save = new SaveGate(this);
        }

        public IInterceptor Reader { get; }

        public IInterceptor Save { get; }

        public Task Reached => _reached.Task;

        public void ArmSelect() => Volatile.Write(ref _armedSelect, 1);

        public void ArmSave() => Volatile.Write(ref _armedSave, 1);

        public void Release() => _release.TrySetResult();

        private async Task HoldAsync(CancellationToken ct)
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }

        private sealed class ReaderGate(PinGate gate) : DbCommandInterceptor
        {
            // Angehalten wird beim Schliessen des Readers: Die Zeile ist dann gelesen, aber noch
            // nicht im Cache. Vor dem Ausführen anzuhalten träfe das Fenster nicht — das Nachlesen
            // fände die Zeile dann schon gelöscht.
            public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
                DbCommand command,
                DataReaderClosingEventData eventData,
                InterceptionResult result)
            {
                if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    && command.CommandText.Contains("ToolDefinitionPins", StringComparison.Ordinal)
                    && Interlocked.Exchange(ref gate._armedSelect, 0) == 1)
                {
                    await gate.HoldAsync(CancellationToken.None);
                }

                return result;
            }
        }

        private sealed class SaveGate(PinGate gate) : SaveChangesInterceptor
        {
            public override async ValueTask<int> SavedChangesAsync(
                SaveChangesCompletedEventData eventData,
                int result,
                CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref gate._armedSave, 0) == 1)
                {
                    await gate.HoldAsync(cancellationToken);
                }

                return result;
            }
        }
    }
}
