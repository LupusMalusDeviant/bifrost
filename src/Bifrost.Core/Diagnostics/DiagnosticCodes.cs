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
        HostExecutionPolicy,
        HostExecutionAdoption,
    ];
}
