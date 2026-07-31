using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Persistence.Audit;

/// <summary>Automatische Bereinigung alter Audit-Ereignisse (FR-25).</summary>
public sealed partial class AuditRetentionJob
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly PersistenceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditRetentionJob> _logger;

    public AuditRetentionJob(
        IDbContextFactory<BifrostDbContext> factory,
        PersistenceOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<AuditRetentionJob>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _options = options ?? new PersistenceOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AuditRetentionJob>.Instance;
    }

    /// <summary>Löscht alle Ereignisse, die älter als die Retention sind. Liefert die Anzahl gelöschter Zeilen.</summary>
    public async Task<int> ExecuteOnceAsync(CancellationToken ct)
    {
        var cutoff = _time.GetUtcNow() - _options.AuditRetention;
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var deleted = await db.AuditEvents
            .Where(r => r.Timestamp < cutoff)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            Log.Purged(_logger, deleted, cutoff);
        }

        return deleted;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RunInterval, _time);
        try
        {
            do
            {
                await ExecuteOnceAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normales Ende
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Audit-Retention: {Count} Ereignisse älter als {Cutoff} gelöscht.")]
        public static partial void Purged(ILogger logger, int count, DateTimeOffset cutoff);
    }
}
