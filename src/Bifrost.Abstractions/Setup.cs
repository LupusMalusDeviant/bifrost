using Bifrost.Abstractions.Importing;

namespace Bifrost.Abstractions.Setup;

/// <summary>
/// Die neun Schritte des gefuehrten Erstaufbaus (WP4.4, FR-P012).
/// <para>
/// <b>Die Reihenfolge ist der Vertrag</b>, nicht die Dekoration: Schritt 2 legt den Zugang an, ab
/// Schritt 3 gibt es einen Eigentuemer, und erst ab Schritt 4 entsteht ueberhaupt etwas. Ein
/// Wizard, der seine Schritte in einer Zahl fuehrt, laesst sich nicht darauf pruefen — deshalb
/// stehen sie hier als benannte Werte und nicht als <c>int</c>.
/// </para>
/// </summary>
public enum SetupStep
{
    /// <summary>Was diese Instanz sein soll — abgeschirmt oder lokale Werkbank.</summary>
    SecurityMode = 1,

    /// <summary>Erstzugang mit dem Setup-Token einloesen (WP3.4).</summary>
    AdminAccess = 2,

    /// <summary>Key-Ring-Schutz pruefen und, wo noetig, ausdruecklich erklaeren (WP3.3).</summary>
    KeyRing = 3,

    /// <summary>Fremde Konfiguration einlesen oder den ersten Upstream von Hand beschreiben.</summary>
    Source = 4,

    /// <summary>Befunde je Eintrag ansehen, auswaehlen, Risiken der Auswahl bestaetigen.</summary>
    ImportReview = 5,

    /// <summary>Verbindung und Discovery pruefen — die Zeitlinie aus WP4.6.</summary>
    Connection = 6,

    /// <summary>Agentenidentitaet, Rolle und Profil erzeugen.</summary>
    Agent = 7,

    /// <summary>Das passende Client-Snippet anzeigen (FR-41).</summary>
    Snippet = 8,

    /// <summary>Einen echten Toolaufruf ausfuehren und das Ergebnis erklaeren.</summary>
    TestCall = 9,
}

/// <summary>
/// Was der Betreiber in Schritt 1 waehlt.
///
/// <para>
/// <b>Das ist eine Vorgabe fuer die spaeteren Schritte, keine Berechtigungsgrenze.</b> Was eine
/// Instanz tatsaechlich zulaesst, entscheidet die Ausfuehrungs-Policy (ADR-0025 E1) und der
/// Validator — nicht dieser Wert. Er waehlt aus, was in den Formularen voreingestellt ist und mit
/// welcher Isolationsangabe ein Import angelegt wird. Wer hier <see cref="Workbench"/> waehlt und
/// auf einer Instanz sitzt, die native Ausfuehrung verbietet, bekommt beim Anlegen eine Absage aus
/// dem Kern — und nicht etwa eine Ausnahme, weil der Wizard es so wollte.
/// </para>
/// </summary>
public enum SetupSecurityMode
{
    /// <summary>
    /// Die Vorgabe: Upstreams laufen im Container, Ziele im internen Netz bleiben gesperrt.
    /// </summary>
    Shielded = 0,

    /// <summary>
    /// Lokale Werkbank: Programme duerfen nativ starten. Nur sinnvoll, wenn die Instanz native
    /// Ausfuehrung ueberhaupt erlaubt — sonst steht die Absage in Schritt 4.
    /// </summary>
    Workbench = 1,
}

/// <summary>Woher der erste Upstream kommt.</summary>
public enum SetupSourceKind
{
    /// <summary>Noch nicht entschieden.</summary>
    Undecided = 0,

    /// <summary>Aus einer fremden Konfigurationsdatei (WP4.1/4.2).</summary>
    Import = 1,

    /// <summary>Von Hand beschrieben.</summary>
    Manual = 2,
}

/// <summary>Der Stand des Erstzugangs (WP3.4), so wie der Wizard ihn braucht.</summary>
/// <param name="Phase">Der geschriebene Name der Phase.</param>
/// <param name="Pending">Kann jetzt jemand ein Setup-Token einloesen?</param>
/// <param name="AnyAdmin">Gibt es ueberhaupt einen UI-Zugang?</param>
/// <param name="HandoverPath">Wo der Zettel mit dem Token liegt, falls er noch da ist.</param>
public sealed record SetupAccessFacts(
    string Phase,
    bool Pending,
    bool AnyAdmin,
    DateTimeOffset? ExpiresAt,
    string? HandoverPath,
    string Summary);

/// <summary>
/// Der Stand des Key-Ring-Schutzes (WP3.3). <b>Reine Auskunft:</b> Die Betriebsart wird ueber die
/// Umgebung gesetzt, nicht ueber diese Oberflaeche — was hier steht, sind die Namen der Schalter
/// und das Urteil des Startlaufs.
/// </summary>
/// <param name="Mode">Die aufgeloeste Betriebsart, geschrieben.</param>
/// <param name="Declared">Hat der Betreiber sie ausdruecklich erklaert?</param>
/// <param name="Verdict">Das Urteil des Startlaufs, oder <c>null</c>, wenn keines vorliegt.</param>
public sealed record SetupKeyRingFacts(
    string Mode,
    bool Declared,
    string? Verdict,
    string Summary,
    string? Remediation,
    string ProtectionSetting,
    string NoneValue,
    string CertificatePathSetting);

/// <summary>Was die Ausfuehrungs-Policy dieser Instanz sagt (ADR-0025).</summary>
/// <param name="Allowed">Duerfen Programme nativ auf dem Host starten?</param>
/// <param name="ReasonCode">Der stabile Code aus <see cref="Execution.HostExecutionReason"/>.</param>
public sealed record SetupExecutionFacts(
    bool Allowed,
    string ReasonCode,
    string Summary,
    string? Remediation,
    string SwitchName);

/// <summary>Ein angeschlossener Upstream, so weit der Wizard ihn zeigt.</summary>
public sealed record SetupUpstreamFacts(
    ServerId Id,
    string Slug,
    string State,
    int ToolCount,
    string? LastError);

/// <summary>
/// Der Zustand der laufenden Instanz. <b>Er wird gelesen, nie gespeichert</b> — sonst zeigte der
/// Wizard nach einem Neuladen eine Lage, die es nicht mehr gibt. Genau darauf beruht das
/// Fortsetzen: Was schon angelegt ist, steht in der Instanz und nicht im Sitzungszustand.
/// </summary>
public sealed record SetupFacts(
    SetupAccessFacts Access,
    SetupKeyRingFacts KeyRing,
    SetupExecutionFacts Execution,
    IReadOnlyList<SetupUpstreamFacts> Upstreams,
    IReadOnlyList<string> AgentNames,
    int ToolCount);

/// <summary>Woher die eingelesene Konfiguration stammt.</summary>
public sealed record SetupImportSourceInfo(
    string Provider,
    string? SchemaVersion,
    double Confidence,
    string? OriginPath);

/// <summary>
/// Ein Eintrag der eingelesenen Datei, <b>wertefrei</b>.
///
/// <para>
/// <b>Warum es diesen Typ gibt und nicht einfach <see cref="ImportCandidate"/>:</b> Der Kandidat
/// traegt die Klartextwerte der Quelle — er muss, sonst liesse sich aus ihm nichts anlegen. Was die
/// Oberflaeche zeigt, entsteht aus der Positivliste der Vorschauprojektion; <see cref="Transport"/>
/// ist deren Kurzfassung. Ein neues wertetragendes Feld in <c>UpstreamServerConfig</c> erscheint
/// hier von selbst <em>nicht</em>, und das ist der Unterschied, auf den es ankommt.
/// </para>
/// </summary>
/// <param name="CanApply">
/// Ob genau dieser Eintrag angelegt werden kann — <see cref="ImportPlan.IsApplicable"/>, also
/// einschliesslich der planweiten Fehler und der Befunde, die ueber ihren Pfad genau ihn meinen.
/// </param>
/// <param name="Blockers">
/// Warum nicht, falls nicht. Leer bei einem anwendbaren Eintrag.
/// </param>
public sealed record SetupImportEntry(
    string SourceName,
    string Slug,
    string DisplayName,
    string Kind,
    string Transport,
    bool CanApply,
    IReadOnlyList<ImportFinding> Findings,
    IReadOnlyList<ImportSecret> Secrets,
    IReadOnlyList<ImportFinding> Blockers,
    string? SourcePath);

/// <summary>Das Ergebnis des Einlesens — ohne dass irgendetwas angelegt worden waere.</summary>
/// <param name="AnyApplicable">Ist mindestens ein Eintrag anwendbar?</param>
public sealed record SetupImportOutcome(bool AnyApplicable, string Summary);

/// <summary>Ein angelegter Server.</summary>
public sealed record SetupCreatedServer(ServerId Id, string Slug);

/// <summary>
/// Ein Eintrag, der nicht angelegt wurde — <b>mit Namen und Grund</b>. Ein Teilimport, der die
/// Differenz verschweigt, sieht aus wie ein vollstaendiger, bis jemand die Liste zaehlt.
/// </summary>
public sealed record SetupSkippedServer(string SourceName, string Reason);

/// <summary>
/// Was die Uebernahme bewirkt hat.
/// </summary>
/// <param name="Refusal">
/// Gesetzt, wenn gar nicht erst angelegt wurde — fehlende Bestaetigung, leere Auswahl, planweiter
/// Fehler. <c>null</c> heisst: Es wurde angelegt, und was nicht, steht in
/// <see cref="Skipped"/>.
/// </param>
public sealed record SetupApplyReport(
    IReadOnlyList<SetupCreatedServer> Created,
    IReadOnlyList<SetupSkippedServer> Skipped,
    string? Refusal = null)
{
    public static SetupApplyReport Refused(string reason) => new([], [], reason);
}

/// <summary>Das Ergebnis des Testaufrufs aus Schritt 9.</summary>
/// <param name="Explanation">
/// Warum das Ergebnis so ausgefallen ist — der Teil, der einen Erstaufbau von einem Ratespiel
/// unterscheidet.
/// </param>
public sealed record SetupTestCall(
    string Tool,
    InvocationStatus Status,
    string? Detail,
    string Explanation)
{
    public bool Succeeded => Status is InvocationStatus.Success;
}

/// <summary>
/// Der serverseitige Zustand eines Wizard-Durchlaufs.
///
/// <para>
/// <b>Bewusst kein <c>record</c>:</b> Ein Record erzeugt ein <c>ToString()</c>, das jede
/// Eigenschaft ausgibt — und dieses Objekt haelt den eingelesenen <see cref="Plan"/> mitsamt den
/// Klartextwerten der Quelle. Ein einziges <c>LogDebug("{Session}")</c> haette damit fremde
/// Zugangsdaten im Protokoll. Die Ausgabe unten nennt Fortschritt und Anzahlen, nie einen Wert.
/// Dieselbe Ueberlegung wie bei <c>KeyRingSettings</c>.
/// </para>
///
/// <para>
/// <b>Was hier NICHT liegt:</b> der ausgestellte API-Key. Er wird genau einmal angezeigt und lebt
/// nur im Circuit, der ihn ausgestellt hat. Nach einem Neuladen steht in Schritt 8 deshalb ein
/// Knopf, der einen neuen ausstellt — kein Geheimnis in einer Ablage, die laenger lebt als der
/// Blick darauf.
/// </para>
/// </summary>
public sealed class SetupSession
{
    /// <summary>Die Kennung, unter der dieser Vorgang wiedergefunden wird.</summary>
    public required string Handle { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Wann zuletzt jemand hier war — die Grundlage der Verfallsfrist.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// Der angemeldete Administrator, sobald es einen gibt (ab Schritt 3). Davor <c>null</c>: Vor
    /// dem Einloesen gibt es niemanden, dem der Vorgang gehoeren koennte. Ab dann nimmt ihn kein
    /// anderer mehr auf, auch nicht mit der richtigen Kennung.
    /// </summary>
    public string? Owner { get; set; }

    public SetupStep Step { get; set; } = SetupStep.SecurityMode;

    /// <summary>Die Wahl aus Schritt 1; <c>null</c>, solange sie aussteht.</summary>
    public SetupSecurityMode? Mode { get; set; }

    /// <summary>Das Container-Image fuer den abgeschirmten Betrieb, falls angegeben.</summary>
    public string? ContainerImage { get; set; }

    /// <summary>Hat der Betreiber den ungeschuetzten Key-Ring ausdruecklich zur Kenntnis genommen?</summary>
    public bool KeyRingAcknowledged { get; set; }

    public SetupSourceKind Source { get; set; } = SetupSourceKind.Undecided;

    /// <summary>Die Herkunftsangabe der eingelesenen Datei — eine Beschriftung, kein Leseauftrag.</summary>
    public string? OriginPath { get; set; }

    public SetupImportSourceInfo? ImportSource { get; set; }

    /// <summary>Die wertefreie Sicht auf die Eintraege — das, was die Oberflaeche zeigt.</summary>
    public IReadOnlyList<SetupImportEntry> Entries { get; set; } = [];

    /// <summary>Die Befunde, die das ganze Dokument anhalten.</summary>
    public IReadOnlyList<ImportFinding> BlockingFindings { get; set; } = [];

    /// <summary>
    /// Stellen der Quelldatei, aus denen <b>gar kein Kandidat</b> wurde — ein Eintrag ohne
    /// Transport etwa.
    ///
    /// <para>
    /// <b>Warum das eine eigene Liste braucht.</b> Ein solcher Eintrag taucht weder unter
    /// <see cref="Entries"/> auf (er ist kein Kandidat) noch unter
    /// <see cref="BlockingFindings"/> (er betrifft nur seine Stelle). Ohne diese Liste waere er
    /// unsichtbar — und eine Datei mit drei Eintraegen ergaebe wortlos zwei Server. Genau die
    /// Sorte Auslassung, die erst auffaellt, wenn jemand die Liste zaehlt.
    /// </para>
    /// </summary>
    public IReadOnlyList<ImportFinding> UnreadableEntries { get; set; } = [];

    /// <summary>Die ausgewaehlten Eintraege, an ihrem Quellnamen.</summary>
    public HashSet<string> Selected { get; } = new(StringComparer.Ordinal);

    /// <summary>Die Bestaetigung der Risiken <b>dieser Auswahl</b>.</summary>
    public bool RisksConfirmed { get; set; }

    public SetupApplyReport? Applied { get; set; }

    public string? AgentName { get; set; }

    public IdentityId? Identity { get; set; }

    public string? RoleName { get; set; }

    public string? ProfileName { get; set; }

    /// <summary>Die Beschriftung des zuletzt ausgestellten Schluessels — nie sein Wert.</summary>
    public string? KeyLabel { get; set; }

    public string? TestTool { get; set; }

    public SetupTestCall? TestResult { get; set; }

    /// <summary>
    /// Der eingelesene Plan mit den Klartextwerten der Quelle.
    ///
    /// <para>
    /// <b>Er wird nie gerendert.</b> Die Auswahl je Eintrag braucht ihn — <c>IsApplicable</c>,
    /// <c>BlockersFor</c> und <c>ConfirmationsFor</c> arbeiten auf Kandidaten, nicht auf Namen —,
    /// und angelegt wird ausschliesslich aus ihm. Was die Oberflaeche zeigt, steht in
    /// <see cref="Entries"/>. Der Zugriff laeuft ueber den Wizard-Dienst; eine Seite, die hier
    /// selbst hineingreift, hat den Vertrag verlassen.
    /// </para>
    /// </summary>
    public ImportPlan? Plan { get; set; }

    /// <summary>Fortschritt und Anzahlen — was ins Protokoll darf.</summary>
    public override string ToString()
        => $"Setup-Wizard: Schritt {(int)Step} ({Step}), Modus {Mode?.ToString() ?? "offen"}, "
            + $"{Entries.Count} Eintrag/Eintraege gelesen, {Selected.Count} ausgewaehlt, "
            + $"{Applied?.Created.Count ?? 0} angelegt.";
}

/// <summary>
/// Wann ein Schritt fertig ist und wo ein wiederaufgenommener Vorgang stehen darf.
///
/// <para>
/// <b>Warum das nicht in der Razor-Seite steht.</b> „Kommt der Nutzer weiter oder steht er?" ist die
/// Frage, an der ein Assistent scheitert — und sie ist an jedem der neun Schritte anders zu
/// beantworten. In einer Seite waere sie nur mit einem Browser pruefbar; als reine Funktionen haelt
/// ein Test sie gegen jeden Schritt einzeln. Dieselbe Ueberlegung wie bei <c>UiNavigation</c>.
/// </para>
///
/// <para>
/// <b>Hier steht trotzdem keine Kernregel.</b> Geprueft wird ausschliesslich, ob der Nutzer in
/// diesem Schritt fertig ist — gegen Zustaende, die andere ermittelt haben
/// (<see cref="SetupFacts"/>) oder die er selbst gesetzt hat. Ob etwas angelegt werden <em>darf</em>,
/// entscheidet nichts davon.
/// </para>
/// </summary>
public static class SetupProgress
{
    /// <summary>
    /// Warum dieser Schritt noch nicht fertig ist — oder <c>null</c>, wenn er es ist.
    /// <para>
    /// <b>Ein Grund statt eines toten Knopfes.</b> Ein „Weiter", das grundsaetzlich nichts tut, laesst
    /// offen, ob man etwas falsch gemacht hat; ein Satz beantwortet es.
    /// </para>
    /// </summary>
    /// <param name="signedIn">Ist dieser Browser angemeldet?</param>
    public static string? BlockerFor(SetupSession session, SetupFacts facts, bool signedIn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(facts);

        return session.Step switch
        {
            SetupStep.SecurityMode when session.Mode is null =>
                "Bitte eine Betriebsart wählen.",

            SetupStep.AdminAccess when !facts.Access.AnyAdmin =>
                "Dieser Schritt ist erst fertig, wenn ein Zugang angelegt ist.",
            SetupStep.AdminAccess when !signedIn =>
                "Es gibt einen Zugang, aber dieser Browser ist nicht angemeldet.",

            SetupStep.KeyRing when !facts.KeyRing.Declared && !session.KeyRingAcknowledged =>
                "Der Schlüsselring liegt ungeschützt und niemand hat das erklärt. Entweder ein "
                + "Zertifikat einrichten oder den ungeschützten Betrieb hier ausdrücklich bestätigen.",

            SetupStep.Source when session.Source is SetupSourceKind.Undecided =>
                "Noch keine Quelle gewählt: entweder eine Konfiguration einlesen oder einen Server "
                + "von Hand anlegen.",
            SetupStep.Source when session.Source is SetupSourceKind.Import && session.Plan is null =>
                "Es ist noch keine Konfiguration eingelesen.",
            SetupStep.Source when session.Source is SetupSourceKind.Manual
                && session.Applied is not { Created.Count: > 0 } =>
                "Der Server ist noch nicht angelegt.",

            SetupStep.ImportReview when session.Applied is null =>
                "Die Auswahl ist noch nicht übernommen.",

            SetupStep.Connection when facts.Upstreams.Count == 0 =>
                "Es ist kein Server angeschlossen — zurück zu Schritt 4.",

            SetupStep.Agent when session.Identity is null =>
                "Es ist noch keine Agenten-Identität angelegt.",

            _ => null,
        };
    }

    /// <summary>
    /// Der naechste Schritt.
    /// <para>
    /// Schritt 5 gibt es nur nach einem Import: Wer von Hand angelegt hat, hat keine Befunde, und
    /// ein leerer Bildschirm mit der Ueberschrift „Was in dieser Datei steht" waere die Sorte
    /// Sackgasse, in der jemand denkt, er habe etwas uebersehen.
    /// </para>
    /// </summary>
    public static SetupStep Next(SetupSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Step is SetupStep.TestCall)
        {
            return SetupStep.TestCall;
        }

        var next = (SetupStep)((int)session.Step + 1);
        return next is SetupStep.ImportReview && session.Source is not SetupSourceKind.Import
            ? SetupStep.Connection
            : next;
    }

    /// <summary>
    /// Wo dieser Vorgang stehen darf, nachdem er wieder aufgenommen wurde — und warum nicht dort,
    /// wo er stand.
    ///
    /// <para>
    /// <b>Das ist die Fortsetzen-Semantik.</b> Der Vorgang merkt sich, wo jemand war; ob er dort
    /// noch stehen darf, entscheidet die Instanz. Wer den Browser schliesst und wiederkommt, steht
    /// dort, wo er war — es sei denn, die Lage hat sich geaendert, und dann kommt der Grund
    /// zurueck. <c>Reason</c> ist genau dann gesetzt, wenn zurueckgesetzt wurde.
    /// </para>
    /// </summary>
    public static (SetupStep Step, string? Reason) Normalise(
        SetupSession session, SetupFacts facts, bool signedIn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(facts);

        if ((!facts.Access.AnyAdmin || !signedIn) && session.Step > SetupStep.AdminAccess)
        {
            return (SetupStep.AdminAccess, facts.Access.AnyAdmin
                ? "Dieser Vorgang war weiter, aber dieser Browser ist nicht angemeldet. Ab Schritt 3 "
                    + "legt der Wizard Dinge an; dafür braucht er einen Zugang."
                : "Dieser Vorgang war weiter, aber diese Installation hat noch keinen Zugang. "
                    + "Schritt 2 holt das nach.");
        }

        // Der eingelesene Plan lebt im Arbeitsspeicher des Gateways. Nach einem Neustart ist er weg;
        // was daraus angelegt wurde, steht in der Instanz und bleibt.
        if (session.Step is SetupStep.ImportReview && session.Plan is null)
        {
            return (SetupStep.Source,
                "Die eingelesene Konfiguration ist nicht mehr vorgemerkt — sie lag im Arbeitsspeicher "
                + "des Gateways. Bereits angelegte Server sind unberührt; die Datei noch einmal "
                + "einlesen.");
        }

        if (session.Step >= SetupStep.Snippet && session.Identity is null)
        {
            return (SetupStep.Agent, "Zu diesem Vorgang gehört noch keine Agenten-Identität.");
        }

        return (session.Step, null);
    }
}

/// <summary>Das Ergebnis eines Fortsetzungsversuchs.</summary>
/// <param name="Session">Der Vorgang, oder <c>null</c>.</param>
/// <param name="Reason">
/// Warum es keinen gibt. <b>Nie <c>null</c>, wenn <see cref="Session"/> es ist</b> — ein Wizard,
/// der einen Vorgang wortlos verliert, sieht aus wie einer, der nie einen hatte.
/// </param>
public sealed record SetupResume(SetupSession? Session, string? Reason);

/// <summary>
/// Die Ablage der laufenden Wizard-Vorgaenge.
/// <para>
/// <b>Serverseitig und prozesslokal.</b> Im Browser liegt nur die Kennung; der Zustand selbst
/// verlaesst den Serverprozess nie. Ein Neustart des Gateways verliert die Vorgaenge — was bereits
/// angelegt wurde, bleibt bestehen, und der Wizard baut seinen Stand danach aus der Instanz neu
/// auf (siehe <see cref="SetupFacts"/>).
/// </para>
/// </summary>
public interface ISetupSessionStore
{
    /// <summary>Beginnt einen Vorgang und liefert ihn samt frischer Kennung.</summary>
    SetupSession Start();

    /// <summary>
    /// Nimmt einen Vorgang wieder auf. <paramref name="owner"/> ist der angemeldete Nutzer oder
    /// <c>null</c>; ein Vorgang mit Eigentuemer wird nur diesem herausgegeben.
    /// </summary>
    SetupResume Reopen(string? handle, string? owner);

    /// <summary>Haelt einen Vorgang am Leben und verlaengert seine Frist.</summary>
    void Touch(SetupSession session);

    /// <summary>Wirft einen Vorgang weg — der Abbruch.</summary>
    void Discard(string? handle);
}

/// <summary>
/// Der Serverdienst hinter dem gefuehrten Erstaufbau (WP4.4).
///
/// <para>
/// <b>Warum es ihn gibt.</b> Die Oberflaeche ist Blazor Interactive Server und laeuft im
/// Serverprozess; sie ruft die vorhandenen Dienste direkt auf. Zwei Dinge liegen aber in
/// <c>Bifrost.Server</c> und damit ausserhalb ihrer Reichweite: der Erstzugang (WP3.4) und die
/// Vorschauprojektion des Imports (WP4.3). Dieser Port fuehrt beides an die Oberflaeche heran —
/// <b>ohne</b> den HTTP-Weg. Der Setup-Endpunkt bleibt auf Loopback beschraenkt und ist fuer lokale
/// Werkzeuge gedacht; wer den Wizard darueber baut, hat ihn falsch gebaut
/// (docs/plans/product-readiness-status.md).
/// </para>
///
/// <para>
/// <b>Hier steht keine Kernregel.</b> Was anwendbar ist, sagt <see cref="ImportPlan"/>; was sicher
/// voreingestellt wird, sagt <c>SecureUpstreamDefaults</c>; ob etwas nativ laufen darf, sagt die
/// Ausfuehrungs-Policy. Dieser Dienst ordnet an, er entscheidet nicht.
/// </para>
/// </summary>
public interface ISetupWizard
{
    /// <summary>
    /// Die groesste Datei, die eingelesen wird. Sie steht hier, damit die Oberflaeche <b>dieselbe</b>
    /// Grenze durchsetzt wie der HTTP-Weg — eine zweite Zahl im Markup waere die, die als Erstes
    /// veraltet.
    /// </summary>
    int MaxDocumentBytes { get; }

    /// <summary>Was gerade gilt. Liest nur.</summary>
    Task<SetupFacts> ReadFactsAsync(CancellationToken ct);

    /// <summary>
    /// Liest ein Dokument ein und legt Plan und wertefreie Sicht in den Vorgang. <b>Schreibt
    /// nichts</b> — ein Plan ist keine Aenderung.
    /// </summary>
    SetupImportOutcome Analyse(SetupSession session, string document, string? originPath);

    /// <summary>
    /// Die Bestaetigungen, die <b>fuer die aktuelle Auswahl</b> noetig sind
    /// (<see cref="ImportPlan.ConfirmationsFor"/>). Aendert sich die Auswahl, aendert sich diese
    /// Liste — eine Bestaetigung, die pauschal fuer alles gilt, wird zur Formalie.
    /// </summary>
    IReadOnlyList<ImportFinding> ConfirmationsFor(SetupSession session);

    /// <summary>
    /// Legt die ausgewaehlten Eintraege an — je Eintrag. Ein gesperrter Eintrag haelt die uebrigen
    /// nicht auf; er wird benannt, nicht verschwiegen.
    /// </summary>
    Task<SetupApplyReport> ApplySelectionAsync(SetupSession session, string actor, CancellationToken ct);

    /// <summary>Legt einen von Hand beschriebenen Upstream an — derselbe Weg, dieselben Vorgaben.</summary>
    Task<SetupApplyReport> ApplyManualAsync(
        SetupSession session, UpstreamServerConfig config, string actor, CancellationToken ct);

    /// <summary>Haelt eine bewusste Entscheidung des Betreibers im Audit fest.</summary>
    void Record(string actor, string detail);
}
