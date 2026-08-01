using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Diagnostics.Upstreams;

/// <summary>
/// Die Stufen eines Verbindungsversuchs, in genau der Reihenfolge, in der sie durchlaufen werden
/// (WP4.6).
/// <para>
/// <b>Warum eine Zeitlinie und nicht ein Ergebnis:</b> „Verbindung fehlgeschlagen" nennt das Ende
/// und verschweigt jede Zwischenstufe. Ein Betreiber weiss danach nicht, ob der Name nicht auflöst,
/// das Ziel abgewiesen wurde, die Anmeldung scheiterte oder die Gegenstelle ein kaputtes Schema
/// liefert — vier völlig verschiedene Handlungen. Die Stufe, die als erste scheitert, ist die
/// Antwort; alles danach ist Folge und wird als <b>nicht erreicht</b> geführt, nicht als Fehler.
/// </para>
/// <para>
/// Die Reihenfolge der Aufzählungswerte ist bedeutungstragend: Sie ist die Kette. Wer eine Stufe
/// einfügt, fügt sie an ihrer zeitlichen Stelle ein und vergibt einen neuen Code — die Nummern in
/// <see cref="DiagnosticCodes"/> werden nicht neu verteilt.
/// </para>
/// </summary>
public enum UpstreamStage
{
    /// <summary>Aufbau der Konfiguration. Ohne sie ist alles Weitere gegenstandslos.</summary>
    Validation = 0,

    /// <summary>Darf das auf dieser Instanz starten (ADR-0025)?</summary>
    Policy = 1,

    /// <summary>Ist das Nötige da — Programm, Container-Runtime, WASI-Host, aufgelöster Name?</summary>
    Runtime = 2,

    /// <summary>Zeigt das Ziel nach innen, ohne dass das freigegeben wäre (SSRF)?</summary>
    TargetGuard = 3,

    /// <summary>Anmeldung: vollständig hinterlegt und von der Gegenstelle angenommen?</summary>
    Auth = 4,

    /// <summary>Protokoll-Handshake: Transport steht, die Gegenstelle spricht das Protokoll.</summary>
    Handshake = 5,

    /// <summary>Discovery: der Katalog kam an und war lesbar.</summary>
    Discovery = 6,
}

/// <summary>
/// Was aus einer Stufe wurde. Drei Zustände, die man auseinanderhalten muss — sonst steht im
/// Bericht nach der ersten Ursache noch eine Reihe Folgefehler, und das ist wieder eine Sackgasse,
/// nur länger.
/// </summary>
public enum UpstreamStageOutcome
{
    /// <summary>Durchlaufen und in Ordnung.</summary>
    Passed = 0,

    /// <summary>Hier ist es gescheitert. Genau eine Stufe eines Berichts trägt das.</summary>
    Failed = 1,

    /// <summary>
    /// Nicht erreicht: Eine frühere Stufe hat die Kette beendet. <b>Kein Fehler</b> — über diese
    /// Stufe ist nichts bekannt, und das ist die ehrliche Aussage.
    /// </summary>
    NotReached = 2,

    /// <summary>
    /// Übersprungen, weil auf diese Konfiguration nicht anwendbar (ein stdio-Upstream hat kein
    /// Netzwerkziel). Der Grund steht im Befund und ist Pflicht.
    /// </summary>
    Skipped = 3,
}

/// <summary>
/// Eine Stufe mit ihrem Befund. Der Befund ist ein <see cref="DiagnosticCheck"/> aus M2 — dasselbe
/// Modell wie im Instanzbericht, damit Code, Text und Abhilfe überall gleich aussehen und durch
/// dieselbe Redaktion laufen.
/// </summary>
public sealed record UpstreamStageResult(
    UpstreamStage Stage,
    UpstreamStageOutcome Outcome,
    DiagnosticCheck Check)
{
    public string Code => Check.Code;
}

/// <summary>
/// Was die Gegenstelle über sich preisgegeben hat: ausgehandeltes Protokoll und angebotene
/// Fähigkeiten.
/// <para>
/// <see cref="ProtocolVersion"/> ist <c>null</c>, solange die Angabe nicht zu bekommen ist. Ein
/// erfundener oder aus der Konfiguration abgeleiteter Wert wäre hier schlimmer als eine Lücke: Er
/// sähe aus wie eine Messung.
/// </para>
/// </summary>
/// <param name="Transport">Der verwendete Transport — die einzige Angabe, die immer feststeht.</param>
/// <param name="ProtocolVersion">Ausgehandelte Protokollfassung; <c>null</c> = nicht ermittelt.</param>
/// <param name="Capabilities">
/// Beobachtete Fähigkeiten (<c>tools</c>, <c>resources.subscribe</c>, <c>prompts.listChanged</c>,
/// <c>experimental:…</c>). Beobachtet heisst: aus dem, was tatsächlich ankam — nicht aus dem, was
/// die Konfiguration erwartet. <b>Nur Namen, nie Werte</b>: Ein Capability-Objekt kann Felder
/// tragen, die niemand vorhergesehen hat.
/// </param>
/// <param name="ToolCount">Zahl der entdeckten Werkzeuge.</param>
/// <param name="Note">Warum eine Angabe fehlt, falls eine fehlt.</param>
/// <param name="Availability">
/// Wie die fehlende Fassung zu lesen ist. <b>Die Unterscheidung ist der Punkt:</b> Bei einem
/// OpenAPI- oder CLI-Upstream gibt es keine Fassung zu ermitteln
/// (<see cref="UpstreamProtocolAvailability.NotApplicable"/>) — bei einem MCP-Upstream ohne Fassung
/// dagegen wäre etwas zu holen gewesen, und es kam nichts
/// (<see cref="UpstreamProtocolAvailability.Unknown"/>). Beides gleich zu melden wäre eine
/// Auskunft, die man nicht benutzen kann.
/// <para>
/// Der Parameter steht am Ende und trägt eine Vorgabe: Ein Aufrufer, der ihn nicht setzt, behauptet
/// damit nichts.
/// </para>
/// </param>
public sealed record UpstreamNegotiation(
    string Transport,
    string? ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    int ToolCount,
    string? Note = null,
    UpstreamProtocolAvailability Availability = UpstreamProtocolAvailability.Unknown)
{
    /// <summary>
    /// Was in der Zeile „Protokoll" steht. An <b>einer</b> Stelle, weil es an dreien gebraucht wird
    /// — Oberfläche, REST-Antwort und Test. Drei Formulierungen desselben Zustands wären drei
    /// Wahrheiten, von denen zwei veralten.
    /// </summary>
    public string ProtocolLabel => Availability switch
    {
        UpstreamProtocolAvailability.Negotiated when ProtocolVersion is { Length: > 0 } version => version,
        UpstreamProtocolAvailability.NotApplicable => "kein MCP — nicht zutreffend",
        _ => "nicht ermittelt",
    };
}

/// <summary>
/// Der Bericht eines Verbindungsversuchs: die Zeitlinie, die erste scheiternde Stufe und die
/// Angaben der Gegenstelle.
/// </summary>
/// <param name="RequestId">
/// Die Kennung, unter der dieser Lauf im Serverlog steht (WP4.6, Punkt 4). Sie steht im Bericht und
/// in jeder Logzeile des Laufs — damit ein Betreiber vom Bildschirm ins Log kommt, ohne über
/// Zeitstempel zu raten.
/// </param>
public sealed record UpstreamDiagnosticReport(
    string Slug,
    UpstreamTransportKind Kind,
    string RequestId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<UpstreamStageResult> Stages,
    UpstreamNegotiation? Negotiation)
{
    /// <summary>Die Stufe, an der es scheiterte — oder <c>null</c>, wenn nichts scheiterte.</summary>
    public UpstreamStageResult? FirstFailure
        => Stages.FirstOrDefault(stage => stage.Outcome is UpstreamStageOutcome.Failed);

    public bool Succeeded => FirstFailure is null;

    /// <summary>
    /// Eine Zeile für Log und Kurzanzeige: Code, Stufe, Zusammenfassung. Bewusst mit Code voran —
    /// der Text wird umformuliert, der Code nicht.
    /// </summary>
    public string Headline()
    {
        var failure = FirstFailure;
        return failure is null
            ? $"[{RequestId}] Verbindung zu '{Slug}' vollständig durchlaufen."
            : $"[{RequestId}] {failure.Code} ({UpstreamStages.Label(failure.Stage)}): {failure.Check.Summary}";
    }
}

/// <summary>
/// Die Stammdaten der Stufen: Code, Bezeichnung, geprüfte Frage. Sie stehen an <b>einer</b> Stelle,
/// weil sie an drei gebraucht werden — im Bericht, in der Oberfläche und im Log.
/// </summary>
public static class UpstreamStages
{
    /// <summary>Alle Stufen in ihrer zeitlichen Reihenfolge.</summary>
    public static IReadOnlyList<UpstreamStage> All { get; } =
    [
        UpstreamStage.Validation,
        UpstreamStage.Policy,
        UpstreamStage.Runtime,
        UpstreamStage.TargetGuard,
        UpstreamStage.Auth,
        UpstreamStage.Handshake,
        UpstreamStage.Discovery,
    ];

    public static string Code(UpstreamStage stage) => stage switch
    {
        UpstreamStage.Validation => DiagnosticCodes.UpstreamValidation,
        UpstreamStage.Policy => DiagnosticCodes.UpstreamPolicy,
        UpstreamStage.Runtime => DiagnosticCodes.UpstreamRuntime,
        UpstreamStage.TargetGuard => DiagnosticCodes.UpstreamTargetGuard,
        UpstreamStage.Auth => DiagnosticCodes.UpstreamAuth,
        UpstreamStage.Handshake => DiagnosticCodes.UpstreamHandshake,
        UpstreamStage.Discovery => DiagnosticCodes.UpstreamDiscovery,
        _ => throw new ArgumentOutOfRangeException(
            nameof(stage), stage, "Neue Stufe ohne Code — jede Stufe braucht einen aus DiagnosticCodes."),
    };

    public static string Label(UpstreamStage stage) => stage switch
    {
        UpstreamStage.Validation => "Validierung",
        UpstreamStage.Policy => "Policy",
        UpstreamStage.Runtime => "Runtime/DNS",
        UpstreamStage.TargetGuard => "Zielschutz",
        UpstreamStage.Auth => "Anmeldung",
        UpstreamStage.Handshake => "Handshake",
        UpstreamStage.Discovery => "Discovery",
        _ => stage.ToString(),
    };

    /// <summary>Was die Stufe prüft — der Satz, den die Oberfläche neben den Code stellt.</summary>
    public static string Question(UpstreamStage stage) => stage switch
    {
        UpstreamStage.Validation =>
            "Ist die Konfiguration in sich stimmig — Slug, Transport, Pflichtfelder?",
        UpstreamStage.Policy =>
            "Darf diese Konfiguration auf dieser Instanz überhaupt starten (ADR-0025)?",
        UpstreamStage.Runtime =>
            "Ist das Nötige vorhanden — Programm, Container-Runtime, WASI-Host, auflösbarer Name?",
        UpstreamStage.TargetGuard =>
            "Zeigt die Adresse in ein internes Netz, ohne dass das freigegeben wäre?",
        UpstreamStage.Auth =>
            "Sind die Zugangsdaten vollständig, und hat die Gegenstelle sie angenommen?",
        UpstreamStage.Handshake =>
            "Kommt der Transport zustande, und spricht die Gegenstelle das Protokoll?",
        UpstreamStage.Discovery =>
            "Kam ein lesbarer Katalog an?",
        _ => string.Empty,
    };

    /// <summary>Die Anzeige eines Ausgangs — dieselben Worte in Oberfläche, Log und Test.</summary>
    public static string Label(UpstreamStageOutcome outcome) => outcome switch
    {
        UpstreamStageOutcome.Passed => "erreicht",
        UpstreamStageOutcome.Failed => "gescheitert",
        UpstreamStageOutcome.NotReached => "nicht erreicht",
        UpstreamStageOutcome.Skipped => "übersprungen",
        _ => outcome.ToString(),
    };
}
