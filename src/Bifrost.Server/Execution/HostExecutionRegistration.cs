using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Core.Execution;

using Microsoft.Extensions.Logging;

namespace Bifrost.Server.Execution;

/// <summary>
/// Verdrahtung der Ausführungs-Policy (ADR-0025, WP3.1). Eine Registrierung, ein Koordinator, eine
/// Entscheidung — <see cref="IHostExecutionPolicy"/> zeigt auf dasselbe Objekt wie der
/// <see cref="HostExecutionCoordinator"/>, damit es keine zweite Meinung im Prozess gibt.
/// </summary>
public static class HostExecutionRegistration
{
    public static IServiceCollection AddBifrostHostExecution(
        this IServiceCollection services, string dataDirectory, string? environmentValue)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        services.AddSingleton<IHostExecutionSettingStore>(
            _ => new HostExecutionSettingFile(dataDirectory));
        services.AddSingleton(sp => new HostExecutionCoordinator(
            sp.GetRequiredService<IHostExecutionSettingStore>(),
            environmentValue,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IHostExecutionPolicy>(
            sp => sp.GetRequiredService<HostExecutionCoordinator>());
        services.AddSingleton<HostExecutionStartup>();

        return services;
    }
}

/// <summary>
/// Der Startschritt: Zustand ermitteln, Übernahme festschreiben, sie hörbar machen.
/// <para>
/// Er läuft <b>vor</b> dem Wiederherstellen der Upstreams. Andersherum wäre die Reihenfolge selbst
/// der Fehler: Der Koordinator verbietet, solange er nichts ermittelt hat — eine bestehende Instanz
/// käme dann nicht hoch, und genau das verbietet ADR-0025 E3.
/// </para>
/// </summary>
public sealed partial class HostExecutionStartup
{
    private readonly HostExecutionCoordinator _coordinator;
    private readonly IAuditSink _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<HostExecutionStartup> _logger;

    public HostExecutionStartup(
        HostExecutionCoordinator coordinator,
        IAuditSink audit,
        TimeProvider time,
        ILogger<HostExecutionStartup> logger)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _coordinator = coordinator;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Ermittelt den Zustand aus den bereits geladenen Konfigurationen und protokolliert das
    /// Ergebnis. Idempotent — ein zweiter Aufruf ändert nichts und meldet nichts erneut.
    /// </summary>
    [NoHostExecution(
        "Ermittelt den Zustand der Instanz und protokolliert ihn. Der Schritt startet nichts — "
        + "er entscheidet, was danach starten darf.")]
    public HostExecutionState Run(IReadOnlyDictionary<ServerId, UpstreamConfigVersion> persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        if (_coordinator.IsResolved)
        {
            return _coordinator.State;
        }

        var state = _coordinator.Resolve([.. persisted.Values.Select(version => version.Config)]);

        var names = string.Join(", ", state.HostUpstreams);
        if (state.Adopted)
        {
            // Ein Audit-Eintrag, der jeden betroffenen Upstream namentlich nennt (ADR-0025 E3,
            // Punkt 3). Ohne die Namen wäre der Eintrag eine Notiz; mit ihnen ist er eine Arbeitsliste.
            _audit.Record(new AuditEvent(
                _time.GetUtcNow(), Caller: null, CallOrigin.System, AuditEventKind.ConfigChanged,
                Server: null, Tool: null, Status: null, RedactedArguments: null,
                RequestBytes: null, ResponseBytes: null, Duration: null,
                Detail: "Ausfuehrungs-Policy: Der bisherige Zustand dieser Instanz wurde uebernommen "
                    + $"({HostExecutionSwitch.Name}=true, Grund {state.ReasonCode}). Nativ laufende "
                    + $"Upstreams: {names}."));
            Log.Adopted(_logger, state.HostUpstreams.Count, names);
        }
        else
        {
            _audit.Record(new AuditEvent(
                _time.GetUtcNow(), Caller: null, CallOrigin.System, AuditEventKind.ConfigChanged,
                Server: null, Tool: null, Status: null, RedactedArguments: null,
                RequestBytes: null, ResponseBytes: null, Duration: null,
                Detail: $"Ausfuehrungs-Policy ermittelt: native Ausfuehrung "
                    + $"{(state.Allowed ? "erlaubt" : "verboten")} (Herkunft {state.Origin}, "
                    + $"Grund {state.ReasonCode})."));
            Log.Resolved(_logger, state.Allowed, state.Origin, state.ReasonCode);
        }

        return state;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 3101, Level = LogLevel.Warning,
            Message = "Ausfuehrungs-Policy: bisheriger Zustand uebernommen, {Count} Upstream(s) laufen "
                + "weiterhin nativ auf dem Host: {Upstreams}. Die Instanz laeuft weiter, ist dadurch "
                + "aber nicht sicherer — auf Container-Isolation umstellen.")]
        public static partial void Adopted(ILogger logger, int count, string upstreams);

        [LoggerMessage(EventId = 3102, Level = LogLevel.Information,
            Message = "Ausfuehrungs-Policy ermittelt: native Ausfuehrung erlaubt={Allowed} "
                + "(Herkunft {Origin}, Grund {ReasonCode}).")]
        public static partial void Resolved(
            ILogger logger, bool allowed, HostExecutionOrigin origin, string reasonCode);
    }
}
