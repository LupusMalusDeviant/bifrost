using Bifrost.Abstractions;

namespace Bifrost.Core.Capabilities;

/// <summary>
/// Bildet das Ergebnis des Invocation-Kerns auf <see cref="CapabilityResultV1"/> ab (ADR-0015).
/// <para>
/// Zwei Dinge gewinnt der Aufrufer dadurch, und beide waren vorher nur als deutscher Text da:
/// </para>
/// <list type="number">
/// <item>
/// Einen <b>stabilen Gateway-Code</b> je Fehlerlage plus die Aussage, ob ein Wiederholen Aussicht
/// hat. Vorher stand im Ergebnis eine Meldung, die ein Automat hätte parsen müssen — und die sich
/// mit jeder Textpflege ändert.
/// </item>
/// <item>
/// Bei einem freigabepflichtigen Aufruf die <b>Vorgangs-Id</b> als eigene Art
/// (<see cref="CapabilityResultKind.Task"/>). Der Aufrufer holt den Stand unter
/// <c>/api/v1/tasks/{id}</c>, statt die Id aus dem Meldungstext zu lesen. Hier treffen sich
/// ADR-0015 und ADR-0019.
/// </item>
/// </list>
/// <para>
/// Bewusst <b>keine</b> Umdeutung des Erfolgsfalls: Ein gelungener Aufruf liefert weiter genau die
/// Nutzlast des Upstreams, nur in der Hülle. Sie zu verändern wäre eine Vertragsänderung an den
/// bestehenden Fassaden, und die ADR sieht den Übergang additiv vor.
/// </para>
/// </summary>
public static class CapabilityResultMapper
{
    /// <summary>
    /// Stabile Gateway-Codes je Status. Sie sind Teil des öffentlichen Vertrags und dürfen sich
    /// nicht mit einer Textänderung verschieben — deshalb hier an einer Stelle und nicht verstreut.
    /// </summary>
    public static string GatewayCodeFor(InvocationStatus status) => status switch
    {
        InvocationStatus.Success => "ok",
        InvocationStatus.Denied => "denied",
        InvocationStatus.ValidationFailed => "invalid-arguments",
        InvocationStatus.ToolNotFound => "not-found",
        InvocationStatus.Timeout => "timeout",
        InvocationStatus.UpstreamError => "upstream-error",
        InvocationStatus.GuardBlocked => "guard-blocked",
        InvocationStatus.ApprovalRequired => "approval-required",
        _ => "unknown",
    };

    /// <summary>
    /// Ob ein Wiederholen desselben Aufrufs Aussicht hat.
    /// <para>
    /// <see cref="InvocationStatus.GuardBlocked"/> ist ausdrücklich <b>nicht</b> wiederholbar: Der
    /// Upstream-Call ist zu diesem Zeitpunkt schon gelaufen, der Seiteneffekt also eingetreten. Ein
    /// Retry legte dasselbe Issue ein zweites Mal an — genau die Verwechslung, vor der die
    /// Fehlermeldung heute in Prosa warnt.
    /// </para>
    /// <para>
    /// <see cref="InvocationStatus.ApprovalRequired"/> ebenfalls nicht: Sofort zu wiederholen bringt
    /// nichts, weil ein Mensch entscheiden muss. Der Weg ist der Vorgang, nicht der Retry.
    /// </para>
    /// </summary>
    public static bool IsRetryable(InvocationStatus status) => status switch
    {
        InvocationStatus.Timeout or InvocationStatus.UpstreamError => true,
        _ => false,
    };

    /// <summary>Projiziert ein Invocation-Ergebnis in die Capability-Hülle.</summary>
    public static CapabilityResultV1 From(ToolInvocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Ein Vorgang statt eines Ergebnisses: Der Aufruf läuft weiter, nur nicht jetzt.
        if (result.TaskId is { } taskId)
        {
            return CapabilityResultV1.Accepted(taskId);
        }

        if (result.Status is not InvocationStatus.Success)
        {
            return CapabilityResultV1.Failed(new CapabilityError(
                GatewayCodeFor(result.Status),
                // Es gibt heute keinen durchgereichten Upstream-Code — die Connectoren liefern
                // Text. `null` ist hier die Wahrheit; ein erfundener Code wäre schlimmer.
                ConnectorCode: null,
                result.ErrorMessage ?? result.Status.ToString(),
                IsRetryable(result.Status)));
        }

        return result.Content is { } content
            ? CapabilityResultV1.Structured(content, result.Truncation)
            // Erfolg ohne Nutzlast kommt vor (ein Kommando, das nichts zurückgibt). Leerer Text ist
            // dafür ehrlicher als ein erfundenes leeres Objekt.
            : CapabilityResultV1.FromText(string.Empty, result.Truncation);
    }
}
