namespace McpMcp.Abstractions;

/// <summary>
/// Vertrauensstufe eines Connector-Herausgebers (ADR-0016). Sie entscheidet, <b>wie viel</b> ein
/// Paket ohne ausdrückliche Zustimmung des Administrators verlangen darf — nicht, ob es läuft.
/// <para>
/// Die Reihenfolge ist absichtlich absteigend nach Vertrauen: Ein Vergleich <c>&gt;</c> liest sich
/// als „weniger vertrauenswürdig als".
/// </para>
/// </summary>
public enum ConnectorTrustLevel
{
    /// <summary>
    /// Mit dem Produkt ausgeliefert und gleich versioniert. <b>Nicht installierbar</b>: Ein Paket,
    /// das diese Stufe für sich beansprucht, würde behaupten, Teil des Gateways zu sein.
    /// </summary>
    Core = 0,

    /// <summary>Offizielles, signiertes Paket. Die Anforderungen des Manifests gelten wie deklariert.</summary>
    Official = 1,

    /// <summary>
    /// Erlaubter Herausgeber. Zugriffe nach außen (Dateisystem, Netz, Environment, Secrets) sind
    /// keine Selbstbedienung: Sie brauchen beim Installieren eine ausdrückliche Zustimmung.
    /// </summary>
    ThirdParty = 2,

    /// <summary>
    /// Nicht eingestuft. Zusätzlich zur Zustimmung je Anforderung braucht die Installation eine
    /// ausdrückliche Freigabe des Pakets selbst — deny-by-default.
    /// </summary>
    Community = 3,
}

/// <summary>
/// Was ein Connector an Host-Zugriffen verlangt. Rein deklarativ: Das Manifest <em>bittet</em>,
/// durchgesetzt wird an der Laufzeitgrenze (WASI-Grants, ADR-0017/0020).
/// </summary>
public sealed record ConnectorGrantRequest(
    IReadOnlyList<string>? FilesystemRead = null,
    IReadOnlyList<string>? FilesystemWrite = null,
    IReadOnlyList<string>? Network = null,
    IReadOnlyList<string>? Environment = null,
    IReadOnlyList<string>? Secrets = null)
{
    public static ConnectorGrantRequest None { get; } = new();

    /// <summary>Alle Anforderungen als „Bereich: Wert"-Paare — für Anzeige, Zustimmung und Audit.</summary>
    public IReadOnlyList<string> Enumerate() =>
    [
        .. (FilesystemRead ?? []).Select(v => $"fs-read:{v}"),
        .. (FilesystemWrite ?? []).Select(v => $"fs-write:{v}"),
        .. (Network ?? []).Select(v => $"network:{v}"),
        .. (Environment ?? []).Select(v => $"env:{v}"),
        .. (Secrets ?? []).Select(v => $"secret:{v}"),
    ];

    public bool IsEmpty => Enumerate().Count == 0;
}

/// <summary>Eine Datei im Paket mit ihrem erwarteten Hash. Nur Deklariertes wird ausgepackt.</summary>
public sealed record ConnectorPayload(string Path, string Sha256);

/// <summary>
/// Das signierte Manifest eines Connector-Pakets (ADR-0016, <c>mcpmcp.connector.v1</c>).
/// <para>
/// Signiert werden <b>genau diese Bytes</b>, und das Manifest nennt den SHA-256 jeder Nutzdatei.
/// Damit deckt eine Signatur das ganze Paket ab, ohne dass das Archivformat selbst signiert werden
/// müsste — Archive sind formbar (Reihenfolge, Kommentare, Duplikate), eine Hash-Liste ist es nicht.
/// </para>
/// </summary>
public sealed record ConnectorManifest(
    string Schema,
    string Id,
    string Version,
    string ContractVersion,
    string PublisherKeyId,
    string DisplayName,
    UpstreamTransportKind Transport,
    string EntryPoint,
    string SignaturePath,
    IReadOnlyList<ConnectorPayload> Payloads,
    ConnectorGrantRequest? Grants = null,
    IReadOnlyList<string>? Platforms = null,
    string? Description = null)
{
    /// <summary>Der einzige Schemawert, den v1 kennt.</summary>
    public const string SchemaV1 = "mcpmcp.connector.v1";

    /// <summary>Die Vertragsversion, die dieses Gateway sprechen kann.</summary>
    public const string SupportedContractVersion = "1";

    public ConnectorGrantRequest GrantsOrNone => Grants ?? ConnectorGrantRequest.None;
}

/// <summary>Zustand einer installierten Paketversion.</summary>
public enum PackageState
{
    /// <summary>Geprüft und ausgepackt, aber noch nicht in Betrieb — die Probe läuft.</summary>
    Quarantined = 0,

    /// <summary>Die Version, die Upstreams dieses Connectors verwenden.</summary>
    Active = 1,

    /// <summary>Vorherige Version. Bleibt liegen, damit ein Rollback ohne erneuten Download geht.</summary>
    Superseded = 2,

    /// <summary>Die Probe ist gescheitert. Bleibt als Beleg stehen, wird aber nie aktiv.</summary>
    Failed = 3,
}

/// <summary>Eine installierte Paketversion, wie sie im Store steht.</summary>
public sealed record InstalledConnectorPackage(
    string PackageId,
    string Version,
    string DisplayName,
    UpstreamTransportKind Transport,
    string PublisherKeyId,
    ConnectorTrustLevel TrustLevel,
    string ManifestSha256,
    string Directory,
    PackageState State,
    DateTimeOffset InstalledAt,
    DateTimeOffset? ActivatedAt,
    IReadOnlyList<string> GrantedCapabilities,
    string? FailureReason = null);

/// <summary>Persistenz der installierten Pakete. Die Dateien liegen daneben auf der Platte.</summary>
public interface IConnectorPackageStore
{
    Task<IReadOnlyList<InstalledConnectorPackage>> ListAsync(CancellationToken ct);

    Task<InstalledConnectorPackage?> GetActiveAsync(string packageId, CancellationToken ct);

    Task<IReadOnlyList<InstalledConnectorPackage>> GetVersionsAsync(string packageId, CancellationToken ct);

    Task UpsertAsync(InstalledConnectorPackage package, CancellationToken ct);

    /// <summary>
    /// Macht <paramref name="version"/> zur aktiven Version und stuft die bisherige auf
    /// <see cref="PackageState.Superseded"/> zurück — in <b>einer</b> Transaktion. Zwei aktive
    /// Versionen desselben Pakets wären ein Zustand, den kein Aufrufer auflösen kann.
    /// </summary>
    Task ActivateAsync(string packageId, string version, DateTimeOffset at, CancellationToken ct);

    Task RemoveAsync(string packageId, string version, CancellationToken ct);
}

/// <summary>
/// Löst eine Paket-Id in die Dateien der <b>aktiven</b> Version auf. Damit kann eine
/// Upstream-Konfiguration auf ein Paket zeigen statt auf Pfade — ein Update wechselt dann die
/// Dateien, ohne dass jemand die Konfiguration anfasst.
/// </summary>
public interface IConnectorPackageResolver
{
    /// <summary>
    /// Absolute Pfade zu Entry Point und zugehöriger Signatur, oder <c>null</c>, wenn es keine
    /// aktive Version gibt.
    /// </summary>
    (string EntryPoint, string SignaturePath)? ResolveActive(string packageId);
}

/// <summary>Ein Paket wurde abgewiesen. Die Meldung nennt den Grund, nicht nur den Fehlschlag.</summary>
public sealed class ConnectorPackageException : Exception
{
    public ConnectorPackageException(string message) : base(message) { }

    public ConnectorPackageException() { }

    public ConnectorPackageException(string message, Exception innerException)
        : base(message, innerException) { }
}
