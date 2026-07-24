using System.Text.Json;

namespace McpMcp.Abstractions;

public enum UpstreamTransportKind
{
    Stdio = 0,
    StreamableHttp = 1,
    OpenApi = 2,
    Cli = 3,
    Wasi = 4,
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
    bool AllowLegacySse = true);

/// <summary>
/// OpenAPI-Quelle als virtueller Upstream (FR-19). <see cref="Credential"/> liegt im Config-Blob,
/// der als Ganzes DataProtection-verschlüsselt persistiert wird (NFR-04, ADR-0007).
/// Bearer: Credential = Token; Basic: Credential = "user:pass"; ApiKeyHeader: Credential = Key,
/// Header-Name über <see cref="ApiKeyHeaderName"/> (Default X-Api-Key).
/// </summary>
public sealed record OpenApiTransportOptions(
    Uri SpecLocation,
    Uri? BaseAddress = null,
    OpenApiAuthKind AuthKind = OpenApiAuthKind.None,
    string? Credential = null,
    string? ApiKeyHeaderName = null);

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
    string? ExecutableSha256 = null);

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
    WasiTransportOptions? Wasi = null);

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
    IReadOnlyList<string>? HostArguments = null);

/// <summary>
/// Die Host-Capabilities, die ein Component erhält — Spiegel des Grant-Modells im Rust-Host.
/// Alles leer/false = default-deny: Der Host linkt nur die gewährten WASI-Interfaces, ein
/// Component mit einem nicht gewährten Import scheitert schon beim Instanziieren (Plan 0003, WP3).
/// <para>
/// <see cref="FilesystemPreopens"/> sind absolute Pfade und werden **lesend** eingehängt; der Host
/// löst sie vorher auf, ein Symlink verschiebt den Grant also nicht. <see cref="NetworkAllow"/>
/// sind <c>host:port</c>-Ziele, die der Host einmalig zu Socket-Adressen auflöst — alles andere
/// wird abgewiesen, Namensauflösung im Guest bleibt aus. <see cref="Secrets"/> ist noch nicht
/// implementiert: Der Host injiziert keine Secrets, deshalb weist der Validator einen solchen
/// Grant ab, statt ihn wirkungslos durchzulassen (kommt mit dem Trust-Store, WP4).
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
    DateTimeOffset LastHealthyAt);

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
/// als Header <c>X-McpMcp-Instance</c> mitgeschickt; empfängt der eigene MCP-Endpoint die eigene
/// Kennung, ist das ein direkter Federations-Loop und wird abgewiesen.
/// </summary>
public sealed class GatewayIdentity
{
    public const string InstanceHeader = "X-McpMcp-Instance";

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

    /// <summary>Health-Probe (MCP ping). Wirft bei totem Upstream.</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Notifications von unten (u. a. tools/list_changed des Upstreams selbst).</summary>
    event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived;
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
/// </summary>
public interface IActiveSessionSource
{
    /// <summary>Anzahl offener MCP-Sessions (eine Agenten-Instanz kann mehrere halten).</summary>
    int ActiveSessions { get; }

    /// <summary>Anzahl verschiedener Identitäten mit mindestens einer offenen Session.</summary>
    int ActiveAgents { get; }
}
