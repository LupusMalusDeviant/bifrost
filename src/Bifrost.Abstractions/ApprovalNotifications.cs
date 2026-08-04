namespace Bifrost.Abstractions;

/// <summary>
/// Ein Ziel, das über wartende Freigaben unterrichtet wird.
/// <para>
/// <b>Das Geheimnis gehört dem Ziel, nicht dem Gateway.</b> Es signiert die Nutzlast mit demselben
/// Verfahren, mit dem der Gateway eingehende Webhooks prüft (ADR-0013) — ein Empfänger kann also
/// mit derselben Beschreibung verifizieren, die er ohnehin hat.
/// </para>
/// </summary>
public sealed record ApprovalNotificationTarget(string Name, Uri Endpoint, string Secret, bool Enabled = true);

public enum ApprovalNotificationState
{
    /// <summary>Liegt in der Warteschlange oder wird gerade zugestellt.</summary>
    Pending = 0,

    Delivered = 1,

    /// <summary>Alle Versuche verbraucht. Der Vorgang bleibt sichtbar, statt still zu verschwinden.</summary>
    DeadLettered = 2,

    /// <summary>Die Freigabe war abgelaufen, bevor zugestellt werden konnte.</summary>
    Expired = 3,
}

/// <param name="LastError">
/// Der letzte Grund im Klartext — ohne ihn steht in einem toten Ziel nur „fehlgeschlagen", und die
/// Frage „warum" beginnt bei null.
/// </param>
public sealed record ApprovalNotificationStatus(
    Guid Id,
    string Target,
    Guid ApprovalId,
    ApprovalNotificationState State,
    int Attempts,
    DateTimeOffset QueuedAt,
    DateTimeOffset? LastTriedAt,
    string? LastError);

/// <summary>
/// Unterrichtet über wartende Freigaben (M6/WP6.3, FR-P074).
/// <para>
/// <b>Zustellung ist von Entscheidung getrennt.</b> Eine Freigabe gilt, wenn sie entschieden wurde
/// — nicht, wenn eine Benachrichtigung ankam. <see cref="Notify"/> gibt deshalb sofort zurück und
/// wirft nie: Ein ausgefallenes Ziel darf weder den Aufrufweg noch die Warteschlange blockieren.
/// </para>
/// </summary>
public interface IApprovalNotifier
{
    void Notify(ApprovalRequest request);

    /// <summary>
    /// Was aus den Benachrichtigungen geworden ist. Ein totes Ziel wird hier sichtbar, statt in
    /// einer Logzeile zu verschwinden, die niemand liest.
    /// </summary>
    IReadOnlyList<ApprovalNotificationStatus> Status { get; }
}
