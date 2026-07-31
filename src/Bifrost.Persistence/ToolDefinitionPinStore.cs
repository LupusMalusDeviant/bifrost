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

    public async Task AcceptAsync(ServerId server, string tool, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ToolDefinitionPins
            .FirstOrDefaultAsync(r => r.ServerId == server.Value && r.Tool == tool, ct)
            .ConfigureAwait(false);
        if (row?.PendingHash is not { Length: > 0 } pending)
        {
            return;
        }

        row.AcceptedHash = pending;
        row.AcceptedAtTicks = _time.GetUtcNow().UtcTicks;
        row.PendingHash = null;
        row.PendingSinceTicks = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cache[(server.Value, tool)] = ToPin(row);

        Changed?.Invoke(this, new ToolDefinitionPinChangedEventArgs(server));
    }

    public async Task ForgetServerAsync(ServerId server, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ToolDefinitionPins.Where(r => r.ServerId == server.Value)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var key in _cache.Keys.Where(k => k.Server == server.Value).ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }

    private static ToolDefinitionPin ToPin(ToolDefinitionPinRow row) => new(
        new ServerId(row.ServerId),
        row.Tool,
        row.AcceptedHash,
        new DateTimeOffset(row.AcceptedAtTicks, TimeSpan.Zero),
        row.PendingHash,
        row.PendingSinceTicks is { } since ? new DateTimeOffset(since, TimeSpan.Zero) : null);
}
