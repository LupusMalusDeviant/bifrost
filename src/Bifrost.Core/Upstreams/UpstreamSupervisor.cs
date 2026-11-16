using System.Collections.Concurrent;
using System.Globalization;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Core.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bifrost.Core.Upstreams;

/// <summary>
/// Besitzt alle Upstream-Lebenszyklen (ADR-0005): pro Server ein Run-Loop mit
/// Connect → Discover → Healthy → Health-Ping-Schleife; bei Verlust Failed → Frist → neuer Versuch.
/// <para>
/// <b>Aus Failed führt immer ein Weg heraus</b> (Vertrag „Selbsttätige Wiederverbindung", Z1):
/// Solange ein Upstream aktiviert ist, steht ein Versuch aus oder einer läuft. Bis 0.12.0 endete
/// die Schleife nach sechs Versuchen in rund 31 Sekunden — ein Deploy, der länger dauerte, ließ
/// den Upstream liegen, bis jemand ihn von Hand aus- und wieder einschaltete. Eine Obergrenze gibt
/// es nur noch, wenn ein Betreiber sie ausdrücklich gesetzt hat, und dann steht sie im Status.
/// </para>
/// Alle Zeitläufe laufen über <see cref="TimeProvider"/> (testbar mit FakeTimeProvider).
/// </summary>
public sealed partial class UpstreamSupervisor : IUpstreamSupervisor, IAsyncDisposable
{
    private readonly Dictionary<UpstreamTransportKind, IUpstreamConnector> _connectors;
    private readonly IUpstreamConfigStore _store;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<UpstreamSupervisor> _logger;
    private readonly IAuditSink? _audit;
    private readonly IToolDefinitionPinStore? _pins;
    private readonly IHostExecutionPolicy _hostExecution;
    private readonly ConcurrentDictionary<ServerId, Entry> _entries = new();

    /// <param name="hostExecution">
    /// Die Ausführungs-Policy (ADR-0025). <c>null</c> heißt <b>nicht</b> „keine Prüfung", sondern
    /// „nicht ermittelt" — dann kommt kein nativ laufender Upstream hoch. Ein vergessener
    /// Verdrahtungsschritt fällt so auf, statt eine Lücke zu öffnen.
    /// </param>
    public UpstreamSupervisor(
        IEnumerable<IUpstreamConnector> connectors,
        IUpstreamConfigStore store,
        SupervisorOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<UpstreamSupervisor>? logger = null,
        IAuditSink? audit = null,
        IToolDefinitionPinStore? pins = null,
        IHostExecutionPolicy? hostExecution = null)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(store);
        _hostExecution = hostExecution ?? HostExecutionPolicy.Unresolved;
        _connectors = connectors.ToDictionary(c => c.Kind);
        _store = store;
        _options = options ?? new SupervisorOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<UpstreamSupervisor>.Instance;
        _audit = audit;
        _pins = pins;
    }

    public event EventHandler<UpstreamChangedEventArgs>? Changed;

    public IReadOnlyList<UpstreamStatus> Statuses => [.. _entries.Values.Select(e => e.ToStatus())];

    public UpstreamStatus? GetStatus(ServerId id) => _entries.TryGetValue(id, out var entry) ? entry.ToStatus() : null;

    public UpstreamInventory? GetInventory(ServerId id) => _entries.TryGetValue(id, out var entry) ? entry.Inventory : null;

    public IUpstreamConnection? GetConnection(ServerId id) => _entries.TryGetValue(id, out var entry) ? entry.Connection : null;

    [HostExecutionChecked]
    public async Task<ServerId> AddAsync(UpstreamServerConfig config, CancellationToken ct)
    {
        UpstreamConfigValidator.Validate(config, _hostExecution);
        EnsureSlugUnique(config.Slug, exclude: null);

        var id = ServerId.New();
        var version = await _store.AppendVersionAsync(id, config, ct).ConfigureAwait(false);
        var entry = new Entry(id,config, version)
        {
            State = config.Enabled ? UpstreamState.Starting : UpstreamState.Stopped,
        };

        if (!_entries.TryAdd(id, entry))
        {
            throw new InvalidOperationException($"ServerId-Kollision für '{config.Slug}' — sollte nie passieren.");
        }

        Raise(entry, UpstreamChangeKind.Added);
        if (config.Enabled)
        {
            StartLoop(entry);
        }

        return id;
    }

    /// <summary>
    /// Registriert einen persistierten Server unter seiner bestehenden Id neu (Startup-Restore, WP4.2).
    /// Erzeugt keine neue Config-Version — die Historie lebt bereits im Store.
    /// </summary>
    [HostExecutionChecked]
    public Task RestoreAsync(ServerId id, UpstreamConfigVersion persisted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        UpstreamConfigValidator.Validate(persisted.Config, _hostExecution);
        EnsureSlugUnique(persisted.Config.Slug, exclude: id);

        var entry = new Entry(id,persisted.Config, persisted.Version)
        {
            State = persisted.Config.Enabled ? UpstreamState.Starting : UpstreamState.Stopped,
        };

        if (!_entries.TryAdd(id, entry))
        {
            throw new InvalidOperationException($"Server {id} ist bereits registriert — Restore doppelt aufgerufen?");
        }

        Raise(entry, UpstreamChangeKind.Added);
        if (persisted.Config.Enabled)
        {
            StartLoop(entry);
        }

        return Task.CompletedTask;
    }

    public async Task RemoveAsync(ServerId id, DrainPolicy drain, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drain);
        var entry = GetEntryOrThrow(id);
        await entry.AdminLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopLoopAsync(entry, drain, ct).ConfigureAwait(false);
            _entries.TryRemove(id, out _);
            await _store.RemoveAsync(id, ct).ConfigureAwait(false);

            // Mit dem Upstream gehen auch seine festgehaltenen Tool-Definitionen. Sonst bleiben
            // Pins auf einen Server zurück, den es nicht mehr gibt — sie wachsen mit jedem
            // Hinzufügen und Entfernen und stehen für immer in der Verwaltungsansicht.
            if (_pins is not null)
            {
                await _pins.ForgetServerAsync(id, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            entry.AdminLock.Release();
        }

        Raise(entry, UpstreamChangeKind.Removed);
    }

    public async Task SetEnabledAsync(ServerId id, bool enabled, CancellationToken ct)
    {
        var entry = GetEntryOrThrow(id);
        await entry.AdminLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Config.Enabled == enabled)
            {
                return;
            }

            var newConfig = entry.Config with { Enabled = enabled };
            entry.Version = await _store.AppendVersionAsync(id, newConfig, ct).ConfigureAwait(false);
            entry.Config = newConfig;

            if (enabled)
            {
                SetState(entry, UpstreamState.Starting, null);
                StartLoop(entry);
            }
            else
            {
                await StopLoopAsync(entry, DrainPolicy.Graceful(_options.DefaultDrainGrace), ct).ConfigureAwait(false);
                SetState(entry, UpstreamState.Stopped, null);
                Raise(entry, UpstreamChangeKind.InventoryChanged);
            }
        }
        finally
        {
            entry.AdminLock.Release();
        }
    }

    [HostExecutionChecked]
    public async Task<ConfigVersionId> ReconfigureAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        UpstreamConfigValidator.Validate(config, _hostExecution);
        EnsureSlugUnique(config.Slug, exclude: id);

        var entry = GetEntryOrThrow(id);
        await entry.AdminLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReconfigureCoreAsync(entry, config, ct).ConfigureAwait(false);
        }
        finally
        {
            entry.AdminLock.Release();
        }
    }

    public async Task RollbackAsync(ServerId id, ConfigVersionId version, CancellationToken ct)
    {
        var entry = GetEntryOrThrow(id);
        var config = await _store.GetVersionAsync(id, version, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Config-Version {version} für Server {id} existiert nicht.");

        await entry.AdminLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReconfigureCoreAsync(entry, config, ct).ConfigureAwait(false);
        }
        finally
        {
            entry.AdminLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<bool> ConnectNowAsync(ServerId id, CancellationToken ct)
    {
        var entry = GetEntryOrThrow(id);
        lock (entry.Gate)
        {
            // Z2: Deaktiviert bleibt aus. Ein Knopf, der einen ausgeschalteten Upstream doch
            // verbindet, wäre ein zweiter Schalter neben dem ersten — und der zweite stünde
            // nicht in der Versionshistorie.
            if (!entry.Config.Enabled || entry.State == UpstreamState.Stopped)
            {
                throw new InvalidOperationException(
                    $"Upstream '{entry.Config.Slug}' ist deaktiviert und wird nicht verbunden. Erst aktivieren.");
            }

            // Kein wartender Loop heißt: verbunden, oder ein Versuch läuft bereits.
            if (entry.State != UpstreamState.Failed || entry.Wake is not { } wake)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(wake.TrySetResult());
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values)
        {
            await entry.AdminLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await StopLoopAsync(entry, DrainPolicy.Immediate, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                entry.AdminLock.Release();
            }
        }

        _entries.Clear();
    }

    /// <summary>
    /// Der gemeinsame innere Weg von Reconfigure und Rollback. Die Policy wird <b>hier</b> gefragt,
    /// nicht nur in <see cref="ReconfigureAsync"/>: Ein Rollback holt seine Konfiguration aus der
    /// Historie und käme sonst an jeder Prüfung vorbei — eine Version von vor der Umstellung ist
    /// genau der Fall, in dem ein nativ laufendes Programm zurückkehrt.
    /// </summary>
    [HostExecutionChecked]
    private async Task<ConfigVersionId> ReconfigureCoreAsync(Entry entry, UpstreamServerConfig config, CancellationToken ct)
    {
        HostExecutionGuard.Ensure(_hostExecution, config);

        await StopLoopAsync(entry, DrainPolicy.Graceful(_options.DefaultDrainGrace), ct).ConfigureAwait(false);
        entry.Version = await _store.AppendVersionAsync(entry.Id, config, ct).ConfigureAwait(false);
        entry.Config = config;
        Raise(entry, UpstreamChangeKind.InventoryChanged);

        if (config.Enabled)
        {
            SetState(entry, UpstreamState.Starting, null);
            StartLoop(entry);
        }
        else
        {
            SetState(entry, UpstreamState.Stopped, null);
        }

        return entry.Version;
    }

    private void StartLoop(Entry entry)
    {
        // Jeder neu gestartete Loop beginnt die Fristenfolge von vorn: Anlegen, Aktivieren und
        // Umkonfigurieren sind Eingriffe eines Menschen, kein weiterer Fehlversuch.
        lock (entry.Gate)
        {
            entry.Attempt = 0;
            entry.FailureKind = UpstreamFailureKind.None;
            entry.NextAttemptAt = null;
            entry.Exhausted = false;
            entry.FailedSince = null;
        }

        var cts = new CancellationTokenSource();
        entry.LoopCts = cts;
        entry.LoopTask = Task.Run(() => RunLoopAsync(entry, cts.Token), CancellationToken.None);
    }

    private async Task StopLoopAsync(Entry entry, DrainPolicy drain, CancellationToken ct)
    {
        var cts = entry.LoopCts;
        var loop = entry.LoopTask;
        if (cts is null)
        {
            return;
        }

        if (entry.Connection is { } connection && drain.GracePeriod > TimeSpan.Zero)
        {
            var idle = await connection.WaitForIdleAsync(drain.GracePeriod, ct).ConfigureAwait(false);
            if (!idle)
            {
                Log.DrainExpired(_logger, entry.Config.Slug, drain.GracePeriod, connection.InFlightCount);
            }
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // erwartetes Loop-Ende
            }
        }

        await CleanupConnectionAsync(entry).ConfigureAwait(false);
        cts.Dispose();
        entry.LoopCts = null;
        entry.LoopTask = null;
    }

    /// <summary>
    /// Der Lebenszyklus eines Upstreams: verbinden, bedienen, bei Verlust nach einer Frist erneut
    /// versuchen — ohne Ende, solange der Upstream aktiviert ist.
    /// <para>
    /// Die Policy wird vor <b>jedem</b> Verbindungsaufbau gefragt (<see cref="EstablishAsync"/>) und
    /// nicht nur einmal beim Anlegen. Diese Schleife läuft, solange der Prozess läuft, und baut
    /// nach jedem Verlust neu auf — eine Prüfung, die nur beim Anlegen stattfand, wäre bei jedem
    /// Neuaufbau danach abwesend.
    /// </para>
    /// </summary>
    [HostExecutionChecked]
    private async Task RunLoopAsync(Entry entry, CancellationToken ct)
    {
        var policy = entry.Config.Restart ?? _options.DefaultRestartPolicy;
        var firstAttempt = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Exception failure;
                try
                {
                    if (firstAttempt)
                    {
                        SetState(entry, UpstreamState.Starting, null);
                    }
                    else
                    {
                        // Z3: Eine Wiederholung ist kein neuer Zustand. Der Upstream bleibt Failed,
                        // bis er verbunden ist — sonst schriebe jeder Versuch zwei Audit-Zeilen,
                        // und ein toter Upstream füllte das Log.
                        MarkAttemptRunning(entry);
                    }

                    firstAttempt = false;
                    var session = await EstablishAsync(entry, _options.ConnectAttemptTimeout, ct).ConfigureAwait(false);
                    Publish(entry, session, afterSessionLoss: false);
                    await ServeAsync(entry, session.Connection, ct).ConfigureAwait(false);
                    continue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                await CleanupConnectionAsync(entry).ConfigureAwait(false);

                int attempt;
                lock (entry.Gate)
                {
                    attempt = ++entry.Attempt;
                }

                var kind = UpstreamFailure.Classify(failure);
                var reason = UpstreamFailure.Describe(failure, entry.Config);
                var exhausted = policy.MaxRetries is { } max && attempt > max;
                TimeSpan? delay = exhausted ? null : ComputeDelay(policy, kind, attempt);

                MarkFailed(entry, kind, reason, attempt, delay, failure);

                try
                {
                    // Bei erreichter Obergrenze wartet der Loop weiter — auf „Jetzt verbinden".
                    // Er endet nicht: Ein beendeter Loop ist der Zustand, aus dem nur ein
                    // Aus- und Einschalten herausführte.
                    await WaitForNextAttemptAsync(entry, delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            AbortPendingRebuild(entry);
        }
    }

    /// <summary>
    /// Ein Verbindungsaufbau, wie er beim ersten Anschluss und bei jedem späteren läuft (Z6):
    /// verbinden, Katalog holen, jede Werkzeugdefinition gegen den festgehaltenen Stand prüfen.
    /// Der ganze Vorgang hat <b>eine</b> Frist (Z4).
    /// </summary>
    [HostExecutionChecked]
    private async Task<Session> EstablishAsync(Entry entry, TimeSpan timeout, CancellationToken ct)
    {
        HostExecutionGuard.Ensure(_hostExecution, entry.Config);
        var connector = _connectors.TryGetValue(entry.Config.Kind, out var c)
            ? c
            : throw new InvalidOperationException($"Kein Connector für Transport {entry.Config.Kind} registriert.");

        using var deadline = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        GuardedUpstreamConnection? guarded = null;
        try
        {
            var connect = connector.ConnectAsync(entry.Id, entry.Config, linked.Token);
            IUpstreamConnection inner;
            try
            {
                // WaitAsync statt bloßem await: Ein Connector, der sein Abbruchsignal ignoriert,
                // hielte den Loop sonst für immer fest. So wird der Versuch verlassen, und was der
                // Connector später doch noch liefert, wird aufgeräumt.
                inner = await connect.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _ = DisposeLateConnectionAsync(connect);
                throw;
            }

            guarded = new GuardedUpstreamConnection(
                inner, entry.Config.CallTimeout ?? _options.DefaultCallTimeout, _time, entry.Recovery);

            var inventory = await guarded.DiscoverAsync(linked.Token).ConfigureAwait(false);

            // Rug-Pull-Erkennung: Was der Upstream jetzt meldet, wird gegen den festgehaltenen
            // Stand geprüft. Geänderte Tools erreichen den Katalog gar nicht erst — Filtern an
            // der Quelle statt einer zweiten Prüfung im Aufrufpfad.
            var (screened, quarantined) = await ScreenInventoryAsync(entry, inventory, linked.Token)
                .ConfigureAwait(false);
            return new Session(guarded, screened, quarantined, inventory.Tools.Count);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (guarded is not null)
            {
                await DisposeQuietlyAsync(guarded).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Der Verbindungsversuch überschritt die Frist von {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.");
        }
        catch
        {
            if (guarded is not null)
            {
                await DisposeQuietlyAsync(guarded).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Räumt eine Verbindung auf, die erst nach Ablauf der Frist doch noch zustande kam.</summary>
    private static async Task DisposeLateConnectionAsync(Task<IUpstreamConnection> connect)
    {
        try
        {
            var late = await connect.ConfigureAwait(false);
            await late.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Der Versuch ist längst als gescheitert verbucht; was hier noch kommt, ändert nichts.
        }
    }

    /// <summary>Macht eine aufgebaute Verbindung zur aktuellen und meldet den Wechsel.</summary>
    private void Publish(Entry entry, Session session, bool afterSessionLoss)
    {
        var now = _time.GetUtcNow();
        bool inventoryChanged;
        DateTimeOffset? failedSince;
        int attempts;
        lock (entry.Gate)
        {
            inventoryChanged = entry.Inventory is null || Signature(entry.Inventory) != Signature(session.Inventory);
            entry.Connection = session.Connection;
            entry.Inventory = session.Inventory;
            entry.QuarantinedTools = session.Quarantined;
            entry.ToolCount = session.Inventory.Tools.Count;
            entry.LastHealthyAt = now;
            entry.RebuildRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            failedSince = entry.FailedSince;
            entry.FailedSince = null;
            attempts = entry.Attempt;
        }

        if (failedSince is { } since)
        {
            // Der eine Eintrag, der sagt, dass ein Ausfall vorbei ist — mit dem, was man danach
            // wissen will: wie lange, wie viele Versuche, und ob die Werkzeuge wieder da sind.
            var downtime = FormatDuration(now - since);
            SetState(entry, UpstreamState.Healthy, null,
                $"{entry.Config.Slug}: {UpstreamState.Healthy} — wieder verbunden nach {downtime} und "
                + $"{attempts} Fehlversuchen, {session.Inventory.Tools.Count} Werkzeuge.");
            Log.UpstreamReconnected(_logger, entry.Config.Slug, downtime, attempts, session.Inventory.Tools.Count);
        }
        else
        {
            SetState(entry, UpstreamState.Healthy, null);
            if (afterSessionLoss)
            {
                Log.UpstreamSessionRebuilt(_logger, entry.Config.Slug, session.Inventory.Tools.Count);
            }
            else
            {
                Log.UpstreamHealthy(_logger, entry.Config.Slug, session.ReportedToolCount);
            }
        }

        // Nach einem Neuaufbau der Sitzung nur melden, wenn sich wirklich etwas geändert hat:
        // Sonst liefe bei jedem Neustart des Upstreams ein tools/list_changed an jede Sitzung.
        if (!afterSessionLoss || inventoryChanged)
        {
            Raise(entry, UpstreamChangeKind.InventoryChanged);
        }
    }

    /// <summary>
    /// Bedient eine stehende Verbindung, bis sie verloren geht. Kehrt nie regulär zurück: Entweder
    /// der Loop wird abgebrochen, oder die Methode wirft den Verlust.
    /// <para>
    /// Eine <b>verlorene Sitzung</b> ist kein Verlust in diesem Sinn (Vertrag Abschnitt 4): Sie wird
    /// einmal neu aufgebaut, ohne Frist und ohne den Fehlversuchszähler zu erhöhen. Erst wenn das
    /// scheitert, gilt die Verbindung als verloren.
    /// </para>
    /// </summary>
    [HostExecutionChecked]
    private async Task ServeAsync(Entry entry, GuardedUpstreamConnection connection, CancellationToken ct)
    {
        while (true)
        {
            var rebuild = await WatchAsync(entry, connection, ct).ConfigureAwait(false);

            SetState(entry, UpstreamState.Degraded, "Sitzung verloren, wird neu aufgebaut.");
            Log.UpstreamSessionLost(_logger, entry.Config.Slug);

            Session replacement;
            try
            {
                replacement = await EstablishAsync(entry, _options.SessionRebuildTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                rebuild.TrySetCanceled(ct);
                throw;
            }
            catch (Exception ex)
            {
                var reason = UpstreamFailure.Describe(ex, entry.Config);
                var lost = new UpstreamConnectionLostException(
                    $"Sitzung verloren, Neuaufbau gescheitert: {reason}", ex);
                Complete(entry, rebuild, pending => pending.TrySetException(lost));
                throw lost;
            }

            var previous = connection;
            connection = replacement.Connection;
            Publish(entry, replacement, afterSessionLoss: true);
            Complete(entry, rebuild, pending => pending.TrySetResult(connection));
            await DisposeQuietlyAsync(previous).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Die Health-Schleife einer Verbindung. Kehrt zurück, sobald ein Neuaufbau der Sitzung
    /// verlangt ist, und wirft, wenn die Verbindung verloren ist.
    /// </summary>
    private async Task<TaskCompletionSource<GuardedUpstreamConnection>> WatchAsync(
        Entry entry, GuardedUpstreamConnection connection, CancellationToken ct)
    {
        var healthySince = _time.GetUtcNow();
        var pingFailures = 0;

        Task rebuildRequested;
        lock (entry.Gate)
        {
            rebuildRequested = entry.RebuildRequested!.Task;
        }

        // Ein Upstream, der seine Änderungen nicht mehr von selbst meldet (Spec-Revision
        // 2026-07-28), wird turnusmäßig gefragt. Der Zeitpunkt steht auf „jetzt + Intervall",
        // nicht auf „jetzt": Die Erstabfrage ist gerade gelaufen, gleich noch einmal zu
        // fragen wäre reine Last.
        var nextCatalogPoll = connection.PushesCatalogChanges
            ? DateTimeOffset.MaxValue
            : _time.GetUtcNow() + _options.CatalogPollInterval;

        using var timer = new PeriodicTimer(_options.HealthCheckInterval, _time);
        while (true)
        {
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            if (await Task.WhenAny(tick, rebuildRequested).ConfigureAwait(false) == rebuildRequested)
            {
                return TakeRebuild(entry);
            }

            if (!await tick.ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                throw new UpstreamConnectionLostException("Die Health-Schleife wurde unerwartet beendet.");
            }

            try
            {
                await connection.PingAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (UpstreamSessionLostException)
            {
                return TakeRebuild(entry);
            }
            catch (Exception ex)
            {
                // Ein Aufrufer hat inzwischen gemeldet, dass die Sitzung fort ist — dann ist der
                // gescheiterte Ping dessen Folge (der Client ist tot) und kein eigener Befund.
                if (rebuildRequested.IsCompleted)
                {
                    return TakeRebuild(entry);
                }

                var reason = $"Health-Ping fehlgeschlagen: {UpstreamFailure.Describe(ex, entry.Config)}";

                // FR-08: erst Degraded, dann Failed. Die Verbindung bleibt bestehen und
                // bedient weiter Calls — der Zustand macht die Störung aber sichtbar,
                // statt sie bis zum Abriss zu verschweigen.
                if (++pingFailures > _options.DegradedPingTolerance)
                {
                    throw new UpstreamConnectionLostException(reason, ex);
                }

                SetState(entry, UpstreamState.Degraded, reason);
                continue;
            }

            if (pingFailures > 0)
            {
                pingFailures = 0;
                healthySince = _time.GetUtcNow();
                SetState(entry, UpstreamState.Healthy, null);
            }

            var now = _time.GetUtcNow();
            lock (entry.Gate)
            {
                entry.LastHealthyAt = now;

                // Z5: Der Zähler fällt erst nach einer gesunden Minute. Fiele er schon beim
                // Verbinden, würde ein Upstream, der alle paar Sekunden stirbt, auf Dauer im Takt
                // der Anfangsfrist neu gestartet.
                if (now - healthySince >= _options.HealthyResetWindow)
                {
                    entry.Attempt = 0;
                }
            }

            if (now >= nextCatalogPoll)
            {
                nextCatalogPoll = now + _options.CatalogPollInterval;
                try
                {
                    await RefreshInventoryAsync(entry, connection, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (UpstreamSessionLostException)
                {
                    return TakeRebuild(entry);
                }
                catch (Exception ex)
                {
                    // Eine misslungene Katalogabfrage ist kein toter Upstream: Die Aufrufe
                    // laufen weiter, nur der Stand ist alt. Sie zum Verbindungsabriss zu
                    // erklaeren waere eine Verschlimmbesserung — die naechste Runde
                    // versucht es erneut.
                    Log.UpstreamCatalogPollFailed(
                        _logger, entry.Config.Slug,
                        UpstreamFailure.Describe(ex, entry.Config), UpstreamFailure.TypeChain(ex));
                }
            }
        }
    }

    /// <summary>
    /// Die Frist bis zum nächsten Versuch: exponentiell bis zum Deckel, bei abgelehnter Anmeldung
    /// fest und lang — jeweils um bis zu <see cref="SupervisorOptions.ReconnectJitter"/> verkürzt.
    /// </summary>
    internal TimeSpan ComputeDelay(RestartPolicy policy, UpstreamFailureKind kind, int attempt)
    {
        var nominal = kind == UpstreamFailureKind.Authentication
            ? _options.AuthenticationRetryInterval
            : ComputeBackoff(policy, attempt);

        var share = Math.Clamp(_options.JitterSource(), 0d, 1d) * Math.Clamp(_options.ReconnectJitter, 0d, 1d);
        return TimeSpan.FromTicks((long)(nominal.Ticks * (1d - share)));
    }

    internal static TimeSpan ComputeBackoff(RestartPolicy policy, int attempt)
    {
        // In double rechnen und VOR der Umwandlung deckeln. Mit unbegrenzten Versuchen wächst der
        // Exponent ohne Ende; 'Anfang × 2^n' läuft schon bei n ≈ 40 aus dem Wertebereich von long,
        // und eine Frist, die dabei negativ wird, wirft in Task.Delay — der Loop wäre tot.
        var ticks = policy.InitialBackoff.Ticks * Math.Pow(policy.BackoffMultiplier, Math.Max(0, attempt - 1));
        return double.IsNaN(ticks) || ticks >= policy.MaxBackoff.Ticks
            ? policy.MaxBackoff
            : TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>
    /// Wartet die Frist ab — oder bis jemand „Jetzt verbinden" drückt. Ohne Frist
    /// (<paramref name="delay"/> ist <c>null</c>: die konfigurierte Obergrenze ist erreicht) wartet
    /// der Loop nur noch auf den Knopf.
    /// </summary>
    private async Task WaitForNextAttemptAsync(Entry entry, TimeSpan? delay, CancellationToken ct)
    {
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (entry.Gate)
        {
            entry.Wake = wake;
        }

        try
        {
            if (delay is not { } wait)
            {
                await wake.Task.WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timer = Task.Delay(wait, _time, timerCts.Token);
            await Task.WhenAny(timer, wake.Task).ConfigureAwait(false);
            await timerCts.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (entry.Gate)
            {
                entry.Wake = null;
            }
        }
    }

    /// <summary>Ein Wiederholungsversuch beginnt: Der Zustand bleibt Failed, nur die Frist ist abgelaufen.</summary>
    private void MarkAttemptRunning(Entry entry)
    {
        lock (entry.Gate)
        {
            entry.NextAttemptAt = null;
        }

        Raise(entry, UpstreamChangeKind.StateChanged);
    }

    /// <summary>
    /// Verbucht einen gescheiterten Versuch. Ins Audit und (oberhalb von Debug) ins Log geht nur,
    /// was sich <b>geändert</b> hat: der Eintritt in Failed, ein Wechsel der Fehlerart, das Erreichen
    /// der konfigurierten Obergrenze. Ein Versuch, der wieder scheitert, ist keine Nachricht.
    /// </summary>
    private void MarkFailed(
        Entry entry, UpstreamFailureKind kind, string reason, int attempt, TimeSpan? delay, Exception failure)
    {
        var now = _time.GetUtcNow();
        bool entered;
        bool kindChanged;
        bool exhaustedNow;
        UpstreamFailureKind previousKind;
        lock (entry.Gate)
        {
            entered = entry.State != UpstreamState.Failed;
            previousKind = entry.FailureKind;
            kindChanged = !entered && previousKind != kind;
            exhaustedNow = delay is null && !entry.Exhausted;

            entry.State = UpstreamState.Failed;
            entry.LastError = reason;
            entry.FailureKind = kind;
            entry.NextAttemptAt = delay is { } wait ? now + wait : null;
            entry.Exhausted = delay is null;
            if (entered)
            {
                entry.FailedSince = now;
            }
        }

        var slug = entry.Config.Slug;
        var types = UpstreamFailure.TypeChain(failure);
        if (entered)
        {
            AuditLifecycle(entry, $"{slug}: {UpstreamState.Failed} — {reason}");
            Log.UpstreamFailed(_logger, slug, kind, reason, types, delay);
        }
        else if (kindChanged)
        {
            AuditLifecycle(entry,
                $"{slug}: {UpstreamState.Failed} — Fehlerart wechselt von {previousKind} auf {kind}: {reason}");
            Log.UpstreamFailureKindChanged(_logger, slug, previousKind, kind, reason, types, delay);
        }
        else
        {
            Log.UpstreamRetryFailed(_logger, slug, attempt, kind, reason, types, delay);
        }

        if (exhaustedNow)
        {
            AuditLifecycle(entry,
                $"{slug}: {UpstreamState.Failed} — Wiederverbindung nach {attempt} Versuchen eingestellt (so konfiguriert).");
            Log.UpstreamRetriesExhausted(_logger, slug, attempt);
        }

        Raise(entry, UpstreamChangeKind.StateChanged);
        if (entered)
        {
            // Mit der Verbindung ist auch das Inventar fort — der Katalog muss das erfahren.
            Raise(entry, UpstreamChangeKind.InventoryChanged);
        }
    }

    private async Task CleanupConnectionAsync(Entry entry)
    {
        GuardedUpstreamConnection? connection;
        TaskCompletionSource<GuardedUpstreamConnection>? rebuild;
        lock (entry.Gate)
        {
            connection = entry.Connection;
            entry.Connection = null;
            entry.Inventory = null;
            entry.ToolCount = 0;
            entry.RebuildRequested = null;
            rebuild = entry.Rebuild;
            entry.Rebuild = null;
        }

        // Wer noch auf einen Neuaufbau wartet, bekommt eine Antwort statt einer Wartezeit bis zu
        // seinem eigenen Zeitlimit.
        if (rebuild is not null && rebuild.TrySetException(new UpstreamConnectionLostException(
                "Die Sitzung ging verloren, und der Upstream ist nicht mehr verbunden.")))
        {
            Observe(rebuild.Task);
        }

        if (connection is not null)
        {
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Der Neuaufbau, auf den ein Aufrufer wartet (über <see cref="IUpstreamSessionRecovery"/>).
    /// Alle Aufrufer derselben verlorenen Verbindung teilen sich einen Neuaufbau.
    /// </summary>
    private static Task<GuardedUpstreamConnection> RequestRebuildAsync(
        Entry entry, GuardedUpstreamConnection lost, CancellationToken ct)
    {
        TaskCompletionSource<GuardedUpstreamConnection> pending;
        lock (entry.Gate)
        {
            if (entry.Connection is not { } current)
            {
                throw new UpstreamConnectionLostException(
                    "Die Sitzung ging verloren, und der Upstream ist nicht mehr verbunden.");
            }

            // Schon ersetzt: Ein anderer Aufrufer war schneller, der Neuaufbau ist durch.
            if (!ReferenceEquals(current, lost))
            {
                return Task.FromResult(current);
            }

            pending = entry.Rebuild ??= NewRebuild();
            entry.RebuildRequested?.TrySetResult();
        }

        return pending.Task.WaitAsync(ct);
    }

    private static TaskCompletionSource<GuardedUpstreamConnection> TakeRebuild(Entry entry)
    {
        lock (entry.Gate)
        {
            return entry.Rebuild ??= NewRebuild();
        }
    }

    private static TaskCompletionSource<GuardedUpstreamConnection> NewRebuild()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Schließt einen Neuaufbau ab und gibt den Platz für den nächsten frei.</summary>
    private static void Complete(
        Entry entry,
        TaskCompletionSource<GuardedUpstreamConnection> rebuild,
        Action<TaskCompletionSource<GuardedUpstreamConnection>> outcome)
    {
        lock (entry.Gate)
        {
            if (ReferenceEquals(entry.Rebuild, rebuild))
            {
                entry.Rebuild = null;
            }
        }

        outcome(rebuild);
        Observe(rebuild.Task);
    }

    private static void AbortPendingRebuild(Entry entry)
    {
        TaskCompletionSource<GuardedUpstreamConnection>? rebuild;
        lock (entry.Gate)
        {
            rebuild = entry.Rebuild;
            entry.Rebuild = null;
        }

        rebuild?.TrySetCanceled();
    }

    /// <summary>
    /// Ein Neuaufbau, den der Health-Ping ausgelöst hat, hat keinen Wartenden. Scheitert er, darf
    /// die Ausnahme nicht als „unbeobachtet" beim Aufräumen des Laufzeitsystems wieder auftauchen.
    /// </summary>
    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Fragt den Katalog eines laufenden Upstreams neu ab und wendet die Definitionsprüfung erneut
    /// an. Wird nach der Annahme einer geänderten Definition aufgerufen: Das Tool kommt damit zurück,
    /// ohne dass der Upstream neu verbinden muss — und die Prüfung läuft gegen die <em>echte</em>
    /// aktuelle Definition, nicht gegen eine zwischengespeicherte Kopie.
    /// </summary>
    public async Task RediscoverAsync(ServerId id, CancellationToken ct)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return;
        }

        GuardedUpstreamConnection? connection;
        lock (entry.Gate)
        {
            connection = entry.Connection;
        }

        if (connection is null)
        {
            return;
        }

        await RefreshInventoryAsync(entry, connection, ct, raiseAlways: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Holt den Katalog eines laufenden Upstreams neu und legt ihn ab.
    /// <para>
    /// <paramref name="raiseAlways"/> trennt die beiden Anlässe: Die <b>ausdrückliche</b> Abfrage
    /// (Oberfläche, Annahme einer geänderten Definition) meldet immer eine Änderung — der Aufrufer
    /// will sehen, dass etwas passiert ist. Die <b>turnusmäßige</b> Abfrage meldet nur, wenn sich
    /// wirklich etwas geändert hat; sonst liefe jede Minute ein Katalog-Ereignis durch das ganze
    /// System, samt <c>tools/list_changed</c> an jede Sitzung.
    /// </para>
    /// </summary>
    private async Task RefreshInventoryAsync(
        Entry entry, GuardedUpstreamConnection connection, CancellationToken ct, bool raiseAlways = false)
    {
        var inventory = await connection.DiscoverAsync(ct).ConfigureAwait(false);
        var (screened, quarantined) = await ScreenInventoryAsync(entry, inventory, ct).ConfigureAwait(false);

        bool changed;
        lock (entry.Gate)
        {
            changed = entry.Inventory is null || Signature(entry.Inventory) != Signature(screened);
            entry.Inventory = screened;
            entry.QuarantinedTools = quarantined;
            entry.ToolCount = screened.Tools.Count;
        }

        if (raiseAlways || changed)
        {
            Raise(entry, UpstreamChangeKind.InventoryChanged);
        }
    }

    /// <summary>
    /// Was ein Inventar ausmacht, in einer Zeichenkette. Absichtlich über den
    /// Definitions-Fingerabdruck der Werkzeuge und nicht über deren Anzahl: Ein Upstream, der ein
    /// Werkzeug austauscht statt hinzufügt, käme sonst als „unverändert" durch — und genau das ist
    /// der Fall, den der Rug-Pull-Schutz sehen will.
    /// </summary>
    private static string Signature(UpstreamInventory inventory)
        => string.Join('\n',
            inventory.Tools.Select(t => ToolDefinitionHash.Compute(t))
                .Concat(inventory.Resources.Select(r => r.Uri.ToString()))
                .Concat(inventory.Prompts.Select(p => p.Name))
                .Order(StringComparer.Ordinal));

    /// <summary>
    /// Prüft jede gemeldete Tool-Definition gegen den festgehaltenen Fingerabdruck und nimmt
    /// geänderte aus dem Inventar.
    /// <para>
    /// <b>Warum nur das einzelne Tool und nicht der ganze Upstream:</b> Ein Rug Pull zielt auf ein
    /// Tool. Den ganzen Server abzuschalten wäre Kollateralschaden bei jedem normalen Update — und
    /// ein Schutz, der bei jedem Update den Betrieb anhält, wird abgeschaltet. Das geänderte Tool
    /// ist bis zur Annahme weder sichtbar noch aufrufbar; die übrigen laufen weiter.
    /// </para>
    /// <para>
    /// Ohne Pin-Store passiert nichts — dann verhält sich der Supervisor wie vorher.
    /// </para>
    /// </summary>
    private async Task<(UpstreamInventory Inventory, IReadOnlyList<string> Quarantined)>
        ScreenInventoryAsync(Entry entry, UpstreamInventory inventory, CancellationToken ct)
    {
        if (_pins is null)
        {
            return (inventory, []);
        }

        var kept = new List<ToolDescriptor>(inventory.Tools.Count);
        var quarantined = new List<string>();
        foreach (var tool in inventory.Tools)
        {
            var hash = ToolDefinitionHash.Compute(tool);
            var verdict = await _pins.VerifyAsync(entry.Id, tool.Name, hash, ct).ConfigureAwait(false);
            if (verdict is ToolDefinitionVerdict.Changed)
            {
                quarantined.Add(tool.Name);
                Log.ToolDefinitionChanged(_logger, entry.Config.Slug, tool.Name);
                // Eigene Audit-Zeile statt nur eines Logeintrags: Eine stillschweigend geänderte
                // Tool-Definition ist ein sicherheitsrelevantes Ereignis, kein Betriebsdetail.
                _audit?.Record(new AuditEvent(
                    _time.GetUtcNow(), Caller: null, CallOrigin.System, AuditEventKind.ServerLifecycle,
                    entry.Id, Tool: NamespacedToolName.Create(entry.Config.Slug, tool.Name).Value,
                    Status: null, RedactedArguments: null,
                    RequestBytes: null, ResponseBytes: null, Duration: null,
                    Detail: $"{entry.Config.Slug}: Tool-Definition von '{tool.Name}' hat sich geändert "
                        + "— zurückgehalten bis zur Annahme (Rug-Pull-Schutz)."));
                continue;
            }

            kept.Add(tool);
        }

        return quarantined.Count == 0
            ? (inventory, [])
            : (inventory with { Tools = kept }, quarantined);
    }

    private async Task DisposeQuietlyAsync(GuardedUpstreamConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.DisposeFailed(_logger, ex);
        }
    }

    /// <summary>
    /// Setzt einen Zustand außer Failed (dafür gibt es <see cref="MarkFailed"/>).
    /// <para>
    /// Ins Audit geht der <b>Wechsel</b> des Zustands, nicht jede neue Fehlermeldung im selben
    /// Zustand — sonst füllte ein flatternder Server das Log. Die Oberfläche erfährt trotzdem von
    /// jeder Änderung.
    /// </para>
    /// </summary>
    private void SetState(Entry entry, UpstreamState state, string? error, string? auditDetail = null)
    {
        bool stateChanged;
        bool anythingChanged;
        lock (entry.Gate)
        {
            stateChanged = entry.State != state;
            anythingChanged = stateChanged || entry.LastError != error;
            entry.State = state;
            entry.LastError = error;
            if (state != UpstreamState.Failed)
            {
                entry.FailureKind = UpstreamFailureKind.None;
                entry.NextAttemptAt = null;
                entry.Exhausted = false;
            }
        }

        if (!anythingChanged)
        {
            return;
        }

        if (stateChanged)
        {
            AuditLifecycle(entry, auditDetail ?? (error is null
                ? $"{entry.Config.Slug}: {state}"
                : $"{entry.Config.Slug}: {state} — {error}"));
        }

        Raise(entry, UpstreamChangeKind.StateChanged);
    }

    /// <summary>
    /// FR-22: Der Zustandswechsel eines Upstreams ist ein Systemereignis und gehört ins Audit-Log,
    /// nicht nur in den ILogger — sonst fehlt beim Nachvollziehen eines Ausfalls die Spur.
    /// </summary>
    private void AuditLifecycle(Entry entry, string detail)
        => _audit?.Record(new AuditEvent(
            _time.GetUtcNow(), Caller: null, CallOrigin.System, AuditEventKind.ServerLifecycle,
            entry.Id, Tool: null, Status: null, RedactedArguments: null,
            RequestBytes: null, ResponseBytes: null, Duration: null,
            Detail: detail));

    private void Raise(Entry entry, UpstreamChangeKind kind)
    {
        try
        {
            Changed?.Invoke(this, new UpstreamChangedEventArgs
            {
                Server = entry.Id,
                Kind = kind,
                State = entry.State,
            });
        }
        catch (Exception ex)
        {
            Log.ChangedHandlerThrew(_logger, ex);
        }
    }

    private Entry GetEntryOrThrow(ServerId id)
        => _entries.TryGetValue(id, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Upstream-Server {id} ist nicht registriert.");

    private void EnsureSlugUnique(string slug, ServerId? exclude)
    {
        var collision = _entries.Values.FirstOrDefault(
            e => e.Config.Slug.Equals(slug, StringComparison.Ordinal) && e.Id != exclude);
        if (collision is not null)
        {
            throw new ArgumentException($"Slug '{slug}' wird bereits von Server {collision.Id} verwendet (FR-03).");
        }
    }

    /// <summary>„12 s", „3 min 20 s", „12 h 4 min" — eine Dauer, wie ein Mensch sie liest.</summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalMinutes < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalSeconds} s");
        }

        return duration.TotalHours < 1
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes} min {duration.Seconds} s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours} h {duration.Minutes} min");
    }

    /// <summary>Was ein gelungener Verbindungsaufbau liefert.</summary>
    /// <param name="ReportedToolCount">Was der Upstream gemeldet hat — vor der Definitionsprüfung.</param>
    private sealed record Session(
        GuardedUpstreamConnection Connection,
        UpstreamInventory Inventory,
        IReadOnlyList<string> Quarantined,
        int ReportedToolCount);

    private sealed class Entry
    {
        public Entry(ServerId id, UpstreamServerConfig config, ConfigVersionId version)
        {
            Id = id;
            Config = config;
            Version = version;
            Recovery = new SessionRecovery(this);
        }

        public ServerId Id { get; }

        public object Gate { get; } = new();

        /// <summary>Über dieses Objekt lässt eine Verbindung ihre verlorene Sitzung neu aufbauen.</summary>
        public IUpstreamSessionRecovery Recovery { get; }

        /// <summary>Tools, deren Definition sich geändert hat und die deshalb zurückgehalten werden.</summary>
        public IReadOnlyList<string> QuarantinedTools { get; set; } = [];

        public SemaphoreSlim AdminLock { get; } = new(1, 1);

        public UpstreamServerConfig Config { get; set; }

        public ConfigVersionId Version { get; set; }

        public UpstreamState State { get; set; }

        public string? LastError { get; set; }

        public DateTimeOffset LastHealthyAt { get; set; }

        public int ToolCount { get; set; }

        public GuardedUpstreamConnection? Connection { get; set; }

        public UpstreamInventory? Inventory { get; set; }

        public CancellationTokenSource? LoopCts { get; set; }

        public Task? LoopTask { get; set; }

        /// <summary>Warum der Upstream Failed ist; <see cref="UpstreamFailureKind.None"/> in jedem anderen Zustand.</summary>
        public UpstreamFailureKind FailureKind { get; set; }

        /// <summary>Fehlversuche in Folge. Fällt erst nach einer gesunden Minute auf null (Z5).</summary>
        public int Attempt { get; set; }

        public DateTimeOffset? NextAttemptAt { get; set; }

        public bool Exhausted { get; set; }

        /// <summary>Seit wann der laufende Ausfall dauert — für die eine Zeile, die sein Ende meldet.</summary>
        public DateTimeOffset? FailedSince { get; set; }

        /// <summary>Gesetzt, solange der Loop auf den nächsten Versuch wartet; „Jetzt verbinden" löst es aus.</summary>
        public TaskCompletionSource? Wake { get; set; }

        /// <summary>Wird ausgelöst, sobald jemand den Neuaufbau der Sitzung verlangt; je Verbindung neu.</summary>
        public TaskCompletionSource? RebuildRequested { get; set; }

        /// <summary>Der laufende Neuaufbau, auf den Aufrufer warten.</summary>
        public TaskCompletionSource<GuardedUpstreamConnection>? Rebuild { get; set; }

        public UpstreamStatus ToStatus()
        {
            lock (Gate)
            {
                return new UpstreamStatus(
                    Id, Config.Slug, State, LastError, ToolCount, LastHealthyAt,
                    QuarantinedTools.Count == 0 ? null : QuarantinedTools,
                    State == UpstreamState.Failed
                        ? new UpstreamReconnectInfo(FailureKind, Attempt, NextAttemptAt, Exhausted)
                        : null);
            }
        }
    }

    private sealed class SessionRecovery(Entry entry) : IUpstreamSessionRecovery
    {
        public Task<GuardedUpstreamConnection> RebuildAsync(GuardedUpstreamConnection lost, CancellationToken ct)
            => RequestRebuildAsync(entry, lost, ct);

        public bool IsToolAvailable(string toolName)
        {
            lock (entry.Gate)
            {
                return entry.Inventory?.Tools.Any(
                    tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal)) == true;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Upstream {Slug}: Healthy mit {ToolCount} Tools.")]
        public static partial void UpstreamHealthy(ILogger logger, string slug, int toolCount);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Upstream {Slug}: wieder verbunden nach {Downtime} und {Attempts} Fehlversuchen, {ToolCount} Tools.")]
        public static partial void UpstreamReconnected(
            ILogger logger, string slug, string downtime, int attempts, int toolCount);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Sitzung verloren (HTTP 404 auf die Mcp-Session-Id) — wird neu aufgebaut.")]
        public static partial void UpstreamSessionLost(ILogger logger, string slug);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Upstream {Slug}: Sitzung neu aufgebaut, {ToolCount} Tools.")]
        public static partial void UpstreamSessionRebuilt(ILogger logger, string slug, int toolCount);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Definition von Tool {Tool} weicht vom angenommenen Stand ab — zurueckgehalten.")]
        public static partial void ToolDefinitionChanged(ILogger logger, string slug, string tool);

        // Die folgenden Einträge bekommen bewusst KEIN Ausnahmeobjekt: Dessen Textform trüge die
        // ungefilterte Meldung — beim MCP-SDK samt Antwortkörper des Upstreams. Geloggt werden
        // der bereinigte Grund und die Typnamen der Kette.

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: turnusmaessige Katalogabfrage fehlgeschlagen — der Stand bleibt vorerst alt. {Reason} [{ExceptionTypes}]")]
        public static partial void UpstreamCatalogPollFailed(
            ILogger logger, string slug, string reason, string exceptionTypes);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Failed ({Kind}) — {Reason} [{ExceptionTypes}] Nächster Versuch in {Delay}.")]
        public static partial void UpstreamFailed(
            ILogger logger, string slug, UpstreamFailureKind kind, string reason, string exceptionTypes, TimeSpan? delay);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Fehlerart wechselt von {Previous} auf {Kind} — {Reason} [{ExceptionTypes}] Nächster Versuch in {Delay}.")]
        public static partial void UpstreamFailureKindChanged(
            ILogger logger, string slug, UpstreamFailureKind previous, UpstreamFailureKind kind,
            string reason, string exceptionTypes, TimeSpan? delay);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Upstream {Slug}: Versuch {Attempt} gescheitert ({Kind}) — {Reason} [{ExceptionTypes}] Nächster Versuch in {Delay}.")]
        public static partial void UpstreamRetryFailed(
            ILogger logger, string slug, int attempt, UpstreamFailureKind kind,
            string reason, string exceptionTypes, TimeSpan? delay);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: Wiederverbindung nach {Attempts} Versuchen eingestellt — die Obergrenze ist ausdrücklich so konfiguriert. Weiter geht es nur über „Jetzt verbinden“.")]
        public static partial void UpstreamRetriesExhausted(ILogger logger, string slug, int attempts);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Drain-Frist {Grace} abgelaufen, {InFlight} Calls werden abgebrochen.")]
        public static partial void DrainExpired(ILogger logger, string slug, TimeSpan grace, int inFlight);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Fehler beim Schließen einer Upstream-Verbindung (ignoriert).")]
        public static partial void DisposeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Changed-Handler warf eine Exception — Handler müssen exception-frei sein.")]
        public static partial void ChangedHandlerThrew(ILogger logger, Exception ex);
    }
}
