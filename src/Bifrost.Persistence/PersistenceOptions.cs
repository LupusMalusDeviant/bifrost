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

    /// <summary>
    /// Wie oft ein Audit-Batch im Modus <see cref="AuditDeliveryMode.BestEffort"/> geschrieben wird,
    /// bevor er aufgegeben — und gezählt — wird. Im Modus
    /// <see cref="AuditDeliveryMode.Compliance"/> ohne Bedeutung: Dort wird unbegrenzt wiederholt.
    /// <para>
    /// Der Wert ist bewusst klein. Er deckt den häufigen Fall ab (ein Schreib-Lock, den ein anderer
    /// gerade hält) und nicht den seltenen (eine dauerhaft kaputte Datenbank) — für den ist
    /// unbegrenztes Wiederholen die falsche Antwort, weil der Channel dann vollläuft und der Verlust
    /// nur an eine andere Stelle wandert.
    /// </para>
    /// </summary>
    public int AuditMaxWriteAttempts { get; init; } = 3;
}
