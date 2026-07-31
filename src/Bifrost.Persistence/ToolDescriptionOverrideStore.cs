using System.Collections.Concurrent;
using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Persistente Description-Overrides (FR-14) mit In-Memory-Cache, weil sie im Katalog-Aufbau
/// und damit im Hot Path gelesen werden. Write-Through: erst DB, dann Cache, dann Changed-Event.
/// </summary>
public sealed class ToolDescriptionOverrideStore : IToolDescriptionOverrides
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly ConcurrentDictionary<NamespacedToolName, string> _cache = new();

    public ToolDescriptionOverrideStore(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public event EventHandler? Changed;

    public IReadOnlyDictionary<NamespacedToolName, string> All => _cache;

    public string? GetOverride(NamespacedToolName tool) => _cache.GetValueOrDefault(tool);

    /// <summary>Lädt die Overrides beim Start in den Cache.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ToolDescriptionOverrides.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        _cache.Clear();
        foreach (var row in rows)
        {
            _cache[new NamespacedToolName(row.Tool)] = row.Description;
        }
    }

    public async Task SetAsync(NamespacedToolName tool, string? description, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(description))
        {
            await db.ToolDescriptionOverrides.Where(r => r.Tool == tool.Value)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            _cache.TryRemove(tool, out _);
        }
        else
        {
            var row = await db.ToolDescriptionOverrides.FindAsync([tool.Value], ct).ConfigureAwait(false);
            if (row is null)
            {
                db.ToolDescriptionOverrides.Add(new ToolDescriptionOverrideRow
                {
                    Tool = tool.Value,
                    Description = description.Trim(),
                });
            }
            else
            {
                row.Description = description.Trim();
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _cache[tool] = description.Trim();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
