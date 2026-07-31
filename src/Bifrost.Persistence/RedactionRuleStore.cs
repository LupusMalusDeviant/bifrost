using System.Collections.Concurrent;
using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Persistente Redaction-Regeln pro Tool (FR-24) mit In-Memory-Cache — sie werden bei jedem
/// auditierten Call gelesen und dürfen den Hot Path nicht in die DB schicken. Write-Through:
/// erst DB, dann Cache.
/// </summary>
public sealed class RedactionRuleStore : IRedactionRules
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly ConcurrentDictionary<NamespacedToolName, IReadOnlyList<string>> _cache = new();

    public RedactionRuleStore(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public IReadOnlyDictionary<NamespacedToolName, IReadOnlyList<string>> All => _cache;

    public IReadOnlyList<string>? GetPatterns(NamespacedToolName tool) => _cache.GetValueOrDefault(tool);

    /// <summary>Lädt die Regeln beim Start in den Cache.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.RedactionRules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        _cache.Clear();
        foreach (var row in rows)
        {
            var patterns = Split(row.Patterns);
            if (patterns.Count > 0)
            {
                _cache[new NamespacedToolName(row.Tool)] = patterns;
            }
        }
    }

    public async Task SetAsync(NamespacedToolName tool, IReadOnlyList<string>? patterns, CancellationToken ct)
    {
        var cleaned = (patterns ?? [])
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (cleaned.Count == 0)
        {
            await db.RedactionRules.Where(r => r.Tool == tool.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            _cache.TryRemove(tool, out _);
            return;
        }

        var joined = string.Join(',', cleaned);
        var row = await db.RedactionRules.FindAsync([tool.Value], ct).ConfigureAwait(false);
        if (row is null)
        {
            db.RedactionRules.Add(new RedactionRuleRow { Tool = tool.Value, Patterns = joined });
        }
        else
        {
            row.Patterns = joined;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cache[tool] = cleaned;
    }

    private static List<string> Split(string patterns)
        => [.. patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
