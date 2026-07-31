using System.Collections.Concurrent;
using McpMcp.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpMcp.Core.Upstreams;

/// <summary>
/// Besitzt alle Upstream-Lebenszyklen (ADR-0005): pro Server ein Run-Loop mit
/// Connect → Discover → Healthy → Health-Ping-Schleife; bei Verlust Failed → Backoff → Restart.
/// Nach Erschöpfen der RestartPolicy bleibt der Server endgültig Failed, bis ein Admin eingreift.
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
    private readonly ConcurrentDictionary<ServerId, Entry> _entries = new();

    public UpstreamSupervisor(
        IEnumerable<IUpstreamConnector> connectors,
        IUpstreamConfigStore store,
        SupervisorOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<UpstreamSupervisor>? logger = null,
        IAuditSink? audit = null,
        IToolDefinitionPinStore? pins = null)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        ArgumentNullException.ThrowIfNull(store);
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

    public async Task<ServerId> AddAsync(UpstreamServerConfig config, CancellationToken ct)
    {
        UpstreamConfigValidator.Validate(config);
        EnsureSlugUnique(config.Slug, exclude: null);

        var id = ServerId.New();
        var version = await _store.AppendVersionAsync(id, config, ct).ConfigureAwait(false);
        var entry = new Entry(id, config, version)
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
    public Task RestoreAsync(ServerId id, UpstreamConfigVersion persisted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        UpstreamConfigValidator.Validate(persisted.Config);
        EnsureSlugUnique(persisted.Config.Slug, exclude: id);

        var entry = new Entry(id, persisted.Config, persisted.Version)
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

    public async Task<ConfigVersionId> ReconfigureAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        UpstreamConfigValidator.Validate(config);
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

    private async Task<ConfigVersionId> ReconfigureCoreAsync(Entry entry, UpstreamServerConfig config, CancellationToken ct)
    {
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

    private async Task RunLoopAsync(Entry entry, CancellationToken ct)
    {
        var policy = entry.Config.Restart ?? _options.DefaultRestartPolicy;
        var attempts = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(entry, UpstreamState.Starting, null);
                var connector = _connectors.TryGetValue(entry.Config.Kind, out var c)
                    ? c
                    : throw new InvalidOperationException($"Kein Connector für Transport {entry.Config.Kind} registriert.");

                var inner = await connector.ConnectAsync(entry.Id, entry.Config, ct).ConfigureAwait(false);
                var guarded = new GuardedUpstreamConnection(
                    inner, entry.Config.CallTimeout ?? _options.DefaultCallTimeout, _time);

                UpstreamInventory inventory;
                try
                {
                    inventory = await guarded.DiscoverAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await DisposeQuietlyAsync(guarded).ConfigureAwait(false);
                    throw;
                }

                // Rug-Pull-Erkennung: Was der Upstream jetzt meldet, wird gegen den festgehaltenen
                // Stand geprüft. Geänderte Tools erreichen den Katalog gar nicht erst — Filtern an
                // der Quelle statt einer zweiten Prüfung im Aufrufpfad.
                var (screened, quarantined) = await ScreenInventoryAsync(entry, inventory, ct)
                    .ConfigureAwait(false);

                lock (entry.Gate)
                {
                    entry.Connection = guarded;
                    entry.Inventory = screened;
                    entry.QuarantinedTools = quarantined;
                    entry.ToolCount = screened.Tools.Count;
                    entry.LastHealthyAt = _time.GetUtcNow();
                }

                SetState(entry, UpstreamState.Healthy, null);
                Raise(entry, UpstreamChangeKind.InventoryChanged);
                Log.UpstreamHealthy(_logger, entry.Config.Slug, inventory.Tools.Count);

                attempts = 0;
                var healthySince = _time.GetUtcNow();
                var pingFailures = 0;

                // Ein Upstream, der seine Änderungen nicht mehr von selbst meldet (Spec-Revision
                // 2026-07-28), wird turnusmäßig gefragt. Der Zeitpunkt steht auf „jetzt + Intervall",
                // nicht auf „jetzt": Die Erstabfrage ist gerade gelaufen, gleich noch einmal zu
                // fragen wäre reine Last.
                var nextCatalogPoll = guarded.PushesCatalogChanges
                    ? DateTimeOffset.MaxValue
                    : _time.GetUtcNow() + _options.CatalogPollInterval;

                using var timer = new PeriodicTimer(_options.HealthCheckInterval, _time);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        await guarded.PingAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // FR-08: erst Degraded, dann Failed. Die Verbindung bleibt bestehen und
                        // bedient weiter Calls — der Zustand macht die Störung aber sichtbar,
                        // statt sie bis zum Abriss zu verschweigen.
                        if (++pingFailures > _options.DegradedPingTolerance)
                        {
                            throw new UpstreamConnectionLostException($"Health-Ping fehlgeschlagen: {ex.Message}", ex);
                        }

                        SetState(entry, UpstreamState.Degraded, $"Health-Ping fehlgeschlagen: {ex.Message}");
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
                    }

                    if (now >= nextCatalogPoll)
                    {
                        nextCatalogPoll = now + _options.CatalogPollInterval;
                        try
                        {
                            await RefreshInventoryAsync(entry, guarded, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // Eine misslungene Katalogabfrage ist kein toter Upstream: Die Aufrufe
                            // laufen weiter, nur der Stand ist alt. Sie zum Verbindungsabriss zu
                            // erklaeren waere eine Verschlimmbesserung — die naechste Runde
                            // versucht es erneut.
                            Log.UpstreamCatalogPollFailed(_logger, ex, entry.Config.Slug);
                        }
                    }

                    if (now - healthySince >= _options.HealthyResetWindow)
                    {
                        attempts = 0;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await CleanupConnectionAsync(entry).ConfigureAwait(false);
                attempts++;
                SetState(entry, UpstreamState.Failed, ex.Message);
                Raise(entry, UpstreamChangeKind.InventoryChanged);

                if (attempts > policy.MaxRetries)
                {
                    Log.UpstreamPermanentlyFailed(_logger, ex, entry.Config.Slug, attempts);
                    return;
                }

                var delay = ComputeBackoff(policy, attempts);
                Log.UpstreamRestarting(_logger, ex, entry.Config.Slug, attempts, policy.MaxRetries, delay);
                try
                {
                    await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    internal static TimeSpan ComputeBackoff(RestartPolicy policy, int attempt)
    {
        var factor = Math.Pow(policy.BackoffMultiplier, attempt - 1);
        var delay = TimeSpan.FromTicks((long)(policy.InitialBackoff.Ticks * factor));
        return delay > policy.MaxBackoff ? policy.MaxBackoff : delay;
    }

    private async Task CleanupConnectionAsync(Entry entry)
    {
        GuardedUpstreamConnection? connection;
        lock (entry.Gate)
        {
            connection = entry.Connection;
            entry.Connection = null;
            entry.Inventory = null;
            entry.ToolCount = 0;
        }

        if (connection is not null)
        {
            await DisposeQuietlyAsync(connection).ConfigureAwait(false);
        }
    }

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

    private void SetState(Entry entry, UpstreamState state, string? error)
    {
        bool changed;
        lock (entry.Gate)
        {
            changed = entry.State != state || entry.LastError != error;
            entry.State = state;
            entry.LastError = error;
        }

        if (!changed)
        {
            return;
        }

        // FR-22: Zustandswechsel eines Upstreams ist ein Systemereignis und gehört ins Audit-Log,
        // nicht nur in den ILogger — sonst fehlt beim Nachvollziehen eines Ausfalls die Spur.
        _audit?.Record(new AuditEvent(
            _time.GetUtcNow(), Caller: null, CallOrigin.System, AuditEventKind.ServerLifecycle,
            entry.Id, Tool: null, Status: null, RedactedArguments: null,
            RequestBytes: null, ResponseBytes: null, Duration: null,
            Detail: error is null
                ? $"{entry.Config.Slug}: {state}"
                : $"{entry.Config.Slug}: {state} — {error}"));

        Raise(entry, UpstreamChangeKind.StateChanged);
    }

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

    private sealed class Entry
    {
        public Entry(ServerId id, UpstreamServerConfig config, ConfigVersionId version)
        {
            Id = id;
            Config = config;
            Version = version;
        }

        public ServerId Id { get; }

        public object Gate { get; } = new();

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

        public UpstreamStatus ToStatus()
        {
            lock (Gate)
            {
                return new UpstreamStatus(
                    Id, Config.Slug, State, LastError, ToolCount, LastHealthyAt,
                    QuarantinedTools.Count == 0 ? null : QuarantinedTools);
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Upstream {Slug}: Healthy mit {ToolCount} Tools.")]
        public static partial void UpstreamHealthy(ILogger logger, string slug, int toolCount);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Definition von Tool {Tool} weicht vom angenommenen Stand ab — zurueckgehalten.")]
        public static partial void ToolDefinitionChanged(ILogger logger, string slug, string tool);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: turnusmaessige Katalogabfrage fehlgeschlagen — der Stand bleibt vorerst alt.")]
        public static partial void UpstreamCatalogPollFailed(ILogger logger, Exception exception, string slug);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Verbindung verloren, Restart-Versuch {Attempt}/{MaxRetries} in {Delay}.")]
        public static partial void UpstreamRestarting(
            ILogger logger, Exception ex, string slug, int attempt, int maxRetries, TimeSpan delay);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: endgültig Failed nach {Attempts} Fehlversuchen — kein weiterer Restart.")]
        public static partial void UpstreamPermanentlyFailed(ILogger logger, Exception ex, string slug, int attempts);

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
