using System.Globalization;

using Bifrost.Abstractions.Execution;

namespace Bifrost.Core.Execution;

/// <summary>Woher die Antwort auf „darf nativ ausgeführt werden?" stammt.</summary>
public enum HostExecutionOrigin
{
    /// <summary>
    /// Noch nicht ermittelt. Kein Zustand, in dem etwas startet — er existiert, damit „noch nicht
    /// gefragt" nicht wie „erlaubt" aussieht.
    /// </summary>
    Unresolved = 0,

    /// <summary>Ausdrücklich gesetzt über <see cref="HostExecutionSwitch.Name"/>.</summary>
    Environment = 1,

    /// <summary>Aus dem geschriebenen Wert der Instanz gelesen.</summary>
    Persisted = 2,

    /// <summary>Beim Start von einer bestehenden Instanz übernommen (ADR-0025 E3).</summary>
    AdoptedFromExistingInstance = 3,

    /// <summary>Die Vorgabe einer frischen Instanz: verboten (ADR-0025 E2).</summary>
    FreshInstanceDefault = 4,

    /// <summary>Die Einstellung war vorhanden, aber nicht lesbar. Fail-closed.</summary>
    Unreadable = 5,
}

/// <summary>
/// Der ermittelte Zustand der Ausführungs-Policy — das, was ein Betreiber im Diagnosebericht sieht
/// und worauf ein Runbook zeigt.
/// </summary>
/// <param name="Allowed">Darf nativ ausgeführt werden?</param>
/// <param name="Origin">Woher diese Antwort kommt.</param>
/// <param name="ReasonCode">
/// Der stabile Code, den eine Entscheidung über einen nativen Upstream in diesem Zustand trägt.
/// </param>
/// <param name="HostUpstreams">
/// Die nativ laufenden Upstreams, namentlich. Bei einer Übernahme sind das genau die, wegen derer
/// übernommen wurde.
/// </param>
/// <param name="Note">Ein Satz für Menschen, der den Zustand erklärt.</param>
public sealed record HostExecutionState(
    bool Allowed,
    HostExecutionOrigin Origin,
    string ReasonCode,
    IReadOnlyList<string> HostUpstreams,
    string Note)
{
    /// <summary>Wurde beim Start ein bestehender Zustand übernommen? Nur dann verlangt er eine Handlung.</summary>
    public bool Adopted => Origin is HostExecutionOrigin.AdoptedFromExistingInstance;

    /// <summary>
    /// Der Ausgangszustand: nicht ermittelt, also verboten. Er ist bewusst kein „noch nichts
    /// entschieden, deshalb erst mal durchlassen" — die Policy, die im Zweifel erlaubt, ist eine
    /// Dokumentation (ADR-0025 E1).
    /// </summary>
    public static HostExecutionState Unresolved { get; } = new(
        false,
        HostExecutionOrigin.Unresolved,
        HostExecutionReason.Undetermined,
        [],
        "Die Ausführungs-Policy wurde noch nicht ermittelt; native Ausführung bleibt solange verboten.");
}

/// <summary>Die globale Einstellung aus ADR-0025 E2.</summary>
public static class HostExecutionSwitch
{
    /// <summary>Der Name der Einstellung. Stabil — Runbooks und Container-Umgebungen zeigen darauf.</summary>
    public const string Name = "BIFROST_ALLOW_HOST_EXECUTION";

    /// <summary>
    /// Liest den Wert der Einstellung.
    /// <para>
    /// Drei Ausgänge, und der dritte ist der wichtige: nicht gesetzt, gesetzt, oder gesetzt mit
    /// einem Wert, den niemand deuten kann. Ein unverständliches <c>BIFROST_ALLOW_HOST_EXECUTION=ja
    /// bitte</c> wird nicht als „false" gelesen und auch nicht ignoriert — es ist ein Zustand
    /// „unbekannt", und der heißt nein (M3-Vertrag §6, Invariante 3).
    /// </para>
    /// </summary>
    public static HostExecutionSwitchValue Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return HostExecutionSwitchValue.NotSet;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => HostExecutionSwitchValue.True,
            "0" or "false" or "no" or "off" => HostExecutionSwitchValue.False,
            _ => HostExecutionSwitchValue.Invalid,
        };
    }

    /// <summary>Der Wert, den ein bewusst eingeschalteter Betrieb schreiben würde.</summary>
    public static string Format(bool allowed)
        => allowed ? "true" : "false";

    internal static string Describe(HostExecutionSwitchValue value) => value switch
    {
        HostExecutionSwitchValue.NotSet => "nicht gesetzt",
        HostExecutionSwitchValue.True => "true",
        HostExecutionSwitchValue.False => "false",
        _ => string.Create(CultureInfo.InvariantCulture, $"unlesbar"),
    };
}

/// <summary>Ergebnis von <see cref="HostExecutionSwitch.Parse"/>.</summary>
public enum HostExecutionSwitchValue
{
    /// <summary>Keine ausdrückliche Einstellung — der Fall, in dem die Bestandsübernahme greift.</summary>
    NotSet = 0,
    True = 1,
    False = 2,

    /// <summary>Gesetzt, aber nicht deutbar. Fail-closed, nicht stillschweigend „false".</summary>
    Invalid = 3,
}
