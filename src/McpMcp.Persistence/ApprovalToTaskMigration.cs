using System.Text.Json;
using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// Übernimmt bestehende Freigabe-Anfragen einmalig in das Task-Modell (ADR-0019, Entscheidung 1).
/// <para>
/// Eine Installation mit wartenden Freigaben darf durch das Update keine verlieren — deshalb wird
/// kopiert, nicht verworfen. Die alte Tabelle bleibt unangetastet stehen: Sie zu leeren wäre
/// unumkehrbar, und der Gewinn wäre ein paar Kilobyte.
/// </para>
/// <para>
/// Die Übernahme ist <b>idempotent</b>, weil die Freigabe-Id zur Task-Id wird: Was schon einen Task
/// mit dieser Id hat, wird übersprungen. Damit ist ein zweiter Start harmlos, und es braucht kein
/// zusätzliches „schon migriert"-Flag, das selbst wieder falsch stehen könnte.
/// </para>
/// </summary>
public sealed class ApprovalToTaskMigration
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;

    public ApprovalToTaskMigration(IDbContextFactory<McpMcpDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <summary>Kopiert noch nicht übernommene Freigaben und liefert deren Anzahl.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var approvals = await db.ApprovalRequests.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        if (approvals.Count == 0)
        {
            return 0;
        }

        var existing = await db.Tasks.Select(t => t.Id).ToListAsync(ct).ConfigureAwait(false);
        var known = existing.ToHashSet();

        var added = 0;
        foreach (var approval in approvals.Where(a => !known.Contains(a.Id)))
        {
            db.Tasks.Add(new TaskRow
            {
                Id = approval.Id,
                OwnerId = approval.CallerId,
                OwnerDescription = approval.CallerDescription,
                Tool = approval.Tool,
                ServerId = null,
                Origin = (int)CallOrigin.Mcp,
                // Alte Zeilen haben keine Correlation — die Task-Id selbst ist die ehrlichste
                // Ersatzangabe: Sie verbindet wenigstens die künftigen Zustandswechsel.
                CorrelationId = approval.Id,
                State = (int)MapState((ApprovalState)approval.State),
                Revision = 0,
                Progress = null,
                InputFingerprint = approval.Fingerprint,
                RedactedInputJson = approval.RedactedArgumentsJson,
                RedactedResultJson = null,
                FailureCode = (ApprovalState)approval.State is ApprovalState.Denied
                    ? TaskBackedApprovalStore.DeniedCode
                    : null,
                FailureMessage = (ApprovalState)approval.State is ApprovalState.Denied
                    ? "Die Freigabe wurde abgelehnt."
                    : null,
                ExpectedInputSchemaJson = null,
                Cancellation = (int)TaskCancellation.None,
                // Eine bereits verbrauchte Freigabe ist eingelöst — ohne diesen Zeitstempel wäre sie
                // nach der Übernahme wieder einlösbar, und aus einer verbrauchten Zustimmung würde
                // eine zweite.
                ClaimedAtTicks = (ApprovalState)approval.State is ApprovalState.Consumed
                    ? approval.RequestedAtTicks
                    : null,
                CreatedAtTicks = approval.RequestedAtTicks,
                UpdatedAtTicks = approval.RequestedAtTicks,
                ExpiresAtTicks = approval.ExpiresAtTicks,
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return added;
    }

    private static TaskState MapState(ApprovalState state) => state switch
    {
        ApprovalState.Pending => TaskState.InputRequired,
        ApprovalState.Approved => TaskState.Working,
        ApprovalState.Consumed => TaskState.Working,
        _ => TaskState.Failed,
    };

    /// <summary>Nur für Tests: prüft, ob die redigierten Argumente lesbar übernommen wurden.</summary>
    internal static JsonElement? Parse(string? json)
        => json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
