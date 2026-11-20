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
public sealed partial class UpstreamSupervisor : IUpstreamSupervisor, IUpstreamCredentialObserver, IAsyncDisposable
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

    /// <summary>1, sobald <see cref="DisposeAsync"/> begonnen hat (Plan 0005 D12).</summary>
    private int _disposing;

    private readonly object _admissionGate = new();
    private int _admissions;
    private TaskCompletionSource? _admissionsDrained;

    /// <summary>
    /// Die OAuth-Token der Upstreams. Ändert sich die Bindung eines Tokens (Endpunkt, Client-ID)
    /// oder wird der Upstream entfernt, wird es gelöscht (Plan 0005 E2).
    /// </summary>
    private readonly IUpstreamOAuthTokenStore? _oauthTokens;

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
        IHostExecutionPolicy? hostExecution = null,
        IUpstreamOAuthTokenStore? oauthTokens = null)
    {
        _oauthTokens = oauthTokens;
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
        EnterAdmission();
        try
        {
            EnsureSlugUnique(config.Slug, exclude: null);

            var id = ServerId.New();
            var version = await _store.AppendVersionAsync(id, config, ct).ConfigureAwait(false);
            var entry = new Entry(id, config, version)
            {
                State = config.Enabled ? UpstreamState.Starting : UpstreamState.Stopped,
            };

            Register(entry, $"ServerId-Kollision für '{config.Slug}' — sollte nie passieren.");
            return id;
        }
        finally
        {
            LeaveAdmission();
        }
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
        EnterAdmission();
        try
        {
            EnsureSlugUnique(persisted.Config.Slug, exclude: id);

            var entry = new Entry(id, persisted.Config, persisted.Version)
            {
                State = persisted.Config.Enabled ? UpstreamState.Starting : UpstreamState.Stopped,
            };

            Register(entry, $"Server {id} ist bereits registriert — Restore doppelt aufgerufen?");
            return Task.CompletedTask;
        }
        finally
        {
            LeaveAdmission();
        }
    }

    public async Task RemoveAsync(ServerId id, DrainPolicy drain, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(drain);
        var entry = GetEntryOrThrow(id);
        await AcquireAdminAsync(entry, ct).ConfigureAwait(false);
        try
        {
            // Erst löschen, dann stoppen (Plan 0005 D12) — wie beim Umkonfigurieren: Scheitert das
            // Löschen oder bricht der Aufrufer davor ab, läuft der Upstream unverändert weiter. Ab
            // hier ist das Entfernen beschlossen und wird auch ohne den Aufrufer zu Ende geführt;
            // sonst bliebe ein gestoppter Server zurück, der nach dem Neustart wieder hochkommt.
            await _store.RemoveAsync(id, ct).ConfigureAwait(false);

            // G7: Ab hier schreibt keine Prüfung mehr Pins für diesen Upstream fort. Eine, die schon im
            // Schreiben steckt (eine explizite Neuabfrage nimmt keine Verwaltungssperre), räumt nach
            // ihrem Schreiben selbst nach — sonst stünde nach dem Vergessen unten ein Pin für einen
            // Server, den es nicht mehr gibt. Das Entfernen wartet dafür auf nichts.
            lock (entry.Gate)
            {
                entry.PinsRetired = true;
            }

            await StopLoopAsync(entry, drain, CancellationToken.None).ConfigureAwait(false);
            ReleaseDeferredLifecycle(entry);
            entry.Removed = true;
            _entries.TryRemove(id, out _);

            // Mit dem Upstream gehen auch seine festgehaltenen Tool-Definitionen. Sonst bleiben
            // Pins auf einen Server zurück, den es nicht mehr gibt — sie wachsen mit jedem
            // Hinzufügen und Entfernen und stehen für immer in der Verwaltungsansicht.
            if (_pins is not null)
            {
                await _pins.ForgetServerAsync(id, () => NoSuccessor(entry), CancellationToken.None).ConfigureAwait(false);
            }

            // Ebenso sein OAuth-Token (E2): Es hängt nur an der ServerId und gälte sonst für nichts mehr.
            if (_oauthTokens is not null)
            {
                await _oauthTokens.RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
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
        await AcquireAdminAsync(entry, ct).ConfigureAwait(false);
        try
        {
            if (entry.Config.Enabled == enabled)
            {
                return;
            }

            // Erst das Audit, dann die Wirkung: Weist es ab (Compliance-Modus, voller Kanal), ist
            // nichts gespeichert und nichts geschehen (L4, R7, U3).
            AuditLifecycle(entry, $"{entry.Config.Slug}: {(enabled ? UpstreamState.Starting : UpstreamState.Stopped)}");

            var newConfig = entry.Config with { Enabled = enabled };
            entry.Version = await _store.AppendVersionAsync(id, newConfig, ct).ConfigureAwait(false);
            entry.Config = newConfig;

            if (enabled)
            {
                SetState(entry, UpstreamState.Starting, null, fromAdmin: true);
                StartLoop(entry);
            }
            else
            {
                // Ab dem Speichern ist das Abschalten beschlossen: Es wird zu Ende geführt, auch wenn
                // der Aufrufer inzwischen abbricht (Codex H4). Sonst stünde der Upstream als „aus"
                // in der Historie und liefe weiter — und ein zweites Abschalten fände nichts zu tun.
                // Der Drain ist durch seine Gnadenfrist begrenzt.
                await StopLoopAsync(entry, DrainPolicy.Graceful(_options.DefaultDrainGrace), CancellationToken.None)
                    .ConfigureAwait(false);
                SetState(entry, UpstreamState.Stopped, null, fromAdmin: true);
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
        await AcquireAdminAsync(entry, ct).ConfigureAwait(false);
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

        await AcquireAdminAsync(entry, ct).ConfigureAwait(false);
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

            // Kein wartender Loop heißt: verbunden, oder ein Versuch läuft bereits — dann geschieht
            // nichts (Abschnitt 5.1). Für neue Zugangsdaten gibt es CredentialsChanged.
            if (entry.State != UpstreamState.Failed || entry.Wake is not { } wake)
            {
                return Task.FromResult(false);
            }

            var woke = wake.TrySetResult();
            if (woke)
            {
                // Ab hier wartet der Loop nicht mehr — auch wenn er die Sperre noch nicht zurück hat.
                entry.LoopWaiting = false;
            }

            return Task.FromResult(woke);
        }
    }

    /// <inheritdoc />
    public void CredentialsChanged(ServerId id)
    {
        if (Volatile.Read(ref _disposing) != 0 || !_entries.TryGetValue(id, out var entry))
        {
            return;
        }

        lock (entry.Gate)
        {
            if (!entry.Config.Enabled || entry.State is UpstreamState.Stopped)
            {
                // Aus bleibt aus (Z2).
                return;
            }

            if (entry.Wake is { } wake)
            {
                // Er wartet (oder ist gerade dabei): Die Frist endet jetzt.
                if (wake.TrySetResult())
                {
                    entry.LoopWaiting = false;
                }

                return;
            }

            // Ein Versuch läuft — beim ersten Aufbau (Starting), als Wiederholung (Failed) oder als
            // Neuaufbau einer verlorenen Sitzung bzw. nach einem erneuerten Token (Degraded, G2). Liest
            // er noch das alte Token, scheitert er; dann folgt der nächste ohne Frist (Plan 0005 D11).
            // Maßgeblich ist, ob ein Versuch läuft, nicht der Anzeigezustand: Ein Neuaufbau steht auf
            // Degraded, und ein dort verworfenes Signal ließe den Upstream die lange Frist nach E2 —
            // oder an einer gesetzten Grenze für immer — warten, obwohl ein gültiges Token vorliegt.
            if (entry.AttemptRunning || entry.State is UpstreamState.Starting or UpstreamState.Failed)
            {
                entry.CredentialsChanged = true;
            }

            // Sonst steht die Verbindung (Healthy, oder Degraded wegen eines fehlenden Lebenszeichens):
            // Sie braucht keinen Anstoß (N12).
        }
    }

    /// <summary>
    /// Das Wecksignal für das Warten nach einem Fehlversuch — schon ausgelöst, wenn während des
    /// Versuchs neue Zugangsdaten gespeichert wurden. Nur unter <see cref="Entry.Gate"/>.
    /// </summary>
    private static TaskCompletionSource WakeAfterFailure(Entry entry)
    {
        var wake = NewWake();

        // Der Versuch ist verbucht; ab hier trägt das Wecksignal ein neues Token (G2).
        entry.AttemptRunning = false;
        if (entry.CredentialsChanged)
        {
            entry.CredentialsChanged = false;
            wake.TrySetResult();
        }

        return wake;
    }

    public async ValueTask DisposeAsync()
    {
        // Der Zaun zuerst (Plan 0005 D12): Wer ab hier hinzufügt oder eine Verwaltungssperre will,
        // wird abgewiesen, statt nach dem Aufräumen noch einen Loop zu starten.
        // Laufende Zulassungen (Hinzufügen, Wiederherstellen) werden abgewartet (E8).
        if (!await CloseAdmissionAsync().ConfigureAwait(false))
        {
            return;
        }

        foreach (var entry in _entries.Values)
        {
            // Jeder Upstream für sich: Scheitert das Stoppen des einen, laufen die übrigen sonst
            // weiter — mit Prozessen und Sockets bis zum Ende des Prozesses (T8).
            try
            {
                await entry.AdminLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await StopLoopAsync(entry, DrainPolicy.Immediate, CancellationToken.None).ConfigureAwait(false);
                    ReleaseDeferredLifecycle(entry);
                }
                finally
                {
                    entry.AdminLock.Release();
                }
            }
            catch (Exception ex)
            {
                var types = UpstreamFailure.TypeChain(ex);
                Quietly(() => Log.UpstreamDisposeFailed(_logger, entry.Config.Slug, types));
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

        // Erst das Audit, dann die Wirkung (L4, R7): Weist es ab, läuft der alte Stand weiter.
        AuditLifecycle(entry, $"{config.Slug}: {(config.Enabled ? UpstreamState.Starting : UpstreamState.Stopped)}");

        // Erst speichern, dann umschalten (Codex H4): Scheitert das Speichern, läuft der alte Loop
        // unverändert weiter, statt dass ein aktivierter Upstream ohne Loop zurückbleibt. Ab dem
        // Speichern ist der Wechsel beschlossen und wird auch bei einem Abbruch des Aufrufers zu
        // Ende geführt.
        var version = await _store.AppendVersionAsync(entry.Id, config, ct).ConfigureAwait(false);
        await StopLoopAsync(entry, DrainPolicy.Graceful(_options.DefaultDrainGrace), CancellationToken.None)
            .ConfigureAwait(false);

        // Ein Token gilt für genau eine Ressource und einen Client. Ändert sich eins davon, wird es
        // gelöscht, bevor der neue Loop startet — sonst ginge das alte Token an den neuen Endpunkt
        // (E2). Erst NACH dem Speichern der Konfiguration: Ein Rückweg aus dem Browser, der danach
        // speichert, sieht die neue Konfiguration und entfernt sein Token selbst.
        if (_oauthTokens is not null && OAuthAuthorizationAttempt.BindingChanged(entry.Config, config))
        {
            await _oauthTokens.RemoveAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
        }

        entry.Version = version;
        entry.Config = config;
        Raise(entry, UpstreamChangeKind.InventoryChanged);

        if (config.Enabled)
        {
            SetState(entry, UpstreamState.Starting, null, fromAdmin: true);
            StartLoop(entry);
        }
        else
        {
            SetState(entry, UpstreamState.Stopped, null, fromAdmin: true);
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
            entry.Wake = null;

            // Ein abgelöster Loop setzt das nicht mehr zurück (F2) — es gehört ab hier dem neuen.
            entry.LoopWaiting = false;
            entry.CredentialsChanged = false;
            entry.AttemptRunning = false;
            entry.CatalogRefused = null;
            entry.RenewalUnconfirmed = false;
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

        lock (entry.Gate)
        {
            entry.Stopping = true;
        }

        try
        {
            await StopRunningLoopAsync(entry, cts, loop, drain, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (entry.Gate)
            {
                entry.Stopping = false;
            }
        }
    }

    private async Task StopRunningLoopAsync(
        Entry entry, CancellationTokenSource cts, Task? loop, DrainPolicy drain, CancellationToken ct)
    {
        // Ab jetzt nimmt die stehende Verbindung keine neuen Anfragen mehr an (E4): Der Drain wartet
        // auf die laufenden, nicht auf solche, die während des Wartens noch hinzukommen.
        entry.Connection?.CloseAdmission();

        if (entry.Connection is { } connection && drain.GracePeriod > TimeSpan.Zero)
        {
            var idle = await connection.WaitForIdleAsync(drain.GracePeriod, ct).ConfigureAwait(false);
            if (!idle)
            {
                // Eine werfende Meldung darf den Abbruch nicht verhindern (Plan 0005 D4).
                Quietly(() => Log.DrainExpired(_logger, entry.Config.Slug, drain.GracePeriod, connection.InFlightCount));
            }
        }

        // Befristet wie das Warten auf den Loop (E4): Ein Abbruch-Callback, der hängt, hielte sonst
        // die Verwaltungssperre fest. Hängt er, wird die Quelle nicht entsorgt — sie läuft noch.
        var cancelled = await WithinStopTimeoutAsync(entry, cts.CancelAsync(), "Abbruch").ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                // Begrenzt (Plan 0005 D4): Ein Loop, der seinen Abbruch ignoriert — etwa ein
                // Lebenszeichen, das hängt —, hielte das Stoppen sonst für immer fest. Was er danach
                // noch tut, kann nichts mehr veröffentlichen (Publish prüft den Abbruch).
                await loop.WaitAsync(StopJoinTimeout, _time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Quietly(() => Log.UpstreamLoopHung(_logger, entry.Config.Slug, StopJoinTimeout));
                Observe(loop);
            }
            catch (OperationCanceledException)
            {
                // erwartetes Loop-Ende
            }
            catch (Exception ex)
            {
                // Ein Loop, der an einem Fehler geendet ist, darf das Abschalten, Umkonfigurieren
                // und Entfernen nicht ebenfalls scheitern lassen — sonst ließe sich ein solcher
                // Upstream nicht einmal mehr ausschalten.
                Log.UpstreamLoopFaulted(_logger, entry.Config.Slug, UpstreamFailure.TypeChain(ex));
            }
        }

        // Auch das Schließen der Verbindung ist befristet (E4) — ein Prozess oder Container, der
        // beim Beenden hängt, läuft im Hintergrund zu Ende.
        await WithinStopTimeoutAsync(entry, CleanupConnectionAsync(entry), "Schließen der Verbindung").ConfigureAwait(false);
        if (cancelled)
        {
            cts.Dispose();
        }

        entry.LoopCts = null;
        entry.LoopTask = null;
    }

    /// <summary>Wartet höchstens <see cref="StopJoinTimeout"/>; <c>false</c>, wenn der Schritt hängt.</summary>
    private async Task<bool> WithinStopTimeoutAsync(Entry entry, Task step, string what)
    {
        try
        {
            await step.WaitAsync(StopJoinTimeout, _time, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            Quietly(() => Log.UpstreamStopStepHung(_logger, entry.Config.Slug, what, StopJoinTimeout));
            Observe(step);
            return false;
        }
    }

    /// <summary>
    /// Das Schließen einer verlorenen oder ersetzten Verbindung im laufenden Loop — befristet wie
    /// beim Stoppen (F2, Z1). Ein Prozess oder Container, der beim Beenden hängt, läuft im
    /// Hintergrund zu Ende; der Loop macht nach höchstens <see cref="StopJoinTimeout"/> weiter. Ein
    /// Abbruch des Loops beendet das Warten sofort — der Aufrufer prüft danach, ob er abgelöst ist.
    /// </summary>
    private async Task WithinCloseTimeoutAsync(Entry entry, Task close, CancellationToken ct)
    {
        try
        {
            await close.WaitAsync(StopJoinTimeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Quietly(() => Log.UpstreamCloseHung(_logger, entry.Config.Slug, StopJoinTimeout));
            Observe(close);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Der Loop ist abgebrochen; das Schließen läuft im Hintergrund zu Ende.
            Observe(close);
        }
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
                    MarkConnecting(entry);
                    var session = await EstablishAsync(entry, _options.ConnectAttemptTimeout, ct).ConfigureAwait(false);
                    if (!Publish(entry, session, afterSessionLoss: false, ct))
                    {
                        // F6: Der Upstream wird gerade gestoppt; die neue Verbindung ist schon geschlossen.
                        // Der Loop wartet nur noch auf seinen Abbruch.
                        await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct).ConfigureAwait(false);
                    }

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

                // E1: Ein Fehler nach dem Stoppen wird nicht gebucht. Das Stoppen räumt selbst auf,
                // und hat es nach der Frist aus D4 aufgegeben, gehört der Eintrag womöglich schon
                // einem Nachfolger — Zähler, Zustand, Verbindung und Audit wären dann dessen.
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                // G4: Wird der Upstream gerade gestoppt, gehört das Schließen der Verbindung allein dem
                // Stopp-Pfad. Ein Verlust im Drain — etwa eine abgewiesene Katalogabfrage — schließt hier
                // nichts und bucht kein Failed mitten im Abschalten; der Loop wartet nur auf seinen Abbruch.
                // Plan 0005 I4: Geprüft wird unter entry.Gate, unter der auch der Stopp sein Stopping
                // setzt — vorher lag die Prüfung davor, und ein Stopp dazwischen fand die Verbindung schon
                // vom Fehlerpfad geschlossen, während sein Drain noch auf laufende Aufrufe wartete.
                //
                // Z1: Was ab hier scheitert, darf den Loop nicht beenden. Die Buchführung schreibt ins
                // Audit, und das Audit darf werfen (im Compliance-Modus bei vollem Kanal). Ein Loop,
                // der daran stirbt, hinterlässt genau den Zustand, den es nicht mehr geben soll:
                // Failed, und niemand versucht es wieder.
                int attempt;
                bool stopping;
                Detached lost = default;
                lock (entry.Gate)
                {
                    // Unter derselben Sperre, unter der ein Nachfolger seinen Zustand bekommt (StartLoop):
                    // Ein abgelöster Loop zählt keinen Versuch mehr und vermerkt keinen laufenden.
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    stopping = entry.Stopping;
                    attempt = stopping ? entry.Attempt : ++entry.Attempt;

                    // H4: Ab hier bis zum Verbuchen läuft ein Versuch — auch während des Schließens der
                    // verlorenen Verbindung, das bis zu StopJoinTimeout dauern kann. Ohne diese Marke
                    // stünde der Zustand dort noch auf Healthy oder Degraded, und es wartete niemand:
                    // CredentialsChanged verwürfe ein neues Token, und an einer gesetzten Grenze bliebe
                    // der Upstream stehen (E13). Zurückgesetzt wird die Marke in WakeAfterFailure — beide
                    // Wege unten (MarkFailed, SetFailedState) kommen dort vorbei, außer der Loop ist
                    // abgelöst; dann setzt sie StartLoop des Nachfolgers zurück.
                    if (!stopping)
                    {
                        entry.AttemptRunning = true;

                        // I4: Die Verbindung wird hier gelöst, nicht erst beim Schließen: Ab jetzt gehört
                        // sie dem Fehlerpfad, und ein Stopp, der danach beginnt, findet keine mehr, deren
                        // Aufrufe er abwarten müsste. Geschlossen wird unten, ohne Sperre.
                        lost = DetachConnection(entry);
                    }
                }

                if (stopping)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // erwartetes Ende
                    }

                    return;
                }

                // Vor dem geschützten Block bestimmt: Eine ausdrücklich gesetzte Obergrenze (E1) gilt
                // auch dann, wenn die Buchführung scheitert.
                var exhausted = policy.MaxRetries is { } max && attempt > max;
                // J6: Das Schließen der gelösten Verbindung beginnt sofort, vor allem, was scheitern
                // kann (Fehlerart, Beschreibung, Frist mit Jitter). Sonst bliebe sie bei einem Fehler
                // dort offen — gelöst vom Eintrag, also auch für ein späteres Stoppen unerreichbar —,
                // und ein wartender Neuaufbau bekäme keine Antwort.
                var closing = DisposeDetachedAsync(lost);
                Observe(closing);

                TimeSpan? delay = null;
                var planned = false;
                UpstreamFailureKind? kind = null;
                string? reason = null;
                try
                {
                    // Erst die Frist, dann alles andere: Scheitert danach etwas, gilt trotzdem die
                    // Frist, die zur Fehlerart gehört — nach abgelehnter Anmeldung also die lange
                    // aus E2, nicht der Deckel der Policy.
                    kind = UpstreamFailure.Classify(failure);
                    reason = UpstreamFailure.Describe(failure, entry.Config);
                    delay = exhausted ? null : ComputeDelay(policy, kind.Value, attempt);
                    planned = true;

                    // Befristet (F2, Z1): Ein Prozess oder Container, der beim Beenden hängt, hielte
                    // sonst jeden weiteren Versuch für immer auf.
                    await WithinCloseTimeoutAsync(entry, closing, ct).ConfigureAwait(false);

                    // F2, N20: Nach dem Warten zuerst prüfen, ob dieser Loop abgelöst ist. Hat das
                    // Stoppen nach seiner Frist aufgegeben, gehört der Eintrag schon einem Nachfolger.
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    MarkFailed(entry, kind.Value, reason, attempt, delay, failure, ct);
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!planned)
                    {
                        delay = exhausted ? null : FallbackDelay(policy);
                    }

                    // Erst der Zustand — er kann nicht scheitern. Was danach meldet (Audit,
                    // Ereignisse, Log), kann es; daran darf der Loop nicht enden (Z1).
                    if (SetFailedState(entry, kind, reason, delay, ct) is not { } report)
                    {
                        return;
                    }

                    try
                    {
                        ReportFailedWithoutDetail(entry, report, reason, attempt, delay, ex);
                    }
                    catch (Exception)
                    {
                        // Auch die Meldung über die gescheiterte Buchführung ist gescheitert —
                        // etwa an einer Log-Ausgabe, die wirft. Der Zustand steht, die Frist auch;
                        // mehr lässt sich hier nicht tun, ohne den Loop zu gefährden.
                    }
                }

                // Was das Audit bisher abgewiesen hat, bei jedem Versuch noch einmal anbieten —
                // ohne neue Zeile, also ohne Konflikt mit Abschnitt 6 des Vertrags. Sonst wartete
                // eine abgewiesene Failed-Zeile bis zum nächsten echten Wechsel, im Zweifel tagelang.
                RetryDeferredLifecycle(entry);

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
            // Nur der eigene Neuaufbau (E1): Läuft schon ein Nachfolger, gehört der wartende ihm.
            bool own;
            lock (entry.Gate)
            {
                try
                {
                    own = entry.LoopCts is null || entry.LoopCts.Token == ct;
                }
                catch (ObjectDisposedException)
                {
                    // Das Stoppen hat schon aufgeräumt — auch den wartenden Neuaufbau.
                    own = false;
                }
            }

            if (own)
            {
                AbortPendingRebuild(entry);
            }
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

        // Die Frist gilt für den ganzen Aufbau, nicht nur für das Verbinden (Codex H6): Auch eine
        // Katalogabfrage oder Pin-Prüfung, die ihr Abbruchsignal ignoriert, hielte den Loop sonst für
        // immer fest. WaitAsync verlässt den Versuch; was später doch noch fertig wird, wird
        // aufgeräumt und nie veröffentlicht.
        var work = EstablishCoreAsync(entry, connector, linked.Token);
        try
        {
            return await work.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Der Versuch ist verlassen: Was er doch noch liefert — auch, wenn er im selben Augenblick
            // fertig wurde, in dem die Frist ablief —, wird aufgeräumt und nie veröffentlicht
            // (Plan 0005 D5). Ein gescheiterter oder abgebrochener Versuch hinterlässt nichts.
            _ = DisposeLateSessionAsync(entry, work);
            if (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw Expired(timeout);
            }

            throw;
        }
    }

    private static TimeoutException Expired(TimeSpan timeout)
        => new($"Der Verbindungsversuch überschritt die Frist von {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.");

    [HostExecutionChecked]
    private async Task<Session> EstablishCoreAsync(Entry entry, IUpstreamConnector connector, CancellationToken ct)
    {
        HostExecutionGuard.Ensure(_hostExecution, entry.Config);
        var inner = await connector.ConnectAsync(entry.Id, entry.Config, ct).ConfigureAwait(false);
        var guarded = new GuardedUpstreamConnection(
            inner, entry.Config.CallTimeout ?? _options.DefaultCallTimeout, _time, entry.Recovery, entry.Config);
        try
        {
            var inventory = await guarded.DiscoverAsync(ct).ConfigureAwait(false);

            // Rug-Pull-Erkennung: Was der Upstream jetzt meldet, wird gegen den festgehaltenen
            // Stand geprüft. Geänderte Tools erreichen den Katalog gar nicht erst — Filtern an
            // der Quelle statt einer zweiten Prüfung im Aufrufpfad.
            var (screened, quarantined) = await ScreenInventoryAsync(entry, inventory, guarded, standing: false, ct)
                .ConfigureAwait(false);
            return new Session(guarded, screened, quarantined, inventory.Tools.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DisposeQuietlyAsync(guarded).ConfigureAwait(false);

            // F1: Ab hier hat die Verbindung Zugangsdaten vorgelegt — ein OAuth-Token, das in keiner
            // Konfiguration steht. Zitiert der Upstream es in seiner Fehlermeldung (etwa einem
            // JSON-RPC-Fehler auf tools/list), kennt es außerhalb dieser Methode niemand mehr: Status,
            // Audit, Log und der auf den Neuaufbau wartende Agent bekämen es ungefiltert. Deshalb wird
            // HIER bereinigt, mit den Zugangsdaten genau dieser Verbindung; die Fehlerart bleibt.
            throw UpstreamFailure.Sanitize(ex, guarded.DescribeFailure(ex));
        }
        catch
        {
            await DisposeQuietlyAsync(guarded).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Ab so vielen gleichzeitig hängenden Verbindungsaufbauten eines Upstreams steht es im Log.
    /// Einer kann Pech sein; drei heißen, dass der Connector seinen Abbruch nicht beachtet.
    /// </summary>
    internal const int LateConnectWarningThreshold = 3;

    /// <summary>So lange wartet das Stoppen nach dem Abbruch auf das Ende der Schleife (Plan 0005 D4).</summary>
    internal static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Räumt eine Verbindung auf, die erst nach Ablauf der Frist doch noch zustande kam — und zählt,
    /// wie viele solcher Aufbauten je Upstream noch hängen (U7). Ein Connector, der seinen Abbruch
    /// ignoriert, hinterlässt je Versuch einen; ohne Obergrenze der Versuche wächst das ohne Ende.
    /// Gemeldet wird einmal, beim Erreichen der Schwelle — nicht bei jedem weiteren.
    /// </summary>
    private async Task DisposeLateSessionAsync(Entry entry, Task<Session> establish)
    {
        var pending = Interlocked.Increment(ref entry.PendingLateConnects);
        if (pending == LateConnectWarningThreshold)
        {
            Quietly(() => Log.LateConnectsPiling(_logger, entry.Config.Slug, pending));
        }

        try
        {
            var late = await establish.ConfigureAwait(false);
            await DisposeQuietlyAsync(late.Connection).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Der Versuch ist längst als gescheitert verbucht; was hier noch kommt, ändert nichts.
        }
        finally
        {
            Interlocked.Decrement(ref entry.PendingLateConnects);
        }
    }

    /// <summary>Macht eine aufgebaute Verbindung zur aktuellen und meldet den Wechsel.</summary>
    /// <returns>
    /// <c>false</c>, wenn der Upstream gerade gestoppt wird (F6): Die Verbindung ist dann schon
    /// geschlossen und nie veröffentlicht worden. Ist der Loop abgebrochen, wirft die Methode.
    /// </returns>
    private bool Publish(Entry entry, Session session, bool afterSessionLoss, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        bool inventoryChanged;
        DateTimeOffset? failedSince;
        int attempts;
        var refused = false;
        lock (entry.Gate)
        {
            // Wird der Loop gerade gestoppt, veröffentlicht er nichts mehr (Plan 0005 D4): Die
            // Prüfung steht unter derselben Sperre wie das Aufräumen beim Stoppen.
            //
            // Das gilt schon ab dem Beginn des Stoppens, nicht erst ab dem Abbruch (F6): Der Drain
            // wartet vor dem Abbruch auf laufende Aufrufe der alten Verbindung. Eine Verbindung, die
            // in dieser Zeit veröffentlicht würde, nähme neue Anfragen an und würde nach der
            // Gnadenfrist mitten im Aufruf geschlossen.
            refused = ct.IsCancellationRequested || entry.Stopping;
            inventoryChanged = false;
            failedSince = null;
            attempts = 0;
            if (!refused)
            {
                inventoryChanged = entry.Inventory is null || Signature(entry.Inventory) != Signature(session.Inventory);
                entry.Connection = session.Connection;
                entry.Inventory = session.Inventory;
                entry.QuarantinedTools = session.Quarantined;
                entry.ToolCount = session.Inventory.Tools.Count;
                entry.LastHealthyAt = now;
                entry.Wake = null;
                entry.CredentialsChanged = false;
                entry.AttemptRunning = false;
                entry.CatalogRefused = null;
                entry.RebuildRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                // Mit der Verbindung wechselt auch der Platz für den Neuaufbau, im selben Schritt: Wer
                // die neue Verbindung schon verliert, bevor ServeAsync den alten Neuaufbau abschließt,
                // darf sich nicht ihm anschließen — der lieferte genau die Verbindung, die gerade starb
                // (Codex M4). Den alten schließt ServeAsync gleich danach mit dem Ergebnis ab.
                if (afterSessionLoss)
                {
                    entry.Rebuild = null;
                }

                failedSince = entry.FailedSince;
                entry.FailedSince = null;
                attempts = entry.Attempt;
            }
        }

        if (refused)
        {
            // Nicht veröffentlicht heißt: auch nicht offen gelassen.
            _ = DisposeQuietlyAsync(session.Connection);
            ct.ThrowIfCancellationRequested();
            return false;
        }

        if (failedSince is { } since)
        {
            // Der eine Eintrag, der sagt, dass ein Ausfall vorbei ist — mit dem, was man danach
            // wissen will: wie lange, wie viele Versuche, und ob die Werkzeuge wieder da sind.
            var downtime = FormatDuration(now - since);
            SetState(entry, UpstreamState.Healthy, null,
                $"{entry.Config.Slug}: {UpstreamState.Healthy} — wieder verbunden nach {downtime} und "
                + $"{attempts} Fehlversuchen, {session.Inventory.Tools.Count} Werkzeuge.");
            Quietly(() => Log.UpstreamReconnected(
                _logger, entry.Config.Slug, downtime, attempts, session.Inventory.Tools.Count));
        }
        else
        {
            SetState(entry, UpstreamState.Healthy, null);
            if (afterSessionLoss)
            {
                Quietly(() => Log.UpstreamSessionRebuilt(_logger, entry.Config.Slug, session.Inventory.Tools.Count));
            }
            else
            {
                Quietly(() => Log.UpstreamHealthy(_logger, entry.Config.Slug, session.ReportedToolCount));
            }
        }

        // Nach einem Neuaufbau der Sitzung nur melden, wenn sich wirklich etwas geändert hat:
        // Sonst liefe bei jedem Neustart des Upstreams ein tools/list_changed an jede Sitzung.
        if (!afterSessionLoss || inventoryChanged)
        {
            Raise(entry, UpstreamChangeKind.InventoryChanged);
        }

        return true;
    }

    /// <summary>
    /// Darf ein abgewiesenes Lebenszeichen zu einer Erneuerung der Zugangsdaten führen (V1)? Nur bei
    /// einem OAuth-Upstream, dessen Connector erneuern kann, nur bei einem <b>401</b> — ein 403 sagt
    /// „dieses Token darf das nicht", und ein neues Token derselben Anmeldung darf es auch nicht — und
    /// nur, wenn die Verbindung nach der letzten Erneuerung mindestens eine Minute gesund war: Ein 401
    /// gleich auf der neuen Verbindung ist eine abgelehnte Anmeldung (E2), ein Token, das eine Stunde
    /// später wieder abläuft, nicht.
    /// </summary>
    /// <param name="healthySince">
    /// Seit wann die stehende Verbindung ununterbrochen gesund ist (Plan 0005 D8): Zeit in Degraded
    /// zählt nicht, und eine Erneuerung, deren Neuaufbau erst über gewöhnliche Wiederholungen gelang,
    /// gilt bis zu einer gesunden Minute auf der so entstandenen Verbindung als nicht abgeschlossen.
    /// </param>
    private bool MayRenewCredentials(Entry entry, Exception exception, DateTimeOffset healthySince)
    {
        if (entry.Config.Http?.OAuth is null
            || !_connectors.TryGetValue(entry.Config.Kind, out var connector)
            || connector is not IUpstreamCredentialRenewal
            || !IsUnauthorized(exception))
        {
            return false;
        }

        lock (entry.Gate)
        {
            return !entry.RenewalUnconfirmed
                || _time.GetUtcNow() - healthySince >= _options.HealthyResetWindow;
        }
    }

    private static bool IsUnauthorized(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                return true;
            }
        }

        return false;
    }

    private void RequestCredentialRenewal(Entry entry, GuardedUpstreamConnection rejected)
    {
        if (_connectors.TryGetValue(entry.Config.Kind, out var connector)
            && connector is IUpstreamCredentialRenewal renewal)
        {
            // Das Token, das GENAU diese Verbindung vorgelegt hat (E11) — nicht das der zuletzt
            // aufgebauten, die schon eine neuere sein kann.
            var own = rejected.OwnPresentedCredentials;
            renewal.RequestCredentialRenewal(entry.Id, own.Count > 0 ? own[0] : null);
        }
    }

    /// <summary>Mehr Neuaufbauten der Sitzung als diese in <see cref="RebuildWindow"/> gelten als Ausfall (V2).</summary>
    internal const int MaxRebuildsPerWindow = 3;

    internal static readonly TimeSpan RebuildWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Vermerkt einen anstehenden Neuaufbau und sagt, ob es damit zu viele sind. Gezählt werden nur
    /// Neuaufbauten, die wirklich liefen; ein abgelehnter wird nicht vermerkt. Die Zeitpunkte bleiben,
    /// bis sie aus dem Fenster fallen — auch über ein Auslösen der Bremse hinweg: Sonst liefen nach
    /// der ersten Frist wieder drei sofortige Neuaufbauten im selben Fenster (Codex M5).
    /// </summary>
    private bool TooManyRebuilds(Entry entry)
    {
        var now = _time.GetUtcNow();
        lock (entry.Gate)
        {
            var recent = entry.RecentRebuilds;
            while (recent.Count > 0 && now - recent.Peek() >= RebuildWindow)
            {
                recent.Dequeue();
            }

            if (recent.Count >= MaxRebuildsPerWindow)
            {
                return true;
            }

            recent.Enqueue(now);
            return false;
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
            ct.ThrowIfCancellationRequested();

            Exception? refused;
            lock (entry.Gate)
            {
                refused = entry.CatalogRefused;
                entry.CatalogRefused = null;
            }

            if (refused is not null)
            {
                // E5: Eine ausgelöste Katalogabfrage wurde abgewiesen — wie bei der turnusmäßigen (D10)
                // ist die Verbindung damit verloren: abgelehnte Anmeldung, feste Frist.
                var lost = new UpstreamConnectionLostException(
                    $"Katalogabfrage abgewiesen: {connection.DescribeFailure(refused)}", refused);
                Complete(entry, rebuild, pending => pending.TrySetException(lost));
                throw lost;
            }

            bool stopping;
            lock (entry.Gate)
            {
                stopping = entry.Stopping;
            }

            if (stopping)
            {
                // Der Upstream wird gerade abgeschaltet und wartet nur noch auf laufende Aufrufe.
                // Eine neue Verbindung, die gleich wieder geschlossen wird, hilft niemandem — beim
                // Stdio-Upstream wäre es ein neuer Prozess (R7). Der Loop wartet auf sein Ende.
                if (rebuild.TrySetException(new UpstreamConnectionLostException(
                        "Der Upstream wird gestoppt; die Sitzung wird nicht mehr neu aufgebaut.")))
                {
                    Observe(rebuild.Task);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct).ConfigureAwait(false);
            }

            bool renewing;
            lock (entry.Gate)
            {
                renewing = entry.CredentialRenewalRequested;
                entry.CredentialRenewalRequested = false;
                if (renewing)
                {
                    // Offen, bis eine Verbindung danach eine Minute ununterbrochen gesund war — auch
                    // über gewöhnliche Wiederholungen hinweg (D8).
                    entry.RenewalUnconfirmed = true;
                }
            }

            if (renewing)
            {
                // V1: Der Upstream hat das Token auf der stehenden Verbindung abgewiesen. Einmal
                // erneuern und sofort neu verbinden — ohne Frist, ohne Fehlversuch. Wer die alte
                // Hülle noch hält, wartet auf die neue Verbindung.
                RequestCredentialRenewal(entry, connection);
                connection.MarkReplaced();
                SetState(entry, UpstreamState.Degraded, "Anmeldung abgewiesen, Zugriffstoken wird erneuert.");
                Quietly(() => Log.UpstreamCredentialsRenewing(_logger, entry.Config.Slug));
            }
            else if (TooManyRebuilds(entry))
            {
                // V2: Eine Sitzung, die immer wieder verloren geht (etwa hinter einem Lastverteiler
                // ohne feste Zuordnung), ist ein Ausfall — mit Frist statt Neuaufbau im Sekundentakt.
                var lost = new UpstreamConnectionLostException(
                    $"Die Sitzung geht wiederholt verloren: mehr als {MaxRebuildsPerWindow} Neuaufbauten in "
                    + $"{RebuildWindow.TotalMinutes:0} min.");
                Complete(entry, rebuild, pending => pending.TrySetException(lost));
                throw lost;
            }
            else
            {
                SetState(entry, UpstreamState.Degraded, "Sitzung verloren, wird neu aufgebaut.");
                Quietly(() => Log.UpstreamSessionLost(_logger, entry.Config.Slug));
            }

            Session replacement;
            MarkConnecting(entry);
            try
            {
                replacement = await EstablishAsync(entry, _options.SessionRebuildTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Abgebrochen hat der Loop (Abschalten, Entfernen), nicht der Aufrufer: Ein Abbruch
                // hielte er für seinen eigenen. Er bekommt, was jeder gescheiterte Neuaufbau meldet (U8).
                // Auch ein anderer Fehler nach dem Stoppen wird nicht mehr gebucht (E1).
                if (rebuild.TrySetException(new UpstreamConnectionLostException(
                        "Der Upstream wurde gestoppt; die Sitzung wird nicht mehr neu aufgebaut.")))
                {
                    Observe(rebuild.Task);
                }

                throw;
            }
            catch (Exception ex)
            {
                // Die innere Ausnahme bleibt dran: Ein abgelehntes Refresh oder ein 401 auf der neuen
                // Verbindung ist damit eine abgelehnte Anmeldung (E2), nicht „nicht erreichbar".
                var reason = UpstreamFailure.Describe(ex, entry.Config);
                var lost = new UpstreamConnectionLostException(
                    renewing
                        ? $"Zugriffstoken erneuert, Verbindung gescheitert: {reason}"
                        : $"Sitzung verloren, Neuaufbau gescheitert: {reason}",
                    ex);
                Complete(entry, rebuild, pending => pending.TrySetException(lost));
                throw lost;
            }

            var previous = connection;
            bool published;
            try
            {
                published = Publish(entry, replacement, afterSessionLoss: true, ct);
            }
            catch
            {
                // Die tote Verbindung wird geschlossen, was immer beim Veröffentlichen geschah —
                // sonst bliebe ihr Prozess oder ihr Socket bis zum Ende des Gateways offen (U4).
                await WithinCloseTimeoutAsync(entry, DisposeQuietlyAsync(previous), ct).ConfigureAwait(false);
                throw;
            }

            if (!published)
            {
                // F6: Das Stoppen hat begonnen, während der Neuaufbau lief. Die neue Verbindung ist
                // schon geschlossen; wer auf sie wartet, bekommt „Verbindung verloren" wie bei jedem
                // anderen gescheiterten Neuaufbau. Die alte bleibt dem Drain: Er wartet noch auf ihre
                // laufenden Aufrufe und schließt sie danach selbst.
                Complete(entry, rebuild, pending => pending.TrySetException(new UpstreamConnectionLostException(
                    "Der Upstream wird gestoppt; die Sitzung wird nicht mehr neu aufgebaut.")));
                await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct).ConfigureAwait(false);
            }

            var current = replacement.Connection;
            connection = current;
            Complete(entry, rebuild, pending => pending.TrySetResult(current));

            // Befristet (F2): Ein Schließen, das hängt, hielte sonst die Lebenszeichen der neuen
            // Verbindung für immer an.
            await WithinCloseTimeoutAsync(entry, DisposeQuietlyAsync(previous), ct).ConfigureAwait(false);
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
                // Ein abgelöster Loop nimmt keinen Neuaufbau mehr — der gehört dem Nachfolger (E1).
                ct.ThrowIfCancellationRequested();
                return TakeRebuild(entry);
            }

            if (!await tick.ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                throw new UpstreamConnectionLostException("Die Health-Schleife wurde unerwartet beendet.");
            }

            // H3: Im Drain geht kein Lebenszeichen mehr hinaus (N45). Ein 404 darauf beendete den
            // Client — samt den zugelassenen Aufrufen, auf die der Drain wartet (D13). Gezählt wird
            // dann auch nichts gegen die Toleranz; die Filter unten fangen nur ein Lebenszeichen, das
            // schon vor dem Beginn des Stoppens unterwegs war.
            if (IsStopping(entry))
            {
                continue;
            }

            try
            {
                await connection.PingAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                // E1: Kommt ein Lebenszeichen erst nach dem Stoppen zurück — etwa nach dem
                // begrenzten Warten aus D4 —, gehört der Eintrag womöglich schon einem Nachfolger.
                // Was immer das Lebenszeichen meldet: Dieser Loop bucht nichts mehr.
                throw ex as OperationCanceledException ?? new OperationCanceledException(
                    "Der Loop wurde abgelöst.", ex, ct);
            }
            catch (UpstreamSessionLostException)
            {
                return TakeRebuild(entry);
            }
            catch (Exception ex) when (!IsStopping(entry) && MayRenewCredentials(entry, ex, healthySince))
            {
                // V1: abgewiesenes Token — ServeAsync erneuert es und verbindet sofort neu.
                lock (entry.Gate)
                {
                    entry.CredentialRenewalRequested = true;
                }

                return TakeRebuild(entry);
            }
            catch (Exception) when (IsStopping(entry))
            {
                // G4: Der Upstream wird gestoppt und wartet nur noch auf laufende Aufrufe. Das Schließen
                // der Verbindung gehört ab hier allein dem Stopp-Pfad: Ein Lebenszeichen, das im Drain
                // fehlt, zählt nicht gegen die Toleranz, bucht weder Degraded noch Failed und schließt
                // nicht die Verbindung, auf deren Aufrufe der Drain wartet.
                continue;
            }
            catch (Exception ex)
            {
                // Ein Aufrufer hat inzwischen gemeldet, dass die Sitzung fort ist — dann ist der
                // gescheiterte Ping dessen Folge (der Client ist tot) und kein eigener Befund.
                if (rebuildRequested.IsCompleted)
                {
                    return TakeRebuild(entry);
                }

                var reason = $"Health-Ping fehlgeschlagen: {connection.DescribeFailure(ex)}";

                // FR-08: erst Degraded, dann Failed. Die Verbindung bleibt bestehen und
                // bedient weiter Calls — der Zustand macht die Störung aber sichtbar,
                // statt sie bis zum Abriss zu verschweigen.
                if (++pingFailures > _options.DegradedPingTolerance)
                {
                    throw new UpstreamConnectionLostException(reason, ex);
                }

                // Gestört ist nicht gesund (Plan 0005 E6): Die Minute aus Z5 und V1 beginnt erst mit
                // dem nächsten gelungenen Lebenszeichen neu, sonst zählte die Störung mit.
                healthySince = DateTimeOffset.MaxValue;
                SetState(entry, UpstreamState.Degraded, reason);
                continue;
            }

            // E1: auch ein gelungenes, aber spätes Lebenszeichen bucht nichts mehr (Zähler, Status).
            ct.ThrowIfCancellationRequested();

            if (pingFailures > 0)
            {
                pingFailures = 0;
                healthySince = _time.GetUtcNow();
                SetState(entry, UpstreamState.Healthy, null);
            }

            // Ein Lebenszeichen ist auch ein Anlass, abgewiesene Audit-Zeilen nachzureichen — sonst
            // wartete etwa ein abgewiesenes „wieder verbunden" bis zum nächsten Ausfall.
            RetryDeferredLifecycle(entry);

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

                    // Eine Minute ununterbrochen gesund: Die letzte Erneuerung hat getragen (V1, D8).
                    entry.RenewalUnconfirmed = false;
                }
            }

            // Im Drain wird der Katalog nicht mehr gefragt (G4): Eine Abweisung schlösse sonst die
            // Verbindung, auf deren Aufrufe der Drain wartet.
            if (now >= nextCatalogPoll && !IsStopping(entry))
            {
                nextCatalogPoll = now + _options.CatalogPollInterval;
                try
                {
                    await RefreshInventoryAsync(entry, connection, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ct.IsCancellationRequested)
                {
                    // E1: nach dem Stoppen wird nichts mehr gebucht.
                    throw ex as OperationCanceledException ?? new OperationCanceledException(
                        "Der Loop wurde abgelöst.", ex, ct);
                }
                catch (UpstreamSessionLostException)
                {
                    return TakeRebuild(entry);
                }
                catch (Exception ex) when (UpstreamFailure.Classify(ex) == UpstreamFailureKind.Authentication)
                {
                    // Eine abgewiesene Anmeldung bei der Katalogabfrage ist kein „Stand ist alt": Der
                    // Upstream nimmt diese Zugangsdaten nicht mehr an, auch wenn das Lebenszeichen
                    // noch durchkommt (Plan 0005 D10). Abschnitt 2 und 3: abgelehnte Anmeldung, feste
                    // Frist. Die sofortige Erneuerung aus V1 bleibt dem Lebenszeichen vorbehalten.
                    throw new UpstreamConnectionLostException(
                        $"Katalogabfrage abgewiesen: {connection.DescribeFailure(ex)}", ex);
                }
                catch (Exception ex)
                {
                    // Eine misslungene Katalogabfrage ist kein toter Upstream: Die Aufrufe
                    // laufen weiter, nur der Stand ist alt. Sie zum Verbindungsabriss zu
                    // erklaeren waere eine Verschlimmbesserung — die naechste Runde
                    // versucht es erneut.
                    Log.UpstreamCatalogPollFailed(
                        _logger, entry.Config.Slug,
                        connection.DescribeFailure(ex), UpstreamFailure.TypeChain(ex));
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
        return Waitable(TimeSpan.FromTicks((long)(nominal.Ticks * (1d - share))));
    }

    /// <summary>
    /// Die längste Frist, die ein Timer überhaupt annimmt (rund 49,7 Tage). Eine von Hand gesetzte
    /// <c>restart.maxBackoff</c> darüber ließe <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
    /// werfen — und der Loop wäre an einer Konfiguration gestorben, die der Validator annimmt.
    /// </summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static TimeSpan Waitable(TimeSpan delay)
        => delay < TimeSpan.Zero ? TimeSpan.Zero : delay > LongestWait ? LongestWait : delay;

    /// <summary>
    /// Die Frist für den Fall, dass die Buchführung eines Fehlversuchs selbst scheitert: der Deckel
    /// der Policy, ohne Jitter und ohne alles, was noch einmal werfen könnte.
    /// </summary>
    private static TimeSpan FallbackDelay(RestartPolicy policy) => Waitable(policy.MaxBackoff);

    private const string BookkeepingFailedText = "Der Fehlversuch konnte nicht vollständig verbucht werden.";

    /// <summary>Was <see cref="SetFailedState"/> geändert hat — und deshalb noch gemeldet werden muss.</summary>
    private readonly record struct FailedTransition(bool Entered, bool ExhaustedNow, UpstreamFailureKind Kind);

    /// <summary>
    /// Setzt nur den Zustand, für den Fall, dass die Buchführung eines Fehlversuchs gescheitert ist.
    /// Kann nicht werfen: nur Zuweisungen unter der Sperre. Was <see cref="MarkFailed"/> schon
    /// gesetzt hat, bleibt stehen; eine schon bestimmte Fehlerart wird übernommen, damit der nächste
    /// Versuch keinen Wechsel „von None" meldet.
    /// <para>
    /// <c>null</c>, wenn der Loop inzwischen abgelöst ist (F2): Dann wird nichts gesetzt.
    /// </para>
    /// </summary>
    private FailedTransition? SetFailedState(
        Entry entry, UpstreamFailureKind? kind, string? reason, TimeSpan? delay, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        lock (entry.Gate)
        {
            // Unter derselben Sperre, unter der ein Nachfolger seinen Zustand bekommt (StartLoop).
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            var entered = entry.State != UpstreamState.Failed;
            var exhaustedNow = delay is null && !entry.Exhausted;
            if (entered)
            {
                entry.FailedSince = now;
            }

            entry.State = UpstreamState.Failed;
            // Der schon bereinigte Grund geht vor, wenn er bestimmt werden konnte: Sonst stünde nach
            // Degraded noch der Text des gescheiterten Lebenszeichens da, während das Audit den
            // eigentlichen Grund nennt. Scheiterte schon die Bestimmung, bleibt der alte Text stehen.
            entry.LastError = reason ?? entry.LastError ?? BookkeepingFailedText;
            entry.FailureKind = kind
                ?? (entry.FailureKind == UpstreamFailureKind.None ? UpstreamFailureKind.Unreachable : entry.FailureKind);
            entry.NextAttemptAt = delay is { } wait ? now + wait : null;
            entry.Exhausted = delay is null;
            entry.Wake ??= WakeAfterFailure(entry);
            return new FailedTransition(entered, exhaustedNow, entry.FailureKind);
        }
    }

    /// <summary>
    /// Meldet, was <see cref="SetFailedState"/> geändert hat: die Audit-Zeilen, die
    /// <see cref="MarkFailed"/> nicht mehr geschrieben hat, beide Ereignisse und die Fehlerzeile im
    /// Log. Darf werfen; der Aufrufer fängt es.
    /// </summary>
    private void ReportFailedWithoutDetail(
        Entry entry, FailedTransition transition, string? reason, int attempt, TimeSpan? delay, Exception ex)
    {
        var slug = entry.Config.Slug;

        // Ohne diese Zeile stünde im Audit später ein „wieder verbunden" ohne vorheriges Failed.
        if (transition.Entered)
        {
            RecordLoopLifecycle(
                entry, $"{slug}: {UpstreamState.Failed} ({transition.Kind}) — {reason ?? BookkeepingFailedText}");
        }

        if (transition.ExhaustedNow)
        {
            RecordLoopLifecycle(entry,
                $"{slug}: {UpstreamState.Failed} — Wiederverbindung nach {attempt} Versuchen eingestellt (so konfiguriert).");
        }

        // Beide Ereignisse, auch wenn der Zustand schon Failed war: MarkFailed kann nach dem
        // Zustandswechsel und vor seinen Ereignissen gescheitert sein — dann wüsste der Katalog
        // nicht, dass das Inventar fort ist. Ein Ereignis zu viel ist harmlos, eines zu wenig nicht.
        Raise(entry, UpstreamChangeKind.StateChanged);
        Raise(entry, UpstreamChangeKind.InventoryChanged);

        var types = UpstreamFailure.TypeChain(ex);
        if (delay is { } wait)
        {
            Log.UpstreamBookkeepingFailed(_logger, slug, types, wait);
        }
        else
        {
            Log.UpstreamBookkeepingFailedAtLimit(_logger, slug, types, attempt);
        }
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
    /// Ob der Loop dieses Upstreams gerade auf den nächsten Versuch wartet — also alles verbucht hat,
    /// was zum letzten gehört: Zustand, Audit, Log. Für Tests: <see cref="UpstreamStatus"/> zeigt die
    /// neue Frist schon, bevor Audit und Log geschrieben sind.
    /// </summary>
    internal bool IsWaitingForNextAttempt(ServerId id)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return false;
        }

        lock (entry.Gate)
        {
            return entry.LoopWaiting;
        }
    }

    private static TaskCompletionSource NewWake()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Wartet die Frist ab — oder bis jemand „Jetzt verbinden" drückt. Ohne Frist
    /// (<paramref name="delay"/> ist <c>null</c>: die konfigurierte Obergrenze ist erreicht) wartet
    /// der Loop nur noch auf den Knopf.
    /// <para>
    /// Das Wecksignal legt schon das Verbuchen an (<see cref="MarkFailed"/>), unter derselben Sperre,
    /// unter der die Frist sichtbar wird: Ein Klick in der Lücke dazwischen verpufft sonst (L1).
    /// </para>
    /// <para>
    /// Warten zurückgestellte Audit-Zeilen, wird in Abschnitten von einem Lebenszeichen-Abstand
    /// gewartet und dazwischen nachgereicht — sonst warteten sie an der Obergrenze bis zum nächsten
    /// Eingriff und bei sehr großer Frist bis zum nächsten Versuch (T2).
    /// </para>
    /// </summary>
    private async Task WaitForNextAttemptAsync(Entry entry, TimeSpan? delay, CancellationToken ct)
    {
        TaskCompletionSource wake;
        lock (entry.Gate)
        {
            wake = entry.Wake ??= NewWake();
            entry.LoopWaiting = !wake.Task.IsCompleted;
        }

        try
        {
            DateTimeOffset? deadline = delay is { } wait ? _time.GetUtcNow() + wait : null;
            while (!wake.Task.IsCompleted)
            {
                var remaining = deadline is { } until ? until - _time.GetUtcNow() : (TimeSpan?)null;
                if (remaining is { } left && left <= TimeSpan.Zero)
                {
                    return;
                }

                bool pending;
                lock (entry.AuditGate)
                {
                    pending = entry.DeferredLifecycle.Count > 0;
                }

                var slice = pending && (remaining is null || remaining > _options.HealthCheckInterval)
                    ? _options.HealthCheckInterval
                    : remaining;
                if (slice is null)
                {
                    await wake.Task.WaitAsync(ct).ConfigureAwait(false);
                    return;
                }

                using (var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var timer = Task.Delay(Waitable(slice.Value), _time, timerCts.Token);
                    await Task.WhenAny(timer, wake.Task).ConfigureAwait(false);
                    await timerCts.CancelAsync().ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                if (pending)
                {
                    RetryDeferredLifecycle(entry);
                }
            }
        }
        finally
        {
            lock (entry.Gate)
            {
                // Nur das eigene Wecksignal (F2): Ein abgelöster Loop, der erst nach dem Start seines
                // Nachfolgers hier ankommt, räumte sonst dessen Signal weg — „Jetzt verbinden" erreichte
                // den wartenden Nachfolger nicht mehr.
                if (ReferenceEquals(entry.Wake, wake))
                {
                    entry.Wake = null;
                    entry.LoopWaiting = false;
                }
            }
        }
    }

    /// <summary>
    /// Ein Verbindungsaufbau beginnt — erster Versuch, Wiederholung oder Neuaufbau der Sitzung (G2).
    /// Bis er veröffentlicht oder als gescheitert verbucht ist, merkt sich
    /// <see cref="CredentialsChanged(ServerId)"/> ein neues Token für den nächsten Versuch.
    /// </summary>
    private static void MarkConnecting(Entry entry)
    {
        lock (entry.Gate)
        {
            entry.AttemptRunning = true;
        }
    }

    /// <summary>Wird der Upstream gerade gestoppt (Drain eingeschlossen)?</summary>
    private static bool IsStopping(Entry entry)
    {
        lock (entry.Gate)
        {
            return entry.Stopping;
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
        Entry entry, UpstreamFailureKind kind, string reason, int attempt, TimeSpan? delay, Exception failure,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        bool entered;
        bool kindChanged;
        bool exhaustedNow;
        UpstreamFailureKind previousKind;
        lock (entry.Gate)
        {
            // F2: Ein abgelöster Loop bucht nichts — geprüft unter derselben Sperre, unter der ein
            // Nachfolger seinen Zustand bekommt (StartLoop).
            if (ct.IsCancellationRequested)
            {
                return;
            }

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

            // Unter derselben Sperre wie die sichtbare Frist: Wer sie sieht, kann auch klicken (L1).
            entry.Wake ??= WakeAfterFailure(entry);
        }

        // Erst alle Audit-Zeilen, dann das Log. Die Audit-Zeilen gehen über RecordLoopLifecycle, das
        // nicht wirft (eine abgewiesene Zeile wird zurückgestellt und nachgereicht). Eine Log-Ausgabe
        // dagegen kann werfen — sie darf keine Audit-Zeile mehr mitreißen: Der Zustand steht ab hier,
        // und ein späterer Versuch sähe keinen Wechsel mehr, den er nachholen könnte.
        var slug = entry.Config.Slug;
        if (entered)
        {
            // Abschnitt 6: Slug, Fehlerart, bereinigter Grund (L6).
            RecordLoopLifecycle(entry, $"{slug}: {UpstreamState.Failed} ({kind}) — {reason}");
        }
        else if (kindChanged)
        {
            RecordLoopLifecycle(entry,
                $"{slug}: {UpstreamState.Failed} — Fehlerart wechselt von {previousKind} auf {kind}: {reason}");
        }

        if (exhaustedNow)
        {
            RecordLoopLifecycle(entry,
                $"{slug}: {UpstreamState.Failed} — Wiederverbindung nach {attempt} Versuchen eingestellt (so konfiguriert).");
        }

        var types = UpstreamFailure.TypeChain(failure);
        if (entered)
        {
            Log.UpstreamFailed(_logger, slug, kind, reason, types, delay);
        }
        else if (kindChanged)
        {
            Log.UpstreamFailureKindChanged(_logger, slug, previousKind, kind, reason, types, delay);
        }
        else
        {
            Log.UpstreamRetryFailed(_logger, slug, attempt, kind, reason, types, delay);
        }

        if (exhaustedNow)
        {
            Log.UpstreamRetriesExhausted(_logger, slug, attempt);
        }

        Raise(entry, UpstreamChangeKind.StateChanged);
        if (entered)
        {
            // Mit der Verbindung ist auch das Inventar fort — der Katalog muss das erfahren.
            Raise(entry, UpstreamChangeKind.InventoryChanged);
        }
    }

    private Task CleanupConnectionAsync(Entry entry)
    {
        Detached detached;
        lock (entry.Gate)
        {
            detached = DetachConnection(entry);
        }

        return DisposeDetachedAsync(detached);
    }

    /// <summary>Was beim Lösen der Verbindung vom Eintrag abfällt — geschlossen wird es danach, ohne Sperre.</summary>
    private readonly record struct Detached(
        GuardedUpstreamConnection? Connection, TaskCompletionSource<GuardedUpstreamConnection>? Rebuild);

    /// <summary>
    /// Löst Verbindung, Inventar und wartenden Neuaufbau vom Eintrag. Nur unter <see cref="Entry.Gate"/>:
    /// Wer hier löst, hat die Verbindung für sich beansprucht (Plan 0005 I4).
    /// </summary>
    private static Detached DetachConnection(Entry entry)
    {
        var detached = new Detached(entry.Connection, entry.Rebuild);
        entry.Connection = null;
        entry.Inventory = null;
        entry.ToolCount = 0;
        entry.RebuildRequested = null;
        entry.Rebuild = null;
        return detached;
    }

    private async Task DisposeDetachedAsync(Detached detached)
    {
        // Wer noch auf einen Neuaufbau wartet, bekommt eine Antwort statt einer Wartezeit bis zu
        // seinem eigenen Zeitlimit.
        if (detached.Rebuild is { } rebuild && rebuild.TrySetException(new UpstreamConnectionLostException(
                "Die Sitzung ging verloren, und der Upstream ist nicht mehr verbunden.")))
        {
            Observe(rebuild.Task);
        }

        if (detached.Connection is { } connection)
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

            if (entry.Stopping)
            {
                throw new UpstreamConnectionLostException(
                    "Der Upstream wird gestoppt; die Sitzung wird nicht mehr neu aufgebaut.");
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

    /// <summary>
    /// Der Loop endet (abgeschaltet, entfernt, beendet): Wer noch auf einen Neuaufbau wartet, bekommt
    /// „Verbindung verloren" — kein bloßes Abbrechen, das der Aufrufer nicht vom eigenen Abbruch
    /// unterscheiden kann und deshalb unerklärt weitergibt (U8).
    /// </summary>
    private static void AbortPendingRebuild(Entry entry)
    {
        TaskCompletionSource<GuardedUpstreamConnection>? rebuild;
        lock (entry.Gate)
        {
            rebuild = entry.Rebuild;
            entry.Rebuild = null;
        }

        if (rebuild is not null && rebuild.TrySetException(new UpstreamConnectionLostException(
                "Der Upstream wurde gestoppt; die Sitzung wird nicht mehr neu aufgebaut.")))
        {
            Observe(rebuild.Task);
        }
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
            // H3: Im Drain bleibt die Neuabfrage ohne Wirkung, wie ohne Verbindung (N45). Ein 404 auf
            // tools/list beendete sonst den Client samt den Aufrufen, auf die der Drain wartet; der
            // nächste Aufbau holt den Katalog ohnehin neu und prüft ihn.
            connection = entry.Stopping ? null : entry.Connection;
        }

        if (connection is null)
        {
            return;
        }

        try
        {
            await RefreshInventoryAsync(entry, connection, ct, raiseAlways: true).ConfigureAwait(false);
        }
        catch (UpstreamSessionLostException)
        {
            // Die Sitzung ist fort (R6). Hier meldet sie sonst niemand: Bis das nächste
            // Lebenszeichen sie bemerkt, liefe jeder Aufruf erst in die tote Verbindung. Der
            // Neuaufbau holt den Katalog ohnehin neu und prüft ihn.
            lock (entry.Gate)
            {
                if (ReferenceEquals(entry.Connection, connection))
                {
                    entry.RebuildRequested?.TrySetResult();
                }
            }
        }
        catch (Exception ex) when (UpstreamFailure.Classify(ex) == UpstreamFailureKind.Authentication)
        {
            // Abschnitt 2 gilt für jede Katalogabfrage, auch die ausgelöste (Plan 0005 E5): Der
            // Upstream nimmt diese Zugangsdaten nicht mehr an. Gemeldet wird das dem Loop der
            // Verbindung — nur, wenn sie noch steht —, und der Aufrufer erfährt es trotzdem.
            lock (entry.Gate)
            {
                if (ReferenceEquals(entry.Connection, connection) && entry.RebuildRequested is { } wake)
                {
                    entry.CatalogRefused = ex;
                    wake.TrySetResult();
                }
            }

            throw;
        }
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
        // Eine Neuabfrage nach der anderen: Sonst überschriebe ein älterer Stand, der später fertig
        // wird, das Inventar einer jüngeren Prüfung — samt ihrem Rückzug (Plan 0005 D3).
        await entry.RediscoveryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // H3: Geprüft wird nach dem Warten auf die Sperre, nicht nur davor: Wer hinter einer
            // laufenden Abfrage wartet, bekommt die Sperre womöglich erst im Drain — und sendet dann
            // nichts mehr (N45). Turnusmäßige und ausdrückliche Abfrage gleich.
            if (IsStopping(entry))
            {
                return;
            }

            await RefreshInventoryCoreAsync(entry, connection, raiseAlways, ct).ConfigureAwait(false);
        }
        catch (UpstreamCatalogWithheldException) when (IsStopping(entry))
        {
            // I3: Der Drain begann mitten in der Abfrage; die Verbindung hat vor der nächsten Anfrage
            // aufgehört. Ohne Wirkung wie eine Abfrage, die erst im Drain käme (N49) — kein Fehler.
        }
        finally
        {
            entry.RediscoveryLock.Release();
        }
    }

    private async Task RefreshInventoryCoreAsync(
        Entry entry, GuardedUpstreamConnection connection, bool raiseAlways, CancellationToken ct)
    {
        var inventory = await connection.DiscoverAsync(() => !IsStopping(entry), ct).ConfigureAwait(false);
        var (screened, quarantined) = await ScreenInventoryAsync(entry, inventory, connection, standing: true, ct)
            .ConfigureAwait(false);

        bool changed;
        lock (entry.Gate)
        {
            // Inzwischen ersetzt: Der Katalog der alten Verbindung überschriebe sonst den, den der
            // Neuaufbau gerade geprüft hat (L3).
            if (!ReferenceEquals(entry.Connection, connection))
            {
                return;
            }

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
    /// <para>
    /// <b>Eine Prüfung nach der anderen</b> (F7): Prüfungen beim Verbindungsaufbau und bei der
    /// Neuabfrage schreiben dieselben Pins fort. Hielte eine veraltete Neuabfrage erst im Schreiben
    /// an — nach dem Blick auf die Verbindung —, überschriebe sie sonst das Urteil, das der Neuaufbau
    /// inzwischen geschrieben hat. Die Sperre umfasst nur die Prüfung, nicht die Katalogabfrage: Eine
    /// hängende Abfrage hält keinen Neuaufbau auf. Reihenfolge der Sperren: erst
    /// <see cref="Entry.RediscoveryLock"/>, dann <see cref="Entry.ScreeningLock"/>; der
    /// Verbindungsaufbau nimmt nur die zweite. Das Warten darauf bricht mit dem Abbruch des Aufbaus ab.
    /// </para>
    /// </summary>
    /// <param name="connection">Die Verbindung, deren Katalog geprüft wird.</param>
    /// <param name="standing">
    /// <c>true</c>, wenn <paramref name="connection"/> die stehende Verbindung ist, deren Katalog gerade
    /// neu abgefragt wird — ihr Inventar ist das, aus dem ein geändertes Werkzeug sofort verschwinden
    /// muss. <c>false</c> beim Verbindungsaufbau: Dessen Inventar ist noch nicht veröffentlicht, und das
    /// geltende gehört einer anderen Verbindung.
    /// </param>
    private async Task<(UpstreamInventory Inventory, IReadOnlyList<string> Quarantined)>
        ScreenInventoryAsync(
            Entry entry, UpstreamInventory inventory, GuardedUpstreamConnection connection, bool standing,
            CancellationToken ct)
    {
        if (_pins is null)
        {
            return (inventory, []);
        }

        await entry.ScreeningLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!standing)
            {
                // Ab jetzt ist das Urteil über den Katalog dieser Verbindung das jüngste. Eine Neuabfrage
                // der alten, die danach erst zum Prüfen kommt, ist veraltet — auch wenn die alte noch
                // veröffentlicht ist, weil der Neuaufbau einer Sitzung sie erst beim Veröffentlichen ersetzt.
                lock (entry.Gate)
                {
                    entry.ScreenedConnection = connection;
                }
            }

            return await ScreenInventoryCoreAsync(entry, inventory, standing ? connection : null, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            entry.ScreeningLock.Release();
        }
    }

    private async Task<(UpstreamInventory Inventory, IReadOnlyList<string> Quarantined)>
        ScreenInventoryCoreAsync(
            Entry entry, UpstreamInventory inventory, GuardedUpstreamConnection? standing, CancellationToken ct)
    {
        var pins = _pins!;
        var kept = new List<ToolDescriptor>(inventory.Tools.Count);
        var quarantined = new List<string>();
        foreach (var tool in inventory.Tools)
        {
            // Die Prüfung schreibt Pins fort. Eine veraltete Abfrage — ihre Verbindung ist nicht
            // mehr die stehende, oder ihr Aufbau hat die Frist verloren — schreibt nichts mehr; sonst
            // löschte oder ersetzte sie die anstehende Fassung, die die neue Verbindung gemeldet hat,
            // und der Administrator sähe die falsche Änderung (Plan 0005 E7).
            ct.ThrowIfCancellationRequested();
            if (standing is not null)
            {
                lock (entry.Gate)
                {
                    // F7: auch dann veraltet, wenn ein Neuaufbau den Katalog seiner neuen Verbindung
                    // schon geprüft, aber noch nicht veröffentlicht hat.
                    if (!ReferenceEquals(entry.Connection, standing)
                        || !ReferenceEquals(entry.ScreenedConnection, standing))
                    {
                        throw new UpstreamConnectionLostException(
                            "Die Verbindung wurde während der Katalogprüfung ersetzt; diese Prüfung ist veraltet.");
                    }
                }
            }

            ThrowIfPinsRetired(entry);
            var hash = ToolDefinitionHash.Compute(tool);
            var verdict = await pins.VerifyAsync(entry.Id, tool.Name, hash, ct).ConfigureAwait(false);

            // G7: Wurde der Upstream entfernt, während die Prüfung schrieb, hat das Entfernen seine
            // Pins womöglich schon vergessen — und dieser hier kam danach. Er wird mit allen übrigen
            // noch einmal vergessen. Da das Entfernen das Merkzeichen VOR seinem Vergessen setzt, sieht
            // jede Prüfung, deren Schreiben danach endet, es hier.
            if (PinsRetired(entry))
            {
                await ForgetRetiredPinsAsync(entry).ConfigureAwait(false);
                ThrowIfPinsRetired(entry);
            }

            if (verdict is ToolDefinitionVerdict.Changed)
            {
                quarantined.Add(tool.Name);
                // Sofort zurückziehen, nicht erst nach der Prüfung der übrigen (Plan 0005 D3): Hängt
                // oder wirft eine davon, bliebe das geänderte Werkzeug sonst aufrufbar. Und vor dem
                // Audit (C2): Weist das die Zeile ab, gilt der Rückzug trotzdem.
                if (standing is not null)
                {
                    WithdrawTools(entry, [tool.Name], standing);
                }

                continue;
            }

            kept.Add(tool);
        }

        if (quarantined.Count > 0)
        {
            foreach (var name in quarantined)
            {
                Log.ToolDefinitionChanged(_logger, entry.Config.Slug, name);
                // Eigene Audit-Zeile statt nur eines Logeintrags: Eine stillschweigend geänderte
                // Tool-Definition ist ein sicherheitsrelevantes Ereignis, kein Betriebsdetail.
                _audit?.Record(new AuditEvent(
                    _time.GetUtcNow(), Caller: null, CallOrigin.System, AuditEventKind.ServerLifecycle,
                    entry.Id, Tool: NamespacedToolName.Create(entry.Config.Slug, name).Value,
                    Status: null, RedactedArguments: null,
                    RequestBytes: null, ResponseBytes: null, Duration: null,
                    Detail: $"{entry.Config.Slug}: Tool-Definition von '{name}' hat sich geändert "
                        + "— zurückgehalten bis zur Annahme (Rug-Pull-Schutz)."));
            }
        }

        return quarantined.Count == 0
            ? (inventory, [])
            : (inventory with { Tools = kept }, quarantined);
    }

    private static bool PinsRetired(Entry entry)
    {
        lock (entry.Gate)
        {
            return entry.PinsRetired;
        }
    }

    private static void ThrowIfPinsRetired(Entry entry)
    {
        if (PinsRetired(entry))
        {
            throw new UpstreamConnectionLostException(
                "Der Upstream wurde entfernt; seine Werkzeugdefinitionen werden nicht mehr festgehalten.");
        }
    }

    /// <summary>
    /// Vergisst die Pins eines entfernten Upstreams noch einmal — für eine Prüfung, deren Schreiben erst
    /// nach dem Vergessen beim Entfernen endete (G7). Wirft nicht: Der Aufrufer meldet ohnehin, dass der
    /// Upstream fort ist; scheitert das Nachräumen, steht es im Log.
    /// </summary>
    private async Task ForgetRetiredPinsAsync(Entry entry)
    {
        try
        {
            await _pins!.ForgetServerAsync(entry.Id, () => NoSuccessor(entry), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var types = UpstreamFailure.TypeChain(ex);
            Quietly(() => Log.RetiredPinsForgetFailed(_logger, entry.Config.Slug, types));
        }
    }

    /// <summary>
    /// Plan 0005 I1: Die Pins einer <see cref="ServerId"/> gehören dem Eintrag, der unter ihr steht.
    /// Ist dieselbe Id nach dem Entfernen neu eingetragen (Konfigurations-Import mit der Id aus dem
    /// Export), räumt der alte Eintrag nichts mehr — sonst löschte er die Pins, die die erste Prüfung
    /// des Nachfolgers gerade geschrieben hat, und eine geänderte Definition gälte als Erstsichtung.
    /// </summary>
    private bool NoSuccessor(Entry retired)
        => !_entries.TryGetValue(retired.Id, out var current) || ReferenceEquals(current, retired);

    /// <summary>
    /// Nimmt Werkzeuge, deren Definition sich geändert hat, sofort aus dem geltenden Inventar — ehe
    /// irgendetwas anderes scheitern kann. Der Katalog erfährt es über <c>InventoryChanged</c>.
    /// </summary>
    private void WithdrawTools(Entry entry, IReadOnlyList<string> names, GuardedUpstreamConnection standing)
    {
        bool withdrawn;
        lock (entry.Gate)
        {
            // Nur aus dem Inventar der Verbindung, deren Katalog das gemeldet hat (Plan 0005 D5): Ist sie
            // inzwischen ersetzt, hat die neue ihre Werkzeuge selbst geprüft.
            withdrawn = ReferenceEquals(entry.Connection, standing)
                && entry.Inventory is { } current
                && current.Tools.Any(tool => names.Contains(tool.Name, StringComparer.Ordinal));
            if (withdrawn)
            {
                var kept = entry.Inventory!.Tools
                    .Where(tool => !names.Contains(tool.Name, StringComparer.Ordinal)).ToList();
                entry.Inventory = entry.Inventory with { Tools = kept };
                entry.ToolCount = kept.Count;
                entry.QuarantinedTools = entry.QuarantinedTools.Union(names, StringComparer.Ordinal).ToList();
            }
        }

        if (withdrawn)
        {
            Raise(entry, UpstreamChangeKind.InventoryChanged);
        }
    }

    private async Task DisposeQuietlyAsync(GuardedUpstreamConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nur die Typnamen: Die Meldung einer Transport-Ausnahme kann Text des Upstreams oder
            // eine URL mit Token tragen (P1 – P4).
            var types = UpstreamFailure.TypeChain(ex);
            Quietly(() => Log.DisposeFailed(_logger, types));
        }
    }

    /// <summary>
    /// Schreibt eine Logzeile, deren Scheitern nichts weiter aufhalten darf: Was sie meldet, ist
    /// schon geschehen, und eine Log-Ausgabe, die wirft, ist kein Grund, eine Verbindung aufzugeben
    /// oder eine alte offen zu lassen (U4).
    /// </summary>
    private static void Quietly(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // Bewusst verschluckt: Es gibt keinen zweiten Kanal, auf dem sich das melden ließe.
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
    /// <param name="fromAdmin">
    /// Wechsel aus einer Verwaltungsaktion: Deren Audit-Zeile ist schon geschrieben, bevor die
    /// Aktion gespeichert oder wirksam wurde — im Compliance-Modus wird sie dort verweigert (L4).
    /// Hier entsteht deshalb keine zweite. Wechsel des Loops dagegen sind geschehen, ob das Audit
    /// sie annimmt oder nicht; ihre Zeilen werden zurückgestellt statt den Loop zu beenden.
    /// </param>
    private void SetState(
        Entry entry, UpstreamState state, string? error, string? auditDetail = null, bool fromAdmin = false)
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

        if (stateChanged && !fromAdmin)
        {
            var detail = auditDetail ?? (error is null
                ? $"{entry.Config.Slug}: {state}"
                : $"{entry.Config.Slug}: {state} — {error}");
            RecordLoopLifecycle(entry, detail);
        }

        Raise(entry, UpstreamChangeKind.StateChanged);
    }

    /// <summary>
    /// Wie viele abgewiesene Zeilen eines Upstreams höchstens auf das Nachreichen warten. Je Ausfall
    /// entstehen nur eine Handvoll (Eintritt, Wechsel der Fehlerart, Grenze, Rückkehr); die Grenze
    /// hält nur ein Audit, das nie wieder annimmt, davon ab, den Speicher zu füllen.
    /// </summary>
    internal const int MaxDeferredLifecycleLines = 32;

    /// <summary>
    /// FR-22: Der Zustandswechsel eines Upstreams ist ein Systemereignis und gehört ins Audit-Log,
    /// nicht nur in den ILogger — sonst fehlt beim Nachvollziehen eines Ausfalls die Spur.
    /// Für Verwaltungsaktionen: wirft, wenn das Audit nicht annimmt. Zurückgestellte Zeilen des
    /// Loops gehen vorher hinaus, damit die Reihenfolge im Audit stimmt.
    /// </summary>
    private void AuditLifecycle(Entry entry, string detail)
    {
        if (_audit is null)
        {
            return;
        }

        lock (entry.AuditGate)
        {
            if (!FlushDeferredLifecycle(entry))
            {
                // Die Zeile der Aktion stünde sonst vor älteren des Loops — und das Audit nimmt
                // gerade ohnehin nichts an. Die Aktion wird verweigert, bevor sie wirkt (L4).
                throw new InvalidOperationException(
                    $"Das Audit nimmt gerade nicht an; die Aktion an '{entry.Config.Slug}' wird verweigert.");
            }

            _audit.Record(LifecycleEvent(entry, detail));
        }
    }

    /// <summary>
    /// Die Audit-Zeile eines Wechsels, den der Loop festgestellt hat. Wirft nicht wegen des Audits:
    /// Weist es die Zeile ab (im Compliance-Modus bei vollem Kanal), wird sie mit ihrem Zeitpunkt
    /// zurückgestellt und nachgereicht — vor der nächsten Zeile dieses Upstreams und bei jedem
    /// Versuch und jedem Lebenszeichen dazwischen. So fehlt im Audit kein Eintritt in Failed vor dem
    /// „wieder verbunden", und der Loop hängt nicht am Audit (Z1).
    /// <para>
    /// Ist die Warteschlange voll, fällt die <b>älteste</b> Zeile heraus: Die neueren beschreiben
    /// den Zustand, in dem der Upstream jetzt ist.
    /// </para>
    /// </summary>
    private void RecordLoopLifecycle(Entry entry, string detail)
    {
        if (_audit is null)
        {
            return;
        }

        string? droppedDetail = null;
        bool refused;
        int pending;
        lock (entry.AuditGate)
        {
            // Erst nachreichen, dann verdrängen (Codex L1): Hat das Audit inzwischen wieder Platz,
            // muss keine Zeile verloren gehen.
            if (entry.DeferredLifecycle.Count >= MaxDeferredLifecycleLines)
            {
                FlushDeferredLifecycle(entry);
            }

            if (entry.DeferredLifecycle.Count >= MaxDeferredLifecycleLines)
            {
                droppedDetail = entry.DeferredLifecycle[0].Detail;
                entry.DeferredLifecycle.RemoveAt(0);
            }

            entry.DeferredLifecycle.Add(LifecycleEvent(entry, detail));
            refused = !FlushDeferredLifecycle(entry);
            pending = entry.DeferredLifecycle.Count;
        }

        if (droppedDetail is not null)
        {
            // Erst die verworfene Zeile ist ein Verlust, nicht jede Abweisung davor (V3).
            Quietly(() => _audit?.ReportDiscarded(1));
        }

        // Auch die Meldungen hier dürfen nicht werfen: Der Aufrufer schreibt danach womöglich noch
        // eine Audit-Zeile (etwa „eingestellt" nach dem Eintritt in Failed), und die ginge sonst
        // mit verloren — ein späterer Versuch sähe keinen Wechsel mehr, den er nachholen könnte.
        try
        {
            if (droppedDetail is not null)
            {
                // Der Text ist eine Audit-Zeile und damit schon bereinigt (P1 – P4).
                Log.UpstreamAuditLineDropped(_logger, entry.Config.Slug, pending, droppedDetail);
            }

            if (refused)
            {
                Log.UpstreamAuditDeferred(_logger, entry.Config.Slug, pending);
            }
        }
        catch (Exception)
        {
            // Die Zeile selbst steht in der Warteschlange; nur ihre Erwähnung im Log fehlt.
        }
    }

    /// <summary>
    /// Bietet zurückgestellte Zeilen noch einmal an, ohne eine neue zu schreiben. Für jeden Versuch
    /// und jedes Lebenszeichen: Wirft nicht und loggt eine erneute Abweisung nicht — das Zurückstellen
    /// steht schon im Log, und ein Eintrag je Lebenszeichen füllte es.
    /// </summary>
    private void RetryDeferredLifecycle(Entry entry)
    {
        if (_audit is null)
        {
            return;
        }

        lock (entry.AuditGate)
        {
            // Weiter abgewiesen: Die Zeilen bleiben stehen bis zum nächsten Anlauf. Über TryRecord
            // zählt das nicht als Verlust (V3).
            FlushDeferredLifecycle(entry);
        }
    }

    /// <summary>
    /// Der letzte Anlauf, wenn ein Upstream entfernt oder das Gateway beendet wird: was das Audit
    /// dann noch abweist, ist verloren — und steht deshalb als Fehler im Log, statt still zu
    /// verschwinden. Wirft nicht.
    /// </summary>
    private void ReleaseDeferredLifecycle(Entry entry)
    {
        RetryDeferredLifecycle(entry);
        int lost;
        lock (entry.AuditGate)
        {
            lost = entry.DeferredLifecycle.Count;
            entry.DeferredLifecycle.Clear();
        }

        if (lost == 0)
        {
            return;
        }

        // Beim Entfernen und Beenden darf weder die Meldung ans Audit noch die Log-Ausgabe etwas
        // aufhalten.
        Quietly(() => _audit?.ReportDiscarded(lost));
        Quietly(() => Log.UpstreamAuditLinesDiscarded(_logger, entry.Config.Slug, lost));
    }

    /// <summary>
    /// Reicht zurückgestellte Zeilen in ihrer Reihenfolge nach und hält bei der ersten Abweisung
    /// an. <c>true</c>, wenn nichts mehr wartet. Wirft nicht: Angeboten wird über
    /// <see cref="IAuditSink.TryRecord"/>, und eine Abweisung zählt nicht als Verlust (V3).
    /// </summary>
    private bool FlushDeferredLifecycle(Entry entry)
    {
        var deferred = entry.DeferredLifecycle;
        while (deferred.Count > 0)
        {
            if (!_audit!.TryRecord(deferred[0]))
            {
                return false;
            }

            deferred.RemoveAt(0);
        }

        return true;
    }

    private AuditEvent LifecycleEvent(Entry entry, string detail)
        => new(
            _time.GetUtcNow(), Caller: null, CallOrigin.System, AuditEventKind.ServerLifecycle,
            entry.Id, Tool: null, Status: null, RedactedArguments: null,
            RequestBytes: null, ResponseBytes: null, Duration: null,
            Detail: detail);

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
            // Nur die Typnamen: Die Meldung stammt aus fremdem Code und kann alles tragen, was der
            // Handler gerade in der Hand hatte (U6).
            var types = UpstreamFailure.TypeChain(ex);
            Quietly(() => Log.ChangedHandlerThrew(_logger, types));
        }
    }

    private Entry GetEntryOrThrow(ServerId id)
    {
        // Ab dem Herunterfahren gilt für jede Verwaltungsaktion dieselbe Antwort (N17, E8).
        ThrowIfDisposing();
        return _entries.TryGetValue(id, out var entry)
            ? entry
            : throw new KeyNotFoundException($"Upstream-Server {id} ist nicht registriert.");
    }

    /// <summary>
    /// Nimmt die Verwaltungssperre eines Eintrags — und prüft danach, ob es ihn noch gibt (Codex H5).
    /// Eine Anfrage, die hinter dem Entfernen gewartet hat, hielte sonst einen Eintrag in der Hand,
    /// der nirgends mehr steht: Sie schriebe eine neue Version (der Server stünde nach dem Neustart
    /// wieder da) und startete einen Loop, den weder der Status noch das Herunterfahren sieht.
    /// </summary>
    private async Task AcquireAdminAsync(Entry entry, CancellationToken ct)
    {
        ThrowIfDisposing();
        await entry.AdminLock.WaitAsync(ct).ConfigureAwait(false);
        if (entry.Removed)
        {
            entry.AdminLock.Release();
            throw new KeyNotFoundException($"Upstream-Server {entry.Id} ist nicht registriert.");
        }

        // Hat hinter dem Herunterfahren gewartet: Ein Loop, der jetzt noch startete, liefe am
        // Aufräumen vorbei weiter (Plan 0005 D12).
        if (Volatile.Read(ref _disposing) != 0)
        {
            entry.AdminLock.Release();
            ThrowIfDisposing();
        }
    }

    private void ThrowIfDisposing()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);

    /// <summary>
    /// Eine Zulassung umspannt Speichern und Registrieren eines neuen Upstreams (Plan 0005 E8).
    /// Beginnt das Herunterfahren dazwischen, wartet es (höchstens <see cref="StopJoinTimeout"/>), bis
    /// die Zulassung endet — der Upstream ist dann gespeichert, wird aber nicht mehr eingetragen und
    /// kommt erst nach dem Neustart (F10). Wer nach dem Beginn kommt, wird vor dem Speichern abgewiesen.
    /// </summary>
    private void EnterAdmission()
    {
        lock (_admissionGate)
        {
            ThrowIfDisposing();
            _admissions++;
        }
    }

    private void LeaveAdmission()
    {
        lock (_admissionGate)
        {
            if (--_admissions == 0)
            {
                _admissionsDrained?.TrySetResult();
            }
        }
    }

    /// <summary>Setzt den Zaun und wartet auf laufende Zulassungen — höchstens <see cref="StopJoinTimeout"/>.</summary>
    private async Task<bool> CloseAdmissionAsync()
    {
        Task drained;
        lock (_admissionGate)
        {
            if (Interlocked.Exchange(ref _disposing, 1) != 0)
            {
                return false;
            }

            if (_admissions == 0)
            {
                return true;
            }

            _admissionsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _admissionsDrained.Task;
        }

        try
        {
            await drained.WaitAsync(StopJoinTimeout, _time, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Ein Speichern, das hängt, hält das Herunterfahren nicht fest; was danach noch
            // gespeichert wird, wird nicht mehr eingetragen (Register prüft das, F10).
        }

        return true;
    }

    /// <summary>
    /// Trägt einen neuen Eintrag ein und startet seinen Loop — unter seiner Verwaltungssperre und
    /// erst nach dem Blick auf das Herunterfahren. So sieht <see cref="DisposeAsync"/> den Eintrag
    /// entweder und stoppt ihn (es wartet auf die Sperre), oder der Eintrag sieht das Herunterfahren
    /// und startet nicht (Plan 0005 D12).
    /// </summary>
    private void Register(Entry entry, string collision)
    {
        // Läuft immer innerhalb einer Zulassung (E8). Hat das Herunterfahren inzwischen begonnen,
        // wird nichts mehr eingetragen (F10): Kein Status, kein Added-Ereignis, kein Loop — wartet
        // das Herunterfahren nicht mehr (die Frist aus E8 ist um), sähe es den Eintrag nie, und ein
        // Upstream stünde im Status eines Supervisors, der schon beendet ist. Das Speichern davor
        // ist geschehen und wird nicht zurückgenommen: Der Upstream kommt nach dem Neustart wieder.
        // AddAsync meldet deshalb seine Id, ohne zu werfen — die Zusage „gespeichert" stimmt.
        if (Volatile.Read(ref _disposing) != 0)
        {
            return;
        }

        entry.AdminLock.Wait(CancellationToken.None);
        try
        {
            if (!_entries.TryAdd(entry.Id, entry))
            {
                throw new InvalidOperationException(collision);
            }

            Raise(entry, UpstreamChangeKind.Added);
            if (entry.Config.Enabled)
            {
                StartLoop(entry);
            }
        }
        finally
        {
            entry.AdminLock.Release();
        }
    }

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

        /// <summary>Hält die Audit-Zeilen dieses Upstreams in Reihenfolge, auch beim Nachreichen.</summary>
        public object AuditGate { get; } = new();

        /// <summary>Zeilen des Loops, die das Audit abgewiesen hat — in Reihenfolge, mit ihrem Zeitpunkt.</summary>
        public List<AuditEvent> DeferredLifecycle { get; } = [];

        /// <summary>Über dieses Objekt lässt eine Verbindung ihre verlorene Sitzung neu aufbauen.</summary>
        public IUpstreamSessionRecovery Recovery { get; }

        /// <summary>Tools, deren Definition sich geändert hat und die deshalb zurückgehalten werden.</summary>
        public IReadOnlyList<string> QuarantinedTools { get; set; } = [];

        public SemaphoreSlim AdminLock { get; } = new(1, 1);

        /// <summary>Entfernt; gesetzt und gelesen nur unter <see cref="AdminLock"/>.</summary>
        public bool Removed { get; set; }

        /// <summary>Eine Katalog-Neuabfrage nach der anderen (Plan 0005 D3).</summary>
        public SemaphoreSlim RediscoveryLock { get; } = new(1, 1);

        /// <summary>
        /// Eine Pin-Prüfung nach der anderen, beim Aufbau wie bei der Neuabfrage (F7). Wird nach
        /// <see cref="RediscoveryLock"/> genommen, nie davor.
        /// </summary>
        public SemaphoreSlim ScreeningLock { get; } = new(1, 1);

        /// <summary>
        /// Die Verbindung, deren Katalog zuletzt beim Verbindungsaufbau geprüft wurde (F7). Eine
        /// Neuabfrage auf einer anderen ist veraltet. Nur unter <see cref="Gate"/>.
        /// </summary>
        public GuardedUpstreamConnection? ScreenedConnection { get; set; }

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

        /// <summary>
        /// Gesetzt vom Verbuchen eines Fehlversuchs bis zum Ende des Wartens; „Jetzt verbinden" löst
        /// es aus. Kommt der Klick vor dem Warten, endet das Warten sofort.
        /// </summary>
        public TaskCompletionSource? Wake { get; set; }

        /// <summary>Ob der Loop gerade wirklich wartet — erst nach aller Buchführung zum letzten Versuch.</summary>
        public bool LoopWaiting { get; set; }

        /// <summary>Das Lebenszeichen hat ein abgewiesenes Token gemeldet; ServeAsync erneuert es (V1). Unter <see cref="Gate"/>.</summary>
        public bool CredentialRenewalRequested { get; set; }

        /// <summary>
        /// Eine Erneuerung wegen eines abgewiesenen Tokens (V1) hat noch nicht getragen: Seitdem war
        /// keine Verbindung eine Minute ununterbrochen gesund. Ein 401 in diesem Zustand ist eine
        /// abgelehnte Anmeldung (E2). Unter <see cref="Gate"/>; ein Eingriff (StartLoop) setzt zurück.
        /// </summary>
        public bool RenewalUnconfirmed { get; set; }

        /// <summary>Neue Zugangsdaten sind gespeichert, während ein Versuch lief (Plan 0005 D11). Unter <see cref="Gate"/>.</summary>
        public bool CredentialsChanged { get; set; }

        /// <summary>
        /// Ein Verbindungsaufbau läuft oder ist gescheitert, aber noch nicht verbucht — unabhängig vom
        /// angezeigten Zustand (G2): Der Neuaufbau einer Sitzung steht auf Degraded. Unter <see cref="Gate"/>.
        /// </summary>
        public bool AttemptRunning { get; set; }

        /// <summary>
        /// Der Upstream ist entfernt; seine Pins sind vergessen oder werden es gleich (G7). Eine Prüfung,
        /// die danach noch schreibt, räumt selbst nach. Unter <see cref="Gate"/>.
        /// </summary>
        public bool PinsRetired { get; set; }

        /// <summary>
        /// Eine ausgelöste Katalogabfrage wurde mit 401/403 abgewiesen (E5). Der Loop der stehenden
        /// Verbindung liest es nach dem Wecken und beendet die Verbindung. Nur unter <see cref="Gate"/>.
        /// </summary>
        public Exception? CatalogRefused { get; set; }

        /// <summary>Zeitpunkte der Sitzungs-Neuaufbauten in den letzten fünf Minuten (V2); unter <see cref="Gate"/>.</summary>
        public Queue<DateTimeOffset> RecentRebuilds { get; } = new();

        /// <summary>Verbindungsaufbauten, die ihre Frist überschritten haben und noch nicht zurück sind (U7).</summary>
#pragma warning disable SA1401 // Feld statt Eigenschaft: Interlocked braucht eine Referenz.
        public int PendingLateConnects;
#pragma warning restore SA1401

        /// <summary>
        /// Gesetzt, solange der Loop gestoppt wird (Drain eingeschlossen). Eine Sitzung, die in dieser
        /// Zeit verloren geht, wird nicht mehr neu aufgebaut (R7).
        /// </summary>
        public bool Stopping { get; set; }

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

        public ToolAvailability CheckTool(GuardedUpstreamConnection connection, string toolName)
        {
            lock (entry.Gate)
            {
                // Verbindung und Katalog werden gemeinsam unter Gate veröffentlicht; nur für die
                // aktuelle Verbindung ist entry.Inventory ihr eigener Katalog (E14).
                if (!ReferenceEquals(entry.Connection, connection))
                {
                    return ToolAvailability.Stale;
                }

                return entry.Inventory?.Tools.Any(
                    tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal)) == true
                    ? ToolAvailability.Available
                    : ToolAvailability.Withheld;
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

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: Der Fehlversuch konnte nicht vollständig verbucht werden. Die Wiederverbindung läuft trotzdem weiter. [{ExceptionTypes}] Nächster Versuch in {Delay}.")]
        public static partial void UpstreamBookkeepingFailed(
            ILogger logger, string slug, string exceptionTypes, TimeSpan delay);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: Der Fehlversuch konnte nicht vollständig verbucht werden. [{ExceptionTypes}] Die Wiederverbindung ist nach {Attempts} Versuchen eingestellt — die Obergrenze ist ausdrücklich so konfiguriert. Weiter geht es nur über „Jetzt verbinden“.")]
        public static partial void UpstreamBookkeepingFailedAtLimit(
            ILogger logger, string slug, string exceptionTypes, int attempts);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Das Audit hat eine Zustandszeile nicht angenommen; sie wird bei jedem Versuch, jedem Lebenszeichen und vor der nächsten Zeile erneut angeboten ({Pending} zurückgestellt).")]
        public static partial void UpstreamAuditDeferred(ILogger logger, string slug, int pending);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: Die älteste zurückgestellte Zustandszeile fürs Audit ist verworfen — zu viele warten aufs Nachreichen ({Pending}). Verworfen: {Detail}")]
        public static partial void UpstreamAuditLineDropped(ILogger logger, string slug, int pending, string? detail);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: {Count} Zustandszeilen fürs Audit sind verloren — das Audit nahm sie bis zum Entfernen oder Beenden nicht an.")]
        public static partial void UpstreamAuditLinesDiscarded(ILogger logger, string slug, int count);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: Die Wiederverbindungsschleife war an einem Fehler geendet. [{ExceptionTypes}]")]
        public static partial void UpstreamLoopFaulted(ILogger logger, string slug, string exceptionTypes);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: Die Schleife endete nach dem Abbruch nicht binnen {Timeout} — sie beachtet ihren Abbruch nicht. Der Upstream gilt trotzdem als gestoppt.")]
        public static partial void UpstreamLoopHung(ILogger logger, string slug, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: {Step} beim Stoppen endete nicht binnen {Timeout}; er läuft im Hintergrund zu Ende. Der Upstream gilt trotzdem als gestoppt.")]
        public static partial void UpstreamStopStepHung(ILogger logger, string slug, string step, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: Das Schließen der verlorenen Verbindung endete nicht binnen {Timeout}; es läuft im Hintergrund zu Ende. Die Wiederverbindung geht weiter.")]
        public static partial void UpstreamCloseHung(ILogger logger, string slug, TimeSpan timeout);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Drain-Frist {Grace} abgelaufen, {InFlight} Calls werden abgebrochen.")]
        public static partial void DrainExpired(ILogger logger, string slug, TimeSpan grace, int inFlight);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "Fehler beim Schließen einer Upstream-Verbindung (ignoriert). [{ExceptionTypes}]")]
        public static partial void DisposeFailed(ILogger logger, string exceptionTypes);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Changed-Handler warf eine Exception — Handler müssen exception-frei sein. [{ExceptionTypes}]")]
        public static partial void ChangedHandlerThrew(ILogger logger, string exceptionTypes);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: Der Upstream hat das Zugriffstoken abgewiesen — es wird erneuert und sofort neu verbunden.")]
        public static partial void UpstreamCredentialsRenewing(ILogger logger, string slug);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Upstream {Slug}: {Count} Verbindungsaufbauten hängen nach Ablauf ihrer Frist noch — "
                + "der Connector beachtet seinen Abbruch nicht. Jeder hält bis zu seinem Ende Ressourcen "
                + "(Prozess, Socket).")]
        public static partial void LateConnectsPiling(ILogger logger, string slug, int count);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug}: Nach dem Entfernen ließ sich ein spät geschriebener Pin nicht vergessen; er steht womöglich noch in der Ablage. [{ExceptionTypes}]")]
        public static partial void RetiredPinsForgetFailed(ILogger logger, string slug, string exceptionTypes);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Upstream {Slug} ließ sich beim Beenden des Gateways nicht sauber stoppen. [{ExceptionTypes}]")]
        public static partial void UpstreamDisposeFailed(ILogger logger, string slug, string exceptionTypes);
    }
}