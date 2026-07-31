namespace Bifrost.Abstractions;

/// <summary>
/// Prüfrichtung einer Guard-Regel (ADR-0011). Die beiden Richtungen decken verschiedene
/// Bedrohungen ab und werden getrennt konfiguriert.
/// </summary>
public enum GuardDirection
{
    /// <summary>Argumente auf dem Weg zum Tool — Exfiltration durch einen gesteuerten Agenten.</summary>
    Outbound = 0,

    /// <summary>Ergebnisse auf dem Weg zum Agenten — ein Secret wandert ins Kontextfenster des Modells.</summary>
    Inbound = 1,

    /// <summary>Beide Richtungen.</summary>
    Both = 2,
}

/// <summary>Verhalten bei einem Treffer (ADR-0011, E1).</summary>
public enum GuardMode
{
    /// <summary>Nur zählen und auditieren, Call läuft durch — Probelauf für neue Regeln.</summary>
    Observe = 0,

    /// <summary>Call abbrechen bzw. Ergebnis zurückhalten. Default für den kuratierten Regelsatz.</summary>
    Block = 1,
}

/// <summary>
/// Eine Erkennungsregel. <see cref="Pattern"/> ist ein .NET-Regex, der mit
/// <c>RegexOptions.NonBacktracking</c> ausgeführt wird — Lookaround und Backreferences sind
/// dort nicht verfügbar und werden schon beim Speichern abgelehnt.
///
/// <see cref="Keyword"/> ist ein Vorfilter: Enthält die Nutzlast diese Zeichenfolge nicht
/// (Ordinal-Vergleich), wird der Regex gar nicht erst ausgeführt. Das halbiert die Kosten und
/// ist der Grund, warum die Prüfung im Mikrosekunden-Bereich bleibt.
/// </summary>
public sealed record GuardRule(
    string Id,
    string Description,
    string Pattern,
    string? Keyword,
    GuardDirection Direction,
    GuardMode Mode,
    bool Enabled = true,
    /// <summary>Vom Nutzer über den Freitext-Modus angelegt (ADR-0011, E2) — für die UI-Kennzeichnung.</summary>
    bool IsCustom = false);

/// <summary>
/// Ein Treffer. Enthält <b>niemals</b> den gefundenen Wert im Klartext (ADR-0011): Eine
/// Secret-Erkennung, die ihre Funde protokolliert, kopiert Secrets in ein zweites und oft
/// schwächer geschütztes System. Zum Wiedererkennen dient <see cref="Fingerprint"/> —
/// ein Hash, der gleiche Funde zusammenführt, ohne den Wert preiszugeben.
/// </summary>
public sealed record GuardFinding(
    string RuleId,
    string RuleDescription,
    GuardDirection Direction,
    GuardMode Mode,
    string Fingerprint,
    int Offset,
    int Length);

/// <summary>Ergebnis einer Prüfung.</summary>
public sealed record GuardVerdict(IReadOnlyList<GuardFinding> Findings)
{
    public static GuardVerdict Clean { get; } = new([]);

    /// <summary>True, wenn mindestens ein Treffer im Block-Modus vorliegt.</summary>
    public bool Blocked => Findings.Any(f => f.Mode == GuardMode.Block);
}

/// <summary>
/// Inhaltsprüfung im Invocation-Kern (ADR-0011). Implementierungen müssen schnell und
/// seiteneffektfrei sein — sie laufen bei jedem Call auf dem Hot Path.
/// </summary>
public interface IContentGuard
{
    /// <summary>
    /// Prüft eine JSON-Nutzlast. <paramref name="rawJson"/> ist der Rohtext; Aufrufer geben
    /// die Größe bereits geprüft herein (siehe <see cref="GuardOptions.MaxScanChars"/>).
    /// </summary>
    GuardVerdict Inspect(string rawJson, GuardDirection direction);
}

/// <summary>
/// Pflege der Regeln zur Laufzeit — hot-swappable wie Upstreams und Redaction-Muster.
/// </summary>
public interface IGuardRuleStore
{
    IReadOnlyList<GuardRule> All { get; }

    Task UpsertAsync(GuardRule rule, CancellationToken ct);

    Task RemoveAsync(string ruleId, CancellationToken ct);

    /// <summary>Feuert nach jeder Änderung, damit der Guard seine kompilierten Regex neu baut.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Betriebsschalter der Guardrail (ADR-0011).
/// </summary>
public sealed record GuardOptions
{
    /// <summary>
    /// Globaler Not-Aus. Blockieren macht den Gateway zum Single Point of Failure für
    /// Fehlalarme — wer im Störungsfall handeln muss, darf nicht auf einen Neustart angewiesen sein.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Nutzlasten oberhalb dieser Größe werden nicht geprüft und deshalb <b>abgewiesen</b>
    /// (ADR-0011, E4): Ein ungeprüftes Groß-Ergebnis wäre der blinde Fleck, den ein Angreifer
    /// ansteuert. Zusammen mit FR-16 (Ergebnis-Kürzung) laufen legitime Groß-Ergebnisse gekürzt
    /// statt blockiert durch.
    /// </summary>
    public int MaxScanChars { get; init; } = 256 * 1024;

    /// <summary>
    /// Obergrenze je Regex-Auswertung. NonBacktracking garantiert lineare Laufzeit in der
    /// Eingabelänge, deckelt aber keine sehr großen Eingaben — dafür ist dieser Wert da.
    /// </summary>
    public TimeSpan MatchTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Erlaubt Admins, eigene Regex im Freitext zu hinterlegen (ADR-0011, E2).
    /// <b>Vertrauensentscheidung, keine technische Absicherung:</b> .NET bietet laut Microsoft
    /// keine Sicherheitsgrenze gegen bösartige Muster — auch NonBacktracking nicht, das gegen
    /// teure Eingaben schützt. Wer das einschaltet, erlaubt Admins Rechenzeit im Gateway-Prozess.
    /// </summary>
    public bool AllowCustomPatterns { get; init; }
}
