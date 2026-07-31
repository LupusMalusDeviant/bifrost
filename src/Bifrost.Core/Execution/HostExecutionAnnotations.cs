namespace Bifrost.Core.Execution;

/// <summary>
/// „Dieser Weg fragt die Policy." Der Architekturtest prüft die Behauptung nach, indem er den IL-Code
/// der Methode liest — die Kennzeichnung allein genügt nicht.
/// <para>
/// <b>Wozu eine Kennzeichnung, wenn der Test ohnehin den Code liest?</b> Damit die Regel bei einem
/// <em>neuen</em> Weg rot wird. Der Test zählt nicht die heute bekannten Startwege auf, sondern
/// sucht alle Methoden, die eine <c>UpstreamServerConfig</c> annehmen. Eine neue davon trägt keine
/// der beiden Kennzeichnungen und fällt damit auf — anders als bei einer Liste im Test, die morgen
/// nichts mehr aussagt.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
public sealed class HostExecutionCheckedAttribute : Attribute
{
    /// <summary>Womit dieser Weg prüft, falls es nicht der direkte Aufruf des Torpostens ist.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// „Dieser Weg kann nichts starten." Für Methoden, die eine Konfiguration nur lesen, umformen,
/// redigieren oder vergleichen.
/// <para>
/// Die Begründung ist Pflicht und steht im Code, nicht im Test. Eine Ausnahme ohne Begründung ist
/// eine Lücke mit Erlaubnis; eine mit Begründung ist eine Aussage, die jemand widerlegen kann.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
public sealed class NoHostExecutionAttribute : Attribute
{
    public NoHostExecutionAttribute(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    public string Reason { get; }
}
