using Bifrost.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Persistence;

/// <summary>
/// Setzt fällige Vorgänge auf <see cref="TaskState.Expired"/> (ADR-0019).
/// <para>
/// Ohne diesen Lauf bliebe ein Vorgang, den niemand abschließt, für immer offen: in der Liste
/// sichtbar, im Zustand <c>working</c> — und im Fall einer Freigabe theoretisch weiter einlösbar.
/// Der Consume-Pfad prüft die Frist selbst, dieser Lauf macht den Verfall nur **sichtbar** statt
/// bloß wirksam. Ein Betreiber soll in der Liste erkennen können, warum nichts passiert ist.
/// </para>
/// <para>
/// Bewusst kein Löschen: Ein abgelaufener Vorgang bleibt als Terminalzustand stehen und ist damit
/// auditierbar. Aufräumen ist eine Retention-Frage und gehört nicht hierher.
/// </para>
/// </summary>
public sealed partial class TaskExpiryJob
{
    /// <summary>
    /// Wie oft geprüft wird. Fünf Minuten sind grob genug, um billig zu sein, und fein genug, dass
    /// eine Stunde Freigabe-Frist nicht wesentlich überzogen erscheint.
    /// </summary>
    private static readonly TimeSpan RunInterval = TimeSpan.FromMinutes(5);

    private readonly ITaskStore _tasks;
    private readonly TimeProvider _time;
    private readonly ILogger<TaskExpiryJob> _logger;

    public TaskExpiryJob(
        ITaskStore tasks,
        TimeProvider? timeProvider = null,
        ILogger<TaskExpiryJob>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        _tasks = tasks;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<TaskExpiryJob>.Instance;
    }

    /// <summary>Ein Durchlauf; liefert die Anzahl der auf <c>expired</c> gesetzten Vorgänge.</summary>
    public async Task<int> ExecuteOnceAsync(CancellationToken ct)
    {
        var expired = await _tasks.ExpireDueAsync(_time.GetUtcNow(), ct).ConfigureAwait(false);
        if (expired > 0)
        {
            Log.Expired(_logger, expired);
        }

        return expired;
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
            Message = "{Count} Vorgänge waren überfällig und stehen jetzt auf 'expired'.")]
        public static partial void Expired(ILogger logger, int count);
    }
}
