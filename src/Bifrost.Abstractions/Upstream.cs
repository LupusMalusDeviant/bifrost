using System.Text.Json;

namespace Bifrost.Abstractions;

public enum UpstreamTransportKind
{
    Stdio = 0,
    StreamableHttp = 1,
    OpenApi = 2,
    Cli = 3,
    Wasi = 4,
    OpenRpc = 5,
}

public enum UpstreamState
{
    Starting = 0,
    Healthy = 1,
    Degraded = 2,
    Stopped = 3,
    Failed = 4,
}

public enum OpenApiAuthKind
{
    None = 0,
    ApiKeyHeader = 1,
    Bearer = 2,
    Basic = 3,
}

public enum CapabilityRisk
{
    Read = 0,
    Write = 1,
    Destructive = 2,
    Privileged = 3,
}

#pragma warning disable CA1720 // Öffentlicher Manifestvertrag verwendet die JSON-Schema-Typnamen.
public enum CliParameterType
{
    String = 0,
    Integer = 1,
    Number = 2,
    Boolean = 3,
    Enum = 4,
    Path = 5,
    SecretReference = 6,
}
#pragma warning restore CA1720

public enum CliPathAccess
{
    None = 0,
    ReadOnly = 1,
    Write = 2,
}

public sealed record StdioTransportOptions(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? WorkingDirectory = null);

public sealed record HttpTransportOptions(
    Uri Endpoint,
    IReadOnlyDictionary<string, string>? Headers = null,
    /// <summary>
    /// Erlaubt den Rückfall auf den abgelösten HTTP+SSE-Transport, wenn der Upstream kein
    /// Streamable HTTP spricht (FR-02). Default an: genau diese Transport-Heterogenität
    /// wegzukapseln ist Aufgabe eines Gateways. Abschaltbar, sobald SSE aus dem Standard fällt.
    /// </summary>
    bool AllowLegacySse = true,
    /// <summary>
    /// OAuth-Anbindung nach der MCP-Autorisierung. Gesetzt heißt: Der Gateway holt sich ein Token
    /// beim Authorization Server des Upstreams, statt einen festen Header mitzuschicken. Beides
    /// zugleich ist zulässig — Header für Zusatzangaben, das Token für die Autorisierung.
    /// </summary>
    UpstreamOAuthOptions? OAuth = null,
    /// <summary>
    /// Erlaubt Ziele in privaten, Loopback- oder Link-Local-Netzen — dieselbe Frage, die
    /// <see cref="OpenApiTransportOptions.AllowPrivateTargets"/> und
    /// <see cref="OpenRpcTransportOptions.AllowPrivateTargets"/> längst stellen. Bei MCP über HTTP
    /// fehlte sie: Der Endpunkt ging ungeprüft in den Transport, während OpenAPI, OpenRPC und der
    /// OAuth-Issuer die Adresse auflösen und private Ziele abweisen. Ein Gateway, das beliebige
    /// URLs abruft, ist ein Werkzeug, um interne Dienste zu erreichen (SSRF).
    /// <para>
    /// <b><c>null</c> heißt „nicht entschieden", nicht „verboten".</b> Bestandsinstanzen haben
    /// diesen Schalter nie gesetzt, und ein MCP-Server im eigenen Netz ist der Regelfall, nicht die
    /// Ausnahme. Sie beim nächsten Neustart abzuklemmen, wäre dieselbe stille Verhaltensänderung,
    /// die ADR-0025 E3 für die Hostausführung ausdrücklich ablehnt: Der Upstream läuft weiter, und
    /// die Übernahme wird sichtbar gemacht, statt sie anzunehmen oder ihn stillzulegen.
    /// </para>
    /// <para>
    /// Ausdrückliches <c>false</c> weist private Ziele ab. Neu angelegte Konfigurationen sollen den
    /// Wert setzen — solange das nicht überall geschieht, bleibt eine Lücke, und sie steht als
    /// solche im Fortschrittsprotokoll.
    /// </para>
    /// </summary>
    bool? AllowPrivateTargets = null);

/// <summary>
/// OpenAPI-Quelle als virtueller Upstream (FR-19). <see cref="Credential"/> liegt im Config-Blob,
/// der als Ganzes DataProtection-verschlüsselt persistiert wird (NFR-04, ADR-0007).
/// Bearer: Credential = Token; Basic: Credential = "user:pass"; ApiKeyHeader: Credential = Key,
/// Header-Name über <see cref="ApiKeyHeaderName"/> (Default X-Api-Key).
/// </summary>
/// <param name="AllowPrivateTargets">
/// Erlaubt Spec-Quelle und Ziel-API im privaten, Loopback- oder Link-Local-Netz. Vorgabe ist
/// <c>false</c>: Ohne die Prüfung ist das Gateway ein Werkzeug, um interne Dienste zu erreichen —
/// vom Cloud-Metadatendienst auf <c>169.254.169.254</c> bis zum Admin-Port auf <c>127.0.0.1</c>.
/// Wer eine API im eigenen Netz einbindet, setzt den Schalter ausdrücklich; die Absage nennt ihn
/// beim Namen, damit die Umstellung nicht zum Rätsel wird.
/// </param>
public sealed record OpenApiTransportOptions(
    Uri SpecLocation,
    Uri? BaseAddress = null,
    OpenApiAuthKind AuthKind = OpenApiAuthKind.None,
    string? Credential = null,
    string? ApiKeyHeaderName = null,
    bool AllowPrivateTargets = false);

/// <summary>
/// OpenRPC-Dienst als virtueller Upstream (Roadmap Phase 8, Spike `docs/spikes/openrpc-import.md`).
/// <para>
/// Die Beschreibung kommt entweder aus einem statischen Dokument (<see cref="SpecLocation"/>) oder
/// über den standardisierten Discovery-Aufruf <c>rpc.discover</c> am Endpunkt selbst. Beide Wege
/// werden gleich behandelt, <b>nachdem</b> Ziel, Größe und Schema geprüft sind — der Discovery-Weg
/// ist kein Vertrauensvorschuss.
/// </para>
/// <para>
/// <b>Batch-Requests und Notifications sind in v1 ausdrücklich ausgenommen.</b> Ein Batch bündelt
/// mehrere Aufrufe in einer Nachricht; jeder davon müsste einzeln durch RBAC, Guardrail, Approval
/// und Audit — sonst entstünde ein Weg, an der Governance vorbei mehrere Dinge zu tun. Eine
/// Notification hat definitionsgemäß keine Antwort und passt damit nicht auf einen Tool-Call.
/// </para>
/// </summary>
/// <param name="Endpoint">Die JSON-RPC-Adresse, an die Aufrufe gehen.</param>
/// <param name="SpecLocation">
/// Statisches OpenRPC-Dokument. Ohne Angabe wird <c>rpc.discover</c> am <paramref name="Endpoint"/>
/// versucht.
/// </param>
/// <param name="AllowPrivateTargets">
/// Erlaubt Ziele in privaten, Loopback- oder Link-Local-Netzen. Vorgabe ist <c>false</c>: Ein
/// Gateway, das beliebige URLs abruft, ist sonst ein Werkzeug, um interne Dienste zu erreichen
/// (SSRF). Für einen Dienst im eigenen Netz bewusst einschalten.
/// </param>
public sealed record OpenRpcTransportOptions(
    Uri Endpoint,
    Uri? SpecLocation = null,
    OpenApiAuthKind AuthKind = OpenApiAuthKind.None,
    string? Credential = null,
    string? ApiKeyHeaderName = null,
    bool AllowPrivateTargets = false,
    int TimeoutSeconds = 30);

/// <summary>
/// CLI-Programm als virtueller Upstream (ADR-0014). <see cref="Executable"/> ist pro Upstream fix
/// (implizite Allowlist genau eines Binaries); jedes <see cref="CliToolSpec"/> wird ein Tool.
/// Die Ausführung ist strikt shell-frei (ArgumentList) — Aufrufer-Argumente werden literal hinter
/// die festen Argumente gehängt, nie in eine Shell interpoliert. <see cref="MaxOutputBytes"/>
/// begrenzt die zurückgegebene Ausgabe (Memory-/Kontext-Schutz).
/// </summary>
public sealed record CliTransportOptions(
    string Executable,
    IReadOnlyList<CliToolSpec> Tools,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    int? TimeoutSeconds = null,
    int MaxOutputBytes = 64 * 1024,
    bool AllowPathLookup = false,
    IReadOnlyList<string>? AllowedExecutableRoots = null,
    IReadOnlyList<string>? AllowedWorkingDirectoryRoots = null,
    IReadOnlyList<string>? AllowedReadRoots = null,
    IReadOnlyList<string>? AllowedWriteRoots = null,
    int MaxConcurrency = 4,
    string OutputEncoding = "utf-8",
    string? ExecutableSha256 = null,
    /// <summary>
    /// Wie das Programm ausgeführt wird (ADR-0018). Ohne Angabe gilt der bisherige
    /// <see cref="CliIsolationMode.Host"/>-Modus — bestehende Konfigurationen ändern ihr Verhalten
    /// nicht dadurch, dass es die Option jetzt gibt.
    /// </summary>
    CliIsolationOptions? Isolation = null);

/// <summary>Ausführungsmodus eines CLI-Upstreams (ADR-0018).</summary>
public enum CliIsolationMode
{
    /// <summary>
    /// Direkt im Host-Prozessraum. Gehärtet (absolute Pfade, Root-Allowlist, minimale Umgebung,
    /// Prozessbaum-Kill), aber <b>keine Sandbox</b> — nur für ausdrücklich vertrauenswürdige
    /// Programme.
    /// </summary>
    Host = 0,

    /// <summary>
    /// In einem Container je Aufruf. Der Default für vorhandene, nicht vertrauenswürdige Programme.
    /// </summary>
    Container = 1,
}

/// <summary>
/// Container-Ausführung eines CLI-Upstreams (ADR-0018).
/// <para>
/// Die Mount-Allowlisten kommen aus <see cref="CliTransportOptions.AllowedReadRoots"/> und
/// <see cref="CliTransportOptions.AllowedWriteRoots"/> — dieselben kanonischen Wurzeln, die der
/// Host-Modus schon durchsetzt. Zwei getrennte Listen wären zwei Wahrheiten über dieselbe Frage.
/// </para>
/// <para>
/// <b>Kein stiller Rückfall.</b> Ist der Modus <see cref="CliIsolationMode.Container"/> und keine
/// Container-Runtime erreichbar, kommt der Upstream nicht hoch. Ein Ausweichen auf den Host wäre
/// eine stille Herabstufung der Isolation — genau das verbietet ADR-0018.
/// </para>
/// </summary>
/// <param name="Image">Das Image, in dem das Programm läuft. Pflicht im Container-Modus.</param>
/// <param name="Runtime">Ausführbare Container-Runtime, z. B. <c>docker</c> oder <c>podman</c>.</param>
/// <param name="User">Benutzer im Container; Vorgabe ist ein fester Nicht-root-Benutzer.</param>
/// <param name="MemoryLimitMb">Arbeitsspeicher-Obergrenze in MiB.</param>
/// <param name="CpuLimit">CPU-Anteil, z. B. <c>1.0</c> für einen Kern.</param>
/// <param name="PidLimit">Obergrenze der Prozesse im Container — begrenzt Fork-Bomben.</param>
/// <param name="NetworkAllow">
/// Erlaubte Netzwerkziele. <b>Leer heißt: kein Netzwerk.</b> Nicht andersherum — ein vergessenes
/// Feld darf keinen Netzzugang öffnen.
/// </param>
/// <param name="TmpfsSizeMb">
/// Größe des beschreibbaren <c>/tmp</c>. Das Wurzeldateisystem ist read-only; ohne diesen Bereich
/// scheitern Programme, die Temporärdateien anlegen — mit einer Meldung, die niemand versteht.
/// </param>
public sealed record CliIsolationOptions(
    CliIsolationMode Mode = CliIsolationMode.Host,
    string? Image = null,
    string Runtime = "docker",
    string User = "65532:65532",
    int MemoryLimitMb = 512,
    double CpuLimit = 1.0,
    int PidLimit = 128,
    IReadOnlyList<string>? NetworkAllow = null,
    int TmpfsSizeMb = 64);

/// <summary>
/// Ein benanntes CLI-Kommando = ein Tool. <see cref="FixedArguments"/> stehen fest; ist
/// <see cref="AllowCallerArguments"/> true, hängt der Aufrufer über das Tool-Argument
/// <c>args</c> (string[]) weitere Argumente an — sonst läuft nur das feste Kommando.
/// </summary>
public sealed record CliToolSpec(
    string Name,
    string? Description = null,
    IReadOnlyList<string>? FixedArguments = null,
    bool AllowCallerArguments = false,
    IReadOnlyList<CliParameterSpec>? Parameters = null,
    CapabilityRisk Risk = CapabilityRisk.Read,
    int? MaxConcurrency = null);

public sealed record CliParameterSpec(
    string Name,
    string? Description = null,
    CliParameterType Type = CliParameterType.String,
    int? Position = null,
    string? Flag = null,
    bool Required = false,
    JsonElement? DefaultValue = null,
    IReadOnlyList<string>? AllowedValues = null,
    string? Pattern = null,
    double? Minimum = null,
    double? Maximum = null,
    CliPathAccess PathAccess = CliPathAccess.None,
    bool Repeatable = false,
    bool Sensitive = false,
    int? MaxLength = null,
    IReadOnlyList<string>? ConflictsWith = null,
    IReadOnlyList<string>? Requires = null);

public sealed record RestartPolicy(
    int MaxRetries,
    TimeSpan InitialBackoff,
    double BackoffMultiplier,
    TimeSpan MaxBackoff)
{
    public static RestartPolicy Default { get; } = new(5, TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromMinutes(1));
}

/// <summary>
/// Vollständige Konfiguration eines Upstream-Servers. Genau eines der Transport-Options-Felder
/// muss gesetzt sein und zu <see cref="Kind"/> passen; <see cref="Slug"/> ist die Namespacing-Basis (FR-03).
/// </summary>
public sealed record UpstreamServerConfig(
    string Slug,
    string DisplayName,
    UpstreamTransportKind Kind,
    bool Enabled,
    StdioTransportOptions? Stdio = null,
    HttpTransportOptions? Http = null,
    OpenApiTransportOptions? OpenApi = null,
    RestartPolicy? Restart = null,
    TimeSpan? CallTimeout = null,
    CliTransportOptions? Cli = null,
    WasiTransportOptions? Wasi = null,
    OpenRpcTransportOptions? OpenRpc = null);

/// <summary>
/// Ein signiertes WebAssembly-Component als Upstream (ADR-0020, Plan 0003/WP2). Die Ausführung
/// läuft in einem eigenständigen Rust-Host-Prozess, den das Gateway über einen versionierten
/// IPC-Vertrag ansteuert — .NET kann WASI-P2-Components nicht in-process ausführen.
/// <para>
/// <see cref="ComponentPath"/> und <see cref="SignaturePath"/> zeigen auf die Component-Bytes und
/// die zugehörige detached Ed25519-Signatur. Geladen wird nur, wenn die Signatur zu einem der
/// <see cref="PinnedPublishers"/> passt (Base64, je 32 Byte) — eine leere Liste lädt nichts
/// (fail-closed). <see cref="Grants"/> sind standardmäßig leer: kein Dateisystem, kein Netzwerk,
/// kein Environment, keine Secrets.
/// </para>
/// </summary>
public sealed record WasiTransportOptions(
    string HostExecutable,
    string ComponentPath,
    string SignaturePath,
    IReadOnlyList<string> PinnedPublishers,
    WasiCapabilityGrants? Grants = null,
    WasiExecutionLimits? Limits = null,
    int StartupTimeoutSeconds = 30,
    IReadOnlyList<string>? HostArguments = null,
    /// <summary>
    /// Werte zu den in <c>Grants.Secrets</c> genannten Namen (Plan 0003, WP4). Sie liegen als Teil
    /// der Upstream-Konfiguration DataProtection-verschlüsselt (NFR-04) wie Header und
    /// Credentials, werden in Ausgaben maskiert und erreichen den Host nur beim Laden. Jeder
    /// gewährte Name braucht genau einen Wert — sonst lehnt der Host fail-closed ab.
    /// </summary>
    IReadOnlyDictionary<string, string>? Secrets = null,
    /// <summary>
    /// Verzeichnis für den Platten-Cache kompilierter Components (Plan 0003, WP5). Ohne Angabe
    /// bleibt der Cache prozesslokal, und jeder Host-Start kompiliert neu — bei einem Component von
    /// 1–3 MB sind das 3–7 Sekunden.
    /// <para>
    /// Der Host legt dort einen eigenen MAC-Schlüssel an und signiert jedes Kompilat damit: Ein
    /// Kompilat ist ausführbarer Code, den die Publisher-Signatur <b>nicht</b> abdeckt. Das
    /// Verzeichnis muss dem Host-Benutzer gehören und darf für andere nicht schreibbar sein — der
    /// Schlüssel hebt die Hürde auf „gleicher Benutzer", nicht darüber hinaus. Im Container-Image
    /// eignet sich ein Pfad unter <c>/data</c>.
    /// </para>
    /// </summary>
    string? ModuleCacheDirectory = null,
    /// <summary>
    /// Obergrenze der Belegung des Platten-Caches in Byte. Ohne Angabe gilt die Vorgabe des Hosts
    /// (256 MiB); <c>0</c> heißt ausdrücklich unbegrenzt. Über der Grenze verdrängt der Host die
    /// am längsten nicht genutzten Kompilate — ein verdrängter Eintrag kostet nur eine erneute
    /// Kompilierung. Ohne <see cref="ModuleCacheDirectory"/> hat der Wert keine Wirkung.
    /// </summary>
    long? ModuleCacheMaxBytes = null,
    /// <summary>
    /// Hält die Guest-Instanz über die Aufrufe hinweg am Leben (Plan 0003, Resources). Nötig für
    /// Components, die <c>resource</c>-Handles ausgeben: Ein Handle ist ein Index in die Instanz,
    /// die es ausgegeben hat — mit einer frischen Instanz pro Aufruf wäre es beim nächsten Aufruf
    /// wertlos.
    /// <para>
    /// Voreinstellung <c>false</c>, und das ist die vorsichtigere Wahl. Die Instanz gehört dem
    /// Upstream, die Handles gehören je einem Aufrufer — aber der <b>interne</b> Zustand des
    /// Components (Globals, linearer Speicher) ist ab dann zwischen allen Aufrufern geteilt. Die
    /// Handle-Trennung schützt davor nicht. Nur einschalten, wenn der Upstream Resources braucht.
    /// </para>
    /// </summary>
    bool PersistentInstance = false,
    /// <summary>
    /// Id eines installierten Connector-Pakets (ADR-0016). Ist sie gesetzt, kommen Component und
    /// Signatur aus der <b>aktiven</b> Version dieses Pakets, und <see cref="ComponentPath"/> sowie
    /// <see cref="SignaturePath"/> dürfen leer bleiben.
    /// <para>
    /// Der Sinn: Ein Update wechselt die Dateien, ohne dass jemand die Upstream-Konfiguration
    /// anfasst — und ein Rollback ebenso. Pfade in der Konfiguration wären nach jedem Update falsch,
    /// und jemand müsste daran denken.
    /// </para>
    /// </summary>
    string? PackageId = null);

/// <summary>
/// Die Host-Capabilities, die ein Component erhält — Spiegel des Grant-Modells im Rust-Host.
/// Alles leer/false = default-deny: Der Host linkt nur die gewährten WASI-Interfaces, ein
/// Component mit einem nicht gewährten Import scheitert schon beim Instanziieren (Plan 0003, WP3).
/// <para>
/// <see cref="FilesystemPreopens"/> sind absolute Pfade und werden **lesend** eingehängt; der Host
/// löst sie vorher auf, ein Symlink verschiebt den Grant also nicht. <see cref="NetworkAllow"/>
/// sind <c>host:port</c>-Ziele, die der Host einmalig zu Socket-Adressen auflöst — alles andere
/// wird abgewiesen, Namensauflösung im Guest bleibt aus. <see cref="Secrets"/> nennt die Namen,
/// deren Werte der Host als Environment-Einträge injiziert (WP4); die Werte selbst stehen in
/// <see cref="WasiTransportOptions.Secrets"/>. Wer Secrets gewährt, gewährt damit auch
/// <c>wasi:cli/environment</c> — das Component kann dann alle gesetzten Variablen auflisten.
/// </para>
/// </summary>
public sealed record WasiCapabilityGrants(
    IReadOnlyList<string>? FilesystemPreopens = null,
    IReadOnlyList<string>? NetworkAllow = null,
    IReadOnlyList<string>? Environment = null,
    IReadOnlyList<string>? Secrets = null,
    bool Clock = false,
    bool Random = false);

/// <summary>Ausführungslimits pro Aufruf; werden an den Host durchgereicht.</summary>
public sealed record WasiExecutionLimits(
    ulong? Fuel = 50_000_000,
    ulong? TimeoutMs = 5_000,
    long? MaxMemoryBytes = 64 * 1024 * 1024,
    int MaxOutputBytes = 64 * 1024);

public sealed record ToolDescriptor(
    string Name,
    string? Description,
    JsonElement InputSchema,
    CapabilityRisk Risk = CapabilityRisk.Read,
    bool RequiresApproval = false);

public sealed record ResourceDescriptor(Uri Uri, string Name, string? Description, string? MimeType);

public sealed record PromptDescriptor(string Name, string? Description);

/// <summary>Discovery-Ergebnis eines Upstreams: Tools, Resources und Prompts (FR-04).</summary>
public sealed record UpstreamInventory(
    IReadOnlyList<ToolDescriptor> Tools,
    IReadOnlyList<ResourceDescriptor> Resources,
    IReadOnlyList<PromptDescriptor> Prompts);

public sealed record UpstreamStatus(
    ServerId Id,
    string Slug,
    UpstreamState State,
    string? LastError,
    int ToolCount,
    DateTimeOffset LastHealthyAt,
    /// <summary>
    /// Tools, deren Definition sich gegenüber dem angenommenen Stand geändert hat und die deshalb
    /// zurückgehalten werden (Rug-Pull-Schutz). Sie zählen nicht in <see cref="ToolCount"/> — sie
    /// sind weder sichtbar noch aufrufbar, bis jemand die neue Fassung annimmt.
    /// </summary>
    IReadOnlyList<string>? QuarantinedTools = null);

/// <summary>Drain-Verhalten beim Entfernen/Stoppen unter Last (WP1.4): Gnadenfrist für In-Flight-Calls, danach Cancel.</summary>
public sealed record DrainPolicy(TimeSpan GracePeriod)
{
    public static DrainPolicy Immediate { get; } = new(TimeSpan.Zero);
    public static DrainPolicy Graceful(TimeSpan gracePeriod) => new(gracePeriod);
}

public sealed class UpstreamNotificationEventArgs : EventArgs
{
    public required ServerId Server { get; init; }
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}

public enum UpstreamChangeKind
{
    Added = 0,
    Removed = 1,
    InventoryChanged = 2,
    StateChanged = 3,
}

/// <summary>Signal des Supervisors an den Katalog (WP2) und die UI. Auslöser für tools/list_changed (FR-07).</summary>
public sealed class UpstreamChangedEventArgs : EventArgs
{
    public required ServerId Server { get; init; }
    public required UpstreamChangeKind Kind { get; init; }
    public required UpstreamState State { get; init; }
}

/// <summary>Ein Eintrag der Konfigurations-Historie eines Upstream-Servers (FR-10).</summary>
public sealed record UpstreamConfigVersion(ConfigVersionId Version, UpstreamServerConfig Config, DateTimeOffset SavedAt);

/// <summary>
/// Persistenz-Port für versionierte Upstream-Konfigurationen (FR-10). WP1 liefert einen
/// In-Memory-Stub in Core; die EF-Core-Implementierung kommt mit WP3 (ADR-0007).
/// </summary>
public interface IUpstreamConfigStore
{
    /// <summary>Hängt eine neue Version an (append-only) und liefert deren Versionsnummer.</summary>
    Task<ConfigVersionId> AppendVersionAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);

    Task<UpstreamServerConfig?> GetVersionAsync(ServerId id, ConfigVersionId version, CancellationToken ct);

    /// <summary>Historie aufsteigend nach Version; leer, wenn der Server unbekannt ist.</summary>
    Task<IReadOnlyList<UpstreamConfigVersion>> GetHistoryAsync(ServerId id, CancellationToken ct);

    /// <summary>Jeweils neueste Version aller bekannten Server — Grundlage für den Startup-Restore (WP4.2).</summary>
    Task<IReadOnlyDictionary<ServerId, UpstreamConfigVersion>> GetAllLatestAsync(CancellationToken ct);

    /// <summary>Entfernt die komplette Historie eines Servers (bei endgültigem Remove).</summary>
    Task RemoveAsync(ServerId id, CancellationToken ct);
}

/// <summary>
/// Eindeutige Kennung dieser Gateway-Instanz (FR-05). Wird bei ausgehenden HTTP-MCP-Verbindungen
/// als Header <c>X-Bifrost-Instance</c> mitgeschickt; empfängt der eigene MCP-Endpoint die eigene
/// Kennung, ist das ein direkter Federations-Loop und wird abgewiesen.
/// </summary>
public sealed class GatewayIdentity
{
    public const string InstanceHeader = "X-Bifrost-Instance";

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
}

public sealed record UpstreamTestResult(bool Success, int ToolCount, string? Error);

/// <summary>Testet eine Upstream-Konfiguration transient (Verbindung + Discovery), ohne sie zu registrieren — für "Verbindung testen" in der UI (FR-34).</summary>
public interface IUpstreamConnectionTester
{
    Task<UpstreamTestResult> TestAsync(UpstreamServerConfig config, CancellationToken ct);
}

/// <summary>Fabrik pro Transporttyp. Implementierungen: Stdio, StreamableHttp, OpenApi (ADR-0005/0008).</summary>
public interface IUpstreamConnector
{
    UpstreamTransportKind Kind { get; }

    /// <summary>Baut eine Verbindung auf. <paramref name="id"/> wird vom Supervisor vergeben und identifiziert die Verbindung in Events.</summary>
    Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);
}

/// <summary>
/// Eine aktive Verbindung zu einem Upstream. Kapselt das MCP-SDK vollständig —
/// oberhalb dieses Interfaces existieren keine SDK-Typen (DON'T Nr. 1).
/// </summary>
public interface IUpstreamConnection : IAsyncDisposable
{
    ServerId Id { get; }

    Task<UpstreamInventory> DiscoverAsync(CancellationToken ct);

    Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct);

    /// <summary>Liest eine Resource des Upstreams (FR-04); Ergebnis ist das serialisierte ReadResourceResult.</summary>
    Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct);

    /// <summary>Holt einen Prompt des Upstreams (FR-04); Ergebnis ist das serialisierte GetPromptResult.</summary>
    Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct);

    /// <summary>Health-Probe (MCP ping bzw. server/discover ab Revision 2026-07-28). Wirft bei totem Upstream.</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Notifications von unten (u. a. tools/list_changed des Upstreams selbst).</summary>
    event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived;

    /// <summary>
    /// Meldet dieser Upstream Katalogänderungen von sich aus — oder muss man ihn fragen?
    /// <para>
    /// Bis zur Spec-Revision <c>2025-11-25</c> war die Antwort immer „ja": Ein Server schickte
    /// <c>tools/list_changed</c> über die stehende Sitzung. Die Revision <c>2026-07-28</c> hat
    /// unaufgeforderte Server-zu-Client-Nachrichten gestrichen — bei einem solchen Upstream bleibt
    /// ein neu hinzugekommenes Werkzeug unsichtbar, bis jemand nachfragt. Der Supervisor holt den
    /// Katalog dann von sich aus turnusmäßig nach.
    /// </para>
    /// <para>
    /// Standardmäßig <c>true</c>: Wer diesen Vertrag ohne Protokollbezug erfüllt (OpenAPI, CLI,
    /// WASI), meldet seine Änderungen selbst oder hat keine.
    /// </para>
    /// </summary>
    bool PushesCatalogChanges => true;
}

/// <summary>
/// Besitzt alle Upstream-Lebenszyklen (ADR-0005): Zustandsmaschine, Health-Loop, Restart mit Backoff,
/// Config-Versionierung. Add = Add→Connect→Discover→Katalog-Changed als eine Transaktion (DON'T Nr. 6).
/// </summary>
public interface IUpstreamSupervisor
{
    IReadOnlyList<UpstreamStatus> Statuses { get; }

    /// <summary>Wird bei Add/Remove/Zustands-/Inventarwechsel gefeuert. Handler müssen schnell und exception-frei sein.</summary>
    event EventHandler<UpstreamChangedEventArgs>? Changed;

    UpstreamStatus? GetStatus(ServerId id);

    /// <summary>Letztes Discovery-Ergebnis; null wenn unbekannt oder nie verbunden.</summary>
    UpstreamInventory? GetInventory(ServerId id);

    /// <summary>Aktive (guarded) Verbindung für das Routing; null wenn nicht Healthy/Degraded.</summary>
    IUpstreamConnection? GetConnection(ServerId id);

    Task<ServerId> AddAsync(UpstreamServerConfig config, CancellationToken ct);

    Task RemoveAsync(ServerId id, DrainPolicy drain, CancellationToken ct);

    Task SetEnabledAsync(ServerId id, bool enabled, CancellationToken ct);

    Task<ConfigVersionId> ReconfigureAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);

    Task RollbackAsync(ServerId id, ConfigVersionId version, CancellationToken ct);
}

/// <summary>
/// Zählstand der aktiven MCP-Sessions fürs Dashboard (FR-33). Eigener Vertrag, weil die
/// Session-Verwaltung im Server-Host liegt, die UI aber nur nach unten auf Abstractions zeigt (ADR-0004).
/// <para>
/// <b>Seit der Spec-Revision 2026-07-28 gibt es zwei Betriebsarten</b>, und nur eine davon hat
/// überhaupt Sessions: Der stateless Kern kennt keine <c>Mcp-Session-Id</c> mehr, jede Anfrage steht
/// für sich. Ein Zählstand „offene Sessions" wäre dort eine erfundene Zahl. Deshalb sagt
/// <see cref="CountsOpenSessions"/>, was die Werte bedeuten — die Oberfläche beschriftet danach.
/// </para>
/// </summary>
public interface IActiveSessionSource
{
    /// <summary>
    /// Anzahl offener MCP-Sessions (eine Agenten-Instanz kann mehrere halten). Im stateless Betrieb
    /// gibt es keine offenen Sessions; dann steht hier derselbe Wert wie in
    /// <see cref="ActiveAgents"/>.
    /// </summary>
    int ActiveSessions { get; }

    /// <summary>
    /// Anzahl verschiedener Identitäten mit mindestens einer offenen Session — im stateless Betrieb:
    /// mit mindestens einer Anfrage im jüngsten Zeitfenster.
    /// </summary>
    int ActiveAgents { get; }

    /// <summary>
    /// <c>true</c>, wenn <see cref="ActiveSessions"/> wirklich offene Sessions zählt (stateful
    /// Betrieb). <c>false</c> im stateless Betrieb: Dann sind beide Werte „wer war zuletzt da".
    /// </summary>
    bool CountsOpenSessions { get; }
}
