using Bifrost.Abstractions;

namespace Bifrost.Core.Approvals;

/// <summary>
/// Legt eine Freigabe an und unterrichtet danach die Ziele — <b>in dieser Reihenfolge und ohne
/// Rückwirkung</b>.
/// <para>
/// <b>Warum ein Mantel und keine Änderung am Speicher:</b> Die Warteschlange ist der Vertrag
/// (ADR-0012/ADR-0019). Eine Benachrichtigung ist eine Nebenwirkung, die niemand braucht, damit
/// eine Freigabe gilt. Wer sie in den Speicher schriebe, hätte zwei Aufgaben in einer Klasse — und
/// der Tag, an dem ein Zustellfehler eine Freigabe verhindert, wäre nur eine Frage der Zeit.
/// </para>
/// <para>
/// Alle übrigen Wege gehen unverändert durch. Insbesondere <see cref="DecideAsync"/>: Die
/// Entscheidung selbst löst hier nichts aus — sie ist der Zweck, nicht der Anlass.
/// </para>
/// </summary>
public sealed class NotifyingApprovalStore(IApprovalStore inner, IApprovalNotifier notifier) : IApprovalStore
{
    public async Task<Guid> EnqueueAsync(ApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = await inner.EnqueueAsync(request, ct);

        // Nach dem Anlegen und mit der Id, die der Speicher vergeben hat: Ein Deep-Link auf eine
        // Anfrage, die es noch nicht gibt, fuehrt ins Leere.
        //
        // Notify wirft nicht und wartet nicht (siehe IApprovalNotifier). Trotzdem steht hier ein
        // Fangnetz: Diese Zeile darf unter keinen Umstaenden zwischen den Aufrufer und seine
        // angelegte Freigabe geraten.
        try
        {
            notifier.Notify(request with { Id = id });
        }
        catch (Exception)
        {
            // Absichtlich verschluckt — die Freigabe steht bereits.
        }

        return id;
    }

    public Task<Guid?> TryConsumeApprovalAsync(
        IdentityId caller, NamespacedToolName tool, string argumentFingerprint, CancellationToken ct)
        => inner.TryConsumeApprovalAsync(caller, tool, argumentFingerprint, ct);

    public Task CompleteAsync(Guid taskId, TaskFailure? failure, CancellationToken ct)
        => inner.CompleteAsync(taskId, failure, ct);

    public Task<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalState? state, CancellationToken ct)
        => inner.ListAsync(state, ct);

    public Task DecideAsync(Guid requestId, bool approved, CancellationToken ct)
        => inner.DecideAsync(requestId, approved, ct);
}
