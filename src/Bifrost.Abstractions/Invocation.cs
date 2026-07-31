using System.Text.Json;

namespace Bifrost.Abstractions;

/// <summary>Herkunft eines Calls — alle drei Fassaden laufen durch denselben Invoker (ADR-0008).</summary>
public enum CallOrigin
{
    Mcp = 0,
    Rest = 1,
    Ui = 2,

    /// <summary>Kein Aufrufer — vom Gateway selbst ausgelöst (Systemereignisse, FR-22). Wert am Ende: die Zahlen liegen persistiert in der DB.</summary>
    System = 3,

    /// <summary>Von einem eingehenden, signierten Webhook ausgelöst (FR-20, ADR-0013). Wert am Ende — persistiert.</summary>
    Webhook = 4,
}

public enum InvocationStatus
{
    Success = 0,
    UpstreamError = 1,
    Denied = 2,
    Timeout = 3,
    ValidationFailed = 4,
    ToolNotFound = 5,

    /// <summary>
    /// Die Inhaltsprüfung hat das Ergebnis zurückgehalten (ADR-0011). Bewusst nicht
    /// <see cref="Denied"/>: Der Upstream-Call ist zu diesem Zeitpunkt bereits gelaufen, der
    /// Seiteneffekt also eingetreten. Wer im Audit später fragt, warum ein Issue doppelt
    /// existiert, muss genau diesen Unterschied sehen können. Wert am Ende — die Zahlen liegen
    /// persistiert in der DB.
    /// </summary>
    GuardBlocked = 6,

    /// <summary>
    /// Das Tool erfordert menschliche Freigabe (FR-32, ADR-0012). Der Call wurde nicht ausgeführt;
    /// eine Anfrage liegt in der Queue. Unterschieden von <see cref="Denied"/>: „darf nach
    /// Freigabe" statt „darf nie". Wert am Ende — persistiert.
    /// </summary>
    ApprovalRequired = 7,
}

public sealed record ToolInvocationRequest(
    IdentityId Caller,
    CallOrigin Origin,
    NamespacedToolName Tool,
    JsonElement Arguments,
    TimeSpan? TimeoutOverride);

public sealed record ToolInvocationResult(
    InvocationStatus Status,
    JsonElement? Content,
    string? ErrorMessage,
    TimeSpan Duration,
    /// <summary>
    /// Gesetzt, wenn das Ergebnis gekürzt wurde (FR-16). Truncation ist verlustbehaftet — ohne
    /// Kennzeichen hielte ein Agent das Bruchstück für die vollständige Antwort.
    /// </summary>
    ResultTruncation? Truncation = null,
    /// <summary>
    /// Der Vorgang, unter dem dieser Aufruf weiterläuft (ADR-0019) — heute bei
    /// <see cref="InvocationStatus.ApprovalRequired"/>. Vorher stand die Id nur im Meldungstext;
    /// ein Agent hätte sie aus deutscher Prosa herauslesen müssen, um den Stand abzufragen.
    /// </summary>
    Guid? TaskId = null);

/// <summary>
/// Nachweis einer Ergebnis-Kürzung (FR-16): Original- und Endgröße in Zeichen.
/// </summary>
public sealed record ResultTruncation(int OriginalChars, int TruncatedChars)
{
    /// <summary>Feldname, unter dem der Hinweis im gekürzten Ergebnis mitgeliefert wird.</summary>
    public const string MarkerProperty = "_bifrost_truncated";
}

/// <summary>
/// Grenzwerte der Ergebnis-Kompression (FR-16). <see cref="MaxResultChars"/> = 0 schaltet ab.
/// Bewusst in Zeichen statt Token: die Token-Schätzung des Katalogs rechnet ebenfalls in Zeichen,
/// und eine echte Tokenisierung im Hot Path wäre teurer als der Nutzen.
/// </summary>
public sealed record ResultCompressionOptions(int MaxResultChars = 0)
{
    /// <summary>Unterhalb dieser Grenze lohnt Kürzen nicht — der Hinweistext wäre größer als die Ersparnis.</summary>
    public const int MinimumUsefulLimit = 200;
}

/// <summary>
/// Der EINZIGE Weg zu einem Tool-Call (DO Nr. 1): AuthN ist vorgelagert, die Pipeline übernimmt
/// RBAC-Check → Argument-Validierung → Routing → Timeout/Cancellation → Upstream-Call → Audit.
/// Wirft nicht bei fachlichen Fehlern — jedes Ergebnis ist ein <see cref="ToolInvocationResult"/> (DO Nr. 9).
/// </summary>
public interface IToolInvoker
{
    Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct);
}
