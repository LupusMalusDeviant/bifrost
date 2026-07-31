namespace Bifrost.Abstractions.Execution;

/// <summary>
/// Verträge der Ausführungs-Policy (M3, ADR-0025). Die Frage, ob ein fremdes Programm nativ auf dem
/// Host starten darf, wird an <b>einer</b> Stelle beantwortet — Validierung, Test, Start und
/// Paketimport fragen dieselbe.
/// <para>
/// Diese Datei wird vom Lead gelegt und ist für die Dauer der Welle eingefroren. Wer einen Vertrag
/// ändern will, meldet das, statt ihn zu erweitern.
/// </para>
/// </summary>
public interface IHostExecutionPolicy
{
    /// <summary>
    /// Darf dieser Upstream nativ auf dem Host starten? <b>Unbekannt heißt nein</b> (ADR-0025 E1) —
    /// eine Policy, die im Zweifel erlaubt, ist eine Dokumentation.
    /// </summary>
    HostExecutionDecision Evaluate(UpstreamServerConfig config);
}

/// <param name="ReasonCode">
/// Stabil und maschinenlesbar (<c>BFR-POL-0003</c>). Stabil heißt: Der Code überlebt Umformulierungen
/// des Textes — er ist das, worauf ein Betreiber ein Runbook oder eine Suche stützt.
/// </param>
/// <param name="Summary">Ein Satz, für Menschen.</param>
/// <param name="Remediation">Die nächste Handlung, wenn es eine gibt.</param>
public sealed record HostExecutionDecision(
    bool Allowed,
    string ReasonCode,
    string Summary,
    string? Remediation = null);

/// <summary>
/// Die Reason-Codes der Ausführungs-Policy. Reserviert ist <c>BFR-POL-0001…0099</c>; die
/// Diagnosecodes aus M2 bleiben unberührt.
/// <para>
/// Sie stehen hier gesammelt, damit ein Code nie zweimal vergeben wird — dieselbe Regel wie bei den
/// Diagnosecodes, und sie hat dort schon einmal eine Kollision zwischen zwei parallel arbeitenden
/// Paketen verhindert.
/// </para>
/// </summary>
public static class HostExecutionReason
{
    /// <summary>Der Upstream läuft isoliert; die Host-Policy ist nicht betroffen.</summary>
    public const string NotNative = "BFR-POL-0001";

    /// <summary>Host-Ausführung ist ausdrücklich erlaubt.</summary>
    public const string Allowed = "BFR-POL-0002";

    /// <summary>Host-Ausführung ist verboten — die Vorgabe für neue Instanzen (ADR-0025 E2).</summary>
    public const string Forbidden = "BFR-POL-0003";

    /// <summary>
    /// Erlaubt, weil eine bestehende Instanz ihren bisherigen Zustand übernommen hat (ADR-0025 E3).
    /// Ausdrücklich ein <b>eigener</b> Code: „erlaubt, weil jemand das wollte" und „erlaubt, weil es
    /// schon immer so lief" sind verschiedene Aussagen, und nur die zweite verlangt eine Handlung.
    /// </summary>
    public const string AdoptedFromExistingInstance = "BFR-POL-0004";

    /// <summary>
    /// Verboten, weil die Entscheidung nicht getroffen werden konnte. Fail-closed: Das ist der Fall,
    /// in dem eine nachlässige Policy „ja" sagen würde.
    /// </summary>
    public const string Undetermined = "BFR-POL-0005";
}
