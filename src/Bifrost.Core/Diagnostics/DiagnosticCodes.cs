namespace Bifrost.Core.Diagnostics;

/// <summary>
/// Alle Diagnosecodes an <b>einer</b> Stelle (M2-Vertrag §3).
/// <para>
/// Ein Code ist das, worauf ein Betreiber ein Runbook, eine Suche oder eine Alarmregel stützt. Er
/// überlebt jede Umformulierung des Textes daneben — und er darf deshalb nie zweimal vergeben
/// werden. Genau dagegen hilft diese Datei: Wer einen Code sucht, findet hier alle, und
/// <see cref="All"/> macht die Eindeutigkeit prüfbar (siehe
/// <c>DiagnosticCodeUniquenessTests</c>). Ein Code, der hier fehlt, fällt im Test auf.
/// </para>
/// <para>
/// <b>Codes werden nicht wiederverwendet.</b> Wird ein Check abgeschafft, bleibt seine Zeile als
/// vergebener Code stehen (auskommentiert mit Grund), statt die Nummer neu zu vergeben — sonst
/// bedeutet derselbe Code in zwei Versionen zwei verschiedene Dinge, und jedes Runbook, das ihn
/// nennt, wird still falsch.
/// </para>
/// </summary>
public static class DiagnosticCodes
{
    // ── BFR-CFG: Konfiguration, Umgebungsvariablen, Datenverzeichnis ─────────────────────────────

    /// <summary>Datenverzeichnis vorhanden und beschreibbar.</summary>
    public const string DataDirectory = "BFR-CFG-0001";

    /// <summary>Alt benannte <c>MCPMCP_*</c>-Umgebungsvariablen sind noch in Benutzung.</summary>
    public const string LegacyEnvironmentVariables = "BFR-CFG-0002";

    /// <summary>Öffentliche Basis-URL gesetzt und brauchbar, wenn ein Proxy oder OAuth davorsteht.</summary>
    public const string PublicBaseUrl = "BFR-CFG-0003";

    // ── BFR-DB: Datenbank, Migrationen, Provider ────────────────────────────────────────────────

    /// <summary>Datenbank-Provider bekannt und zur Verbindungsangabe passend.</summary>
    public const string DatabaseProvider = "BFR-DB-0001";

    /// <summary>Datenbank erreichbar.</summary>
    public const string DatabaseReachable = "BFR-DB-0002";

    /// <summary>Angewendete Migrationen (Migrationshistorie vorhanden).</summary>
    public const string DatabaseAppliedMigrations = "BFR-DB-0003";

    /// <summary>Ausstehende Migrationen.</summary>
    public const string DatabasePendingMigrations = "BFR-DB-0004";

    /// <summary>SQLite-Datenbankdatei im Datenverzeichnis (auch der alte Name).</summary>
    public const string SqliteDatabaseFile = "BFR-DB-0005";

    /// <summary>
    /// Kann der vorhandene <c>pg_dump</c> diesen Server überhaupt sichern? (ADR-0024 E2)
    /// <para>
    /// Der teuerste Zeitpunkt, das zu erfahren, ist der Ernstfall. Ubuntu 24.04 liefert Client 16,
    /// ein aktueller Server ist 17 oder 18 — und <c>pg_dump</c> bricht dann mit „aborting because of
    /// server version mismatch" ab. Dieser Code macht die Lage zu einem Befund <b>vor</b> der ersten
    /// Sicherung.
    /// </para>
    /// </summary>
    public const string PostgresBackupToolVersion = "BFR-DB-0006";

    // ── BFR-KEY: DataProtection-Key-Ring ────────────────────────────────────────────────────────

    /// <summary>Key-Ring-Verzeichnis vorhanden und nicht leer.</summary>
    public const string KeyRingPresent = "BFR-KEY-0001";

    /// <summary>Key-Ring ungeschützt (kein Zertifikat konfiguriert).</summary>
    public const string KeyRingUnprotected = "BFR-KEY-0002";

    /// <summary>Konfiguriertes Key-Ring-Zertifikat ist am angegebenen Pfad vorhanden.</summary>
    public const string KeyRingCertificate = "BFR-KEY-0003";

    /// <summary>Fehlt Schlüsselmaterial, das laut Zeugeneintrag vorhanden sein müsste? (WP3.3)</summary>
    public const string KeyRingLoss = "BFR-KEY-0004";

    /// <summary>Herkunft des Zertifikatspassworts — Umgebung oder Datei-Secret (FR-P048).</summary>
    public const string KeyRingPasswordSource = "BFR-KEY-0005";

    // ── BFR-NET: Ports, öffentliche Adresse, Proxy-Vertrauen ────────────────────────────────────

    /// <summary>Konfigurierter Port frei bzw. erwartbar belegt.</summary>
    public const string ListenPort = "BFR-NET-0001";

    /// <summary>Nur HTTP, kein TLS-Proxy deklariert — das Sitzungs-Cookie trägt trotzdem 'Secure'.</summary>
    public const string InsecureCookieTransport = "BFR-NET-0002";

    /// <summary><c>BIFROST_TRUSTED_PROXIES</c> ist lesbar (ein Tippfehler bricht den Start ab).</summary>
    public const string TrustedProxies = "BFR-NET-0003";

    // ── BFR-RT: Container-Runtime, WASI-Host ────────────────────────────────────────────────────

    /// <summary>Container-Runtime vorhanden und in einem Modus, der die Policy trägt.</summary>
    public const string ContainerRuntime = "BFR-RT-0001";

    /// <summary>WASI-Host-Binary am konfigurierten Pfad.</summary>
    public const string WasiHost = "BFR-RT-0002";

    // ── BFR-UP: Upstreams ───────────────────────────────────────────────────────────────────────

    /// <summary>Zustände der konfigurierten Upstreams.</summary>
    public const string UpstreamStates = "BFR-UP-0001";

    // ── BFR-UP-0010…0019: die Zeitlinie EINES Verbindungsversuchs (WP4.6) ───────────────────────
    //
    // BFR-UP-0001 beschreibt den Bestand („welche Upstreams sind bereit?"). Die Codes hier
    // beschreiben etwas anderes: den Verlauf eines einzelnen Versuchs, sich mit EINER Konfiguration
    // zu verbinden. Deshalb ein eigener Zehnerblock statt der nächsten freien Nummer — ein Code
    // darf nie zwei Bedeutungen haben, auch nicht zwei verwandte (siehe BFR-POL oben).
    //
    // Die Reihenfolge der Nummern IST die Reihenfolge der Stufen. Wer BFR-UP-0013 im Log findet,
    // weiss damit ohne Nachschlagen, dass 0010 bis 0012 vorher durchgelaufen sind.

    /// <summary>Stufe 1 — Aufbau der Konfiguration (Slug, Transport, Pflichtfelder).</summary>
    public const string UpstreamValidation = "BFR-UP-0010";

    /// <summary>Stufe 2 — darf diese Konfiguration auf dieser Instanz überhaupt starten (ADR-0025)?</summary>
    public const string UpstreamPolicy = "BFR-UP-0011";

    /// <summary>Stufe 3 — ist das Nötige da: Programm, Container-Runtime, WASI-Host, Namensauflösung.</summary>
    public const string UpstreamRuntime = "BFR-UP-0012";

    /// <summary>Stufe 4 — Zielschutz: zeigt die Adresse nach innen, ohne dass das erlaubt wäre (SSRF)?</summary>
    public const string UpstreamTargetGuard = "BFR-UP-0013";

    /// <summary>Stufe 5 — Anmeldung: sind Zugangsdaten vollständig, und hat das Ziel sie angenommen?</summary>
    public const string UpstreamAuth = "BFR-UP-0014";

    /// <summary>Stufe 6 — Protokoll-Handshake: Transport steht, Gegenstelle spricht das Protokoll.</summary>
    public const string UpstreamHandshake = "BFR-UP-0015";

    /// <summary>Stufe 7 — Discovery: der Katalog kam an und war lesbar.</summary>
    public const string UpstreamDiscovery = "BFR-UP-0016";

    // ── BFR-POL: Ausführungs-Policy (ADR-0025, M3-Vertrag §2) ───────────────────────────────────
    //
    // Reserviert ist BFR-POL-0001…0099. Die Nummern 0001…0005 sind bereits als *Reason-Codes*
    // vergeben (HostExecutionReason in Bifrost.Abstractions); sie begründen eine einzelne
    // Entscheidung über einen Upstream. Die beiden Codes hier beschreiben den Zustand der Instanz.
    // Deshalb ein eigener Zehnerblock: Ein Code darf nie zwei Bedeutungen haben, auch nicht zwei
    // verwandte.

    /// <summary>Ist native Host-Ausführung erlaubt, und woher stammt diese Antwort?</summary>
    public const string HostExecutionPolicy = "BFR-POL-0010";

    /// <summary>Diese Instanz hat ihren bisherigen Zustand übernommen (ADR-0025 E3).</summary>
    public const string HostExecutionAdoption = "BFR-POL-0011";

    /// <summary>
    /// Jeder oben vergebene Code, einmal. Handgepflegt — der Test vergleicht diese Liste per
    /// Reflexion gegen die Konstanten und wird rot, wenn eine fehlt oder ein Wert doppelt vorkommt.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DataDirectory,
        LegacyEnvironmentVariables,
        PublicBaseUrl,
        DatabaseProvider,
        DatabaseReachable,
        DatabaseAppliedMigrations,
        DatabasePendingMigrations,
        SqliteDatabaseFile,
        PostgresBackupToolVersion,
        KeyRingPresent,
        KeyRingUnprotected,
        KeyRingCertificate,
        KeyRingLoss,
        KeyRingPasswordSource,
        ListenPort,
        InsecureCookieTransport,
        TrustedProxies,
        ContainerRuntime,
        WasiHost,
        UpstreamStates,
        UpstreamValidation,
        UpstreamPolicy,
        UpstreamRuntime,
        UpstreamTargetGuard,
        UpstreamAuth,
        UpstreamHandshake,
        UpstreamDiscovery,
        HostExecutionPolicy,
        HostExecutionAdoption,
    ];

    /// <summary>
    /// Die Codes der Upstream-Zeitlinie (WP4.6). Sie beschreiben <b>einen</b> Verbindungsversuch mit
    /// <b>einer</b> Konfiguration und stehen deshalb nicht im Instanzbericht: Dort gäbe es nichts,
    /// worauf sie sich beziehen — kein Versuch, kein Verlauf. Sie entstehen, wenn jemand testet oder
    /// anschliesst.
    /// </summary>
    public static IReadOnlyList<string> UpstreamTimeline { get; } =
    [
        UpstreamValidation,
        UpstreamPolicy,
        UpstreamRuntime,
        UpstreamTargetGuard,
        UpstreamAuth,
        UpstreamHandshake,
        UpstreamDiscovery,
    ];

    /// <summary>
    /// Die Codes, die ein <b>Instanzbericht</b> führt — alles ausser der Zeitlinie. Ein neuer Code
    /// gehört in genau eine der beiden Mengen; der Test darüber wird rot, wenn er in keiner steht.
    /// </summary>
    public static IReadOnlyList<string> InstanceReport { get; } =
        [.. All.Where(code => !UpstreamTimeline.Contains(code, StringComparer.Ordinal))];
}
