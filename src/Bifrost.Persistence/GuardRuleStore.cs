using System.Collections.Concurrent;
using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Persistente Guard-Regeln (ADR-0011) mit In-Memory-Cache. Write-Through wie bei den
/// Redaction-Mustern: erst DB, dann Cache, dann <see cref="Changed"/> — worauf der
/// <c>SecretGuard</c> seine Regex neu baut. Die Konstruktion kostet ~100 ms für 50 Regeln und
/// gehört deshalb genau hierher und nicht in einen Request.
///
/// Beim ersten Start wird der kuratierte Regelsatz eingesät. Danach ist die DB maßgeblich:
/// Wer eine eingebaute Regel abschaltet, will das auch nach einem Neustart so haben.
/// </summary>
public sealed class GuardRuleStore : IGuardRuleStore
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly ConcurrentDictionary<string, GuardRule> _cache = new(StringComparer.Ordinal);

    public GuardRuleStore(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<GuardRule> All => [.. _cache.Values.OrderBy(r => r.Id, StringComparer.Ordinal)];

    /// <summary>Lädt die Regeln beim Start und sät beim allerersten Mal den Standardsatz.</summary>
    public async Task LoadAsync(IReadOnlyList<GuardRule> seedIfEmpty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seedIfEmpty);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await db.GuardRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0 && seedIfEmpty.Count > 0)
        {
            db.GuardRules.AddRange(seedIfEmpty.Select(ToRow));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            rows = await db.GuardRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        }

        _cache.Clear();
        foreach (var row in rows)
        {
            _cache[row.Id] = ToRule(row);
        }
    }

    public async Task UpsertAsync(GuardRule rule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var row = await db.GuardRules.FindAsync([rule.Id], ct).ConfigureAwait(false);
        if (row is null)
        {
            db.GuardRules.Add(ToRow(rule));
        }
        else
        {
            row.Description = rule.Description;
            row.Pattern = rule.Pattern;
            row.Keyword = rule.Keyword;
            row.Direction = (int)rule.Direction;
            row.Mode = (int)rule.Mode;
            row.Enabled = rule.Enabled;
            row.IsCustom = rule.IsCustom;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cache[rule.Id] = rule;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(string ruleId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        await db.GuardRules.Where(r => r.Id == ruleId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        _cache.TryRemove(ruleId, out _);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static GuardRuleRow ToRow(GuardRule r) => new()
    {
        Id = r.Id,
        Description = r.Description,
        Pattern = r.Pattern,
        Keyword = r.Keyword,
        Direction = (int)r.Direction,
        Mode = (int)r.Mode,
        Enabled = r.Enabled,
        IsCustom = r.IsCustom,
    };

    private static GuardRule ToRule(GuardRuleRow r) => new(
        r.Id, r.Description, r.Pattern, r.Keyword,
        (GuardDirection)r.Direction, (GuardMode)r.Mode, r.Enabled, r.IsCustom);
}
