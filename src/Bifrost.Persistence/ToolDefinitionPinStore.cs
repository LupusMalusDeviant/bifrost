using System.Collections.Concurrent;
using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Persistierte Tool-Definitions-Pins (Rug-Pull-Erkennung).
/// <para>
/// Write-through wie die übrigen Stores: erst Datenbank, dann Cache. Gelesen wird bei jeder
/// Discovery — deshalb der Cache; geschrieben nur bei Erstsichtung, Abweichung und Annahme.
/// </para>
/// </summary>
public sealed class ToolDefinitionPinStore : IToolDefinitionPinStore
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(Guid Server, string Tool), ToolDefinitionPin> _cache = new();

    // H5: Je Upstream eine Sperre über Datenbankschritt UND Cache-Veröffentlichung. Ohne sie konnte
    // ein Schreibvorgang seine Zeile lesen, ForgetServerAsync Zeilen und Cache leeren und der
    // Schreibvorgang die alte Zeile danach in den Cache zurücktragen — ein Phantom-Pin in All bis
    // zum Neustart. Die Sperren bleiben stehen, auch wenn der Upstream vergessen ist: Entfernte man
    // sie, könnten ein Wartender auf der alten und ein Neuer auf einer frischen Sperre zugleich laufen.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _serverLocks = new();

    public ToolDefinitionPinStore(IDbContextFactory<BifrostDbContext> factory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(time);
        _factory = factory;
        _time = time;
    }

    public event EventHandler<ToolDefinitionPinChangedEventArgs>? Changed;

    public IReadOnlyList<ToolDefinitionPin> All =>
        [.. _cache.Values
            .OrderBy(p => p.Server.Value)
            .ThenBy(p => p.Tool, StringComparer.Ordinal)];

    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ToolDefinitionPins.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        _cache.Clear();
        foreach (var row in rows)
        {
            _cache[(row.ServerId, row.Tool)] = ToPin(row);
        }
    }

    public async Task<ToolDefinitionVerdict> VerifyAsync(
        ServerId server, string tool, string hash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        var gate = LockFor(server);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await VerifyLockedAsync(server, tool, hash, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ToolDefinitionVerdict> VerifyLockedAsync(
        ServerId server, string tool, string hash, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ToolDefinitionPins
            .FirstOrDefaultAsync(r => r.ServerId == server.Value && r.Tool == tool, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            // Trust-on-first-use: Es gibt keinen früheren Stand, gegen den zu prüfen wäre. Was hier
            // übernommen wird, ist der Bezugspunkt für alles Spätere — nicht mehr und nicht weniger.
            row = new ToolDefinitionPinRow
            {
                ServerId = server.Value,
                Tool = tool,
                AcceptedHash = hash,
                AcceptedAtTicks = _time.GetUtcNow().UtcTicks,
            };
            db.ToolDefinitionPins.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _cache[(server.Value, tool)] = ToPin(row);
            return ToolDefinitionVerdict.FirstSeen;
        }

        if (string.Equals(row.AcceptedHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            // Zurück zum angenommenen Stand: Eine zwischenzeitliche Abweichung ist damit erledigt
            // und darf nicht als offener Vorgang stehen bleiben.
            if (row.PendingHash is not null)
            {
                row.PendingHash = null;
                row.PendingSinceTicks = null;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                _cache[(server.Value, tool)] = ToPin(row);
            }

            return ToolDefinitionVerdict.Unchanged;
        }

        // Abweichung. Der anstehende Hash wird fortgeschrieben, damit die Anzeige den aktuellen
        // Vorschlag zeigt und nicht den ersten aus einer Reihe von Änderungen.
        if (!string.Equals(row.PendingHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            row.PendingHash = hash;
            row.PendingSinceTicks = _time.GetUtcNow().UtcTicks;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _cache[(server.Value, tool)] = ToPin(row);
        }

        return ToolDefinitionVerdict.Changed;
    }

    public async Task<bool> AcceptAsync(ServerId server, string tool, string expectedPendingHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPendingHash);

        bool accepted;
        var gate = LockFor(server);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            accepted = await AcceptLockedAsync(server, tool, expectedPendingHash, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // Benachrichtigt wird ausserhalb der Sperre: Ein Abonnent, der den Store wieder anfasst,
        // darf sich nicht an ihr festlaufen.
        if (accepted)
        {
            Changed?.Invoke(this, new ToolDefinitionPinChangedEventArgs(server));
        }

        return accepted;
    }

    private async Task<bool> AcceptLockedAsync(
        ServerId server, string tool, string expectedPendingHash, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Ein Schritt in der Datenbank (E7): Steht inzwischen eine andere Fassung an, trifft die
        // Bedingung keine Zeile, und angenommen wird nichts. Hashes sind kleingeschriebenes Hex.
        var expected = expectedPendingHash.ToLowerInvariant();
        var now = _time.GetUtcNow().UtcTicks;
        var changed = await db.ToolDefinitionPins
            .Where(r => r.ServerId == server.Value && r.Tool == tool && r.PendingHash != null
                && r.PendingHash == expected)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.AcceptedHash, r => r.PendingHash!)
                .SetProperty(r => r.AcceptedAtTicks, now)
                .SetProperty(r => r.PendingHash, (string?)null)
                .SetProperty(r => r.PendingSinceTicks, (long?)null), ct)
            .ConfigureAwait(false);

        var row = await db.ToolDefinitionPins.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ServerId == server.Value && r.Tool == tool, ct)
            .ConfigureAwait(false);
        if (row is not null)
        {
            _cache[(server.Value, tool)] = ToPin(row);
        }

        return changed == 1;
    }

    public Task ForgetServerAsync(ServerId server, CancellationToken ct)
        => ForgetServerAsync(server, static () => true, ct);

    public async Task ForgetServerAsync(ServerId server, Func<bool> stillRetired, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stillRetired);

        // H5: Unter derselben Sperre wie die Schreibvorgänge. Ein laufender Vorgang veröffentlicht
        // vollständig, bevor hier gelöscht wird; einer, der danach kommt, liest die Datenbank neu.
        // Das Entfernen wartet damit höchstens auf einen laufenden Datenbankschritt, nicht auf eine
        // Katalogabfrage (N51).
        var gate = LockFor(server);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // I1: Erst unter der Sperre gefragt — ist die Id inzwischen neu eingetragen, gehören die
            // Pins dem Nachfolger, auch wenn sie schon vor ihm geschrieben wurden.
            if (!stillRetired())
            {
                return;
            }

            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.ToolDefinitionPins.Where(r => r.ServerId == server.Value)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            foreach (var key in _cache.Keys.Where(k => k.Server == server.Value).ToList())
            {
                _cache.TryRemove(key, out _);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim LockFor(ServerId server) =>
        _serverLocks.GetOrAdd(server.Value, static _ => new SemaphoreSlim(1, 1));

    private static ToolDefinitionPin ToPin(ToolDefinitionPinRow row) => new(
        new ServerId(row.ServerId),
        row.Tool,
        row.AcceptedHash,
        new DateTimeOffset(row.AcceptedAtTicks, TimeSpan.Zero),
        row.PendingHash,
        row.PendingSinceTicks is { } since ? new DateTimeOffset(since, TimeSpan.Zero) : null);
}
