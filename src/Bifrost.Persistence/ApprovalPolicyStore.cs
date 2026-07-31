using System.Collections.Concurrent;
using Bifrost.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Bifrost.Persistence;

/// <summary>
/// Welche Tools scharf sind und wie das durchgesetzt wird (FR-32, ADR-0012, ADR-0022). In-Memory-
/// Cache, weil bei jedem Call gelesen; Write-Through wie bei Guard-Regeln und
/// Description-Overrides — hot-swappable ohne Neustart.
/// </summary>
public sealed class ApprovalPolicyStore : IApprovalPolicy
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;

    /// <summary>
    /// Markierte Tools samt Weg. Der Wert ist der Durchsetzungsweg, nicht mehr nur ein Anwesenheits-
    /// Byte: „ist scharf" und „wartet in der Queue" sind seit ADR-0022 zwei verschiedene Aussagen.
    /// </summary>
    private readonly ConcurrentDictionary<NamespacedToolName, ApprovalEnforcement> _marked = new();

    /// <summary>
    /// Weg für alles Scharfe ohne eigene Festlegung. Startwert ist der strengere — eine Instanz,
    /// die diese Einstellung nie gesehen hat, ist nicht schwächer als vorher.
    /// </summary>
    private volatile ApprovalEnforcement _default = ApprovalEnforcement.Queue;

    public ApprovalPolicyStore(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public event EventHandler? Changed;

    public bool RequiresApproval(NamespacedToolName tool)
        => _marked.TryGetValue(tool, out var mode) && mode is ApprovalEnforcement.Queue;

    public bool IsSensitive(NamespacedToolName tool) => _marked.ContainsKey(tool);

    public ApprovalEnforcement? EnforcementFor(NamespacedToolName tool)
        => _marked.TryGetValue(tool, out var mode) ? mode : null;

    public ApprovalEnforcement DefaultEnforcement => _default;

    public ApprovalEnforcement? EffectiveFor(NamespacedToolName tool, bool declaredByCatalog)
    {
        if (_marked.TryGetValue(tool, out var explicitMode))
        {
            return explicitMode;
        }

        // Selbstauskunft eines Connector-Pakets: scharf, aber ohne Zeile, an der ein Mensch einen
        // Weg haette hinterlegen koennen. Bis ADR-0022 landete so ein Werkzeug deshalb immer in der
        // Warteschlange — es gab schlicht keinen anderen Weg dorthin.
        return declaredByCatalog ? _default : null;
    }

    public IReadOnlyCollection<NamespacedToolName> All => [.. _marked.Keys];

    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ApprovalTools.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        _marked.Clear();
        foreach (var row in rows)
        {
            _marked[new NamespacedToolName(row.Tool)] = Parse(row.Mode);
        }

        var settings = await db.ApprovalPolicySettings.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == ApprovalPolicySettingsRow.SingletonId, ct)
            .ConfigureAwait(false);
        _default = Parse(settings?.DefaultMode);
    }

    public async Task SetDefaultEnforcementAsync(ApprovalEnforcement enforcement, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ApprovalPolicySettings
            .FindAsync([ApprovalPolicySettingsRow.SingletonId], ct).ConfigureAwait(false);
        if (row is null)
        {
            db.ApprovalPolicySettings.Add(new ApprovalPolicySettingsRow
            {
                Id = ApprovalPolicySettingsRow.SingletonId,
                DefaultMode = enforcement.ToString(),
            });
        }
        else
        {
            row.DefaultMode = enforcement.ToString();
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _default = enforcement;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task SetAsync(NamespacedToolName tool, bool required, CancellationToken ct)
        => SetAsync(tool, required ? ApprovalEnforcement.Queue : null, ct);

    public async Task SetAsync(NamespacedToolName tool, ApprovalEnforcement? enforcement, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (enforcement is { } mode)
        {
            var existing = await db.ApprovalTools.FindAsync([tool.Value], ct).ConfigureAwait(false);
            if (existing is null)
            {
                db.ApprovalTools.Add(new ApprovalToolRow { Tool = tool.Value, Mode = mode.ToString() });
            }
            else
            {
                existing.Mode = mode.ToString();
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _marked[tool] = mode;
        }
        else
        {
            await db.ApprovalTools.Where(r => r.Tool == tool.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            _marked.TryRemove(tool, out _);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Unbekanntes fällt auf den strengeren Weg zurück, nicht auf den schwächeren. Ein Tippfehler
    /// in der Spalte — oder eine Zeile aus einer neueren Version — darf ein scharfes Werkzeug
    /// nicht stillschweigend freigeben.
    /// </summary>
    private static ApprovalEnforcement Parse(string? mode)
        => Enum.TryParse<ApprovalEnforcement>(mode, ignoreCase: true, out var parsed)
            ? parsed
            : ApprovalEnforcement.Queue;
}
