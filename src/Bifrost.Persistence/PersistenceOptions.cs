namespace Bifrost.Persistence;

public enum AuditDeliveryMode
{
    BestEffort = 0,
    Compliance = 1,
}

/// <summary>Betriebsparameter der Persistenz (ADR-0007).</summary>
public sealed record PersistenceOptions
{
    /// <summary>Maximale Wartezeit, bis ein angefangener Audit-Batch geschrieben wird (Crash-Verlustfenster).</summary>
    public TimeSpan AuditFlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int AuditMaxBatchSize { get; init; } = 500;

    /// <summary>Aufbewahrung der Audit-Ereignisse (FR-25). Retention ist bei SQLite Betriebspflicht (ADR-0007).</summary>
    public TimeSpan AuditRetention { get; init; } = TimeSpan.FromDays(30);

    public int AuditChannelCapacity { get; init; } = 100_000;

    public AuditDeliveryMode AuditMode { get; init; } = AuditDeliveryMode.BestEffort;

    public TimeSpan AuditRetryInterval { get; init; } = TimeSpan.FromSeconds(1);
}
