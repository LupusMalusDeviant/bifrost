using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;

namespace Bifrost.Core.Execution;

/// <summary>
/// Der <b>einzige</b> Weg, auf dem eine bestehende Instanz ihren Zustand übernimmt (ADR-0025 E3,
/// M3-Vertrag §3). Er wird einmal beim Start gegangen, das Ergebnis festgeschrieben, und danach
/// beantwortet dieselbe Instanz jede Policy-Frage aus demselben Zustand.
/// <para>
/// Ein zweiter Umstellungsweg wäre schlimmer als keiner: Zwei Stellen, die dieselbe
/// Bestandsentscheidung treffen, treffen sie irgendwann verschieden.
/// </para>
/// <para>
/// <b>Vor der Ermittlung verbietet dieser Koordinator alles</b>
/// (<see cref="HostExecutionReason.Undetermined"/>). Das ist kein Randfall, sondern die Absicherung
/// gegen eine Reihenfolge, in der Upstreams starten, bevor jemand gefragt hat.
/// </para>
/// </summary>
public sealed class HostExecutionCoordinator : IHostExecutionPolicy
{
    private readonly IHostExecutionSettingStore _store;
    private readonly string? _environmentValue;
    private readonly TimeProvider _time;
    private readonly Lock _sync = new();

    private HostExecutionPolicy _policy = HostExecutionPolicy.Unresolved;
    private bool _resolved;

    /// <param name="store">Wo der festgeschriebene Wert liegt.</param>
    /// <param name="environmentValue">
    /// Der Rohwert von <see cref="HostExecutionSwitch.Name"/>. Wird übergeben statt selbst gelesen,
    /// damit die Regel prüfbar ist, ohne das Prozessumfeld eines Testlaufs anzufassen — dieselbe
    /// Trennung wie in <c>LegacyEnvironment.PlanAdoption</c>.
    /// </param>
    public HostExecutionCoordinator(
        IHostExecutionSettingStore store, string? environmentValue, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _environmentValue = environmentValue;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Der ermittelte Zustand. Vor <see cref="Resolve"/> ist das der Unresolved-Zustand.</summary>
    public HostExecutionState State => _policy.State;

    /// <summary>Wurde bereits ermittelt?</summary>
    public bool IsResolved
    {
        get
        {
            lock (_sync)
            {
                return _resolved;
            }
        }
    }

    /// <summary>
    /// Ermittelt den Zustand aus Einstellung, geschriebenem Wert und den <b>vorhandenen</b>
    /// Upstreams — und schreibt eine Übernahme fest. Idempotent: Ein zweiter Aufruf liefert das
    /// Ergebnis des ersten, ohne erneut zu schreiben.
    /// </summary>
    /// <param name="existing">
    /// Die persistierten Upstream-Konfigurationen dieser Instanz. Leer heißt „frische Instanz" —
    /// genau die Unterscheidung, an der ADR-0025 E2 und E3 auseinandergehen.
    /// </param>
    [NoHostExecution("Ermittelt den Zustand der Instanz; startet nichts.")]
    public HostExecutionState Resolve(IReadOnlyCollection<UpstreamServerConfig> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        lock (_sync)
        {
            if (_resolved)
            {
                return _policy.State;
            }

            var state = Decide(existing, out var toWrite);
            if (toWrite is not null)
            {
                // Aus einer unsichtbaren Vorgabe wird ein sichtbarer Wert, den jemand ändern kann
                // (ADR-0025 E3, Punkt 2). Schlägt das Schreiben fehl, läuft die Instanz trotzdem
                // weiter — sie stillzulegen, weil eine Datei nicht angelegt werden konnte, wäre
                // genau der Ausfall, den dieses ADR verhindern soll. Der Fehlschlag steht im
                // Zustand und damit in Diagnose und Audit.
                try
                {
                    _store.Write(toWrite);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    state = state with
                    {
                        Note = state.Note
                            + $" Der Wert konnte nicht nach '{_store.Location}' geschrieben werden "
                            + $"({exception.GetType().Name}) — die Übernahme wiederholt sich beim nächsten Start.",
                    };
                }
            }

            _policy = new HostExecutionPolicy(state);
            _resolved = true;
            return state;
        }
    }

    [NoHostExecution("Das ist die Entscheidung selbst, nicht ein Weg zu ihr vorbei.")]
    public HostExecutionDecision Evaluate(UpstreamServerConfig config)
    {
        HostExecutionPolicy policy;
        lock (_sync)
        {
            policy = _policy;
        }

        return policy.Evaluate(config);
    }

    /// <summary>
    /// Die Regel als reine Rechnung — ohne Uhr, ohne Datei, ohne Prozessumfeld. Alles, was hier
    /// entschieden wird, lässt sich einzeln prüfen.
    /// </summary>
    private HostExecutionState Decide(
        IReadOnlyCollection<UpstreamServerConfig> existing, out HostExecutionSettingRecord? toWrite)
    {
        toWrite = null;
        var hostUpstreams = NativeExecution.DescribeAll(existing);

        // 1. Die ausdrückliche Einstellung gewinnt immer. Wer sie gesetzt hat, hat entschieden —
        //    und eine Übernahme würde diese Entscheidung überschreiben.
        var setting = HostExecutionSwitch.Parse(_environmentValue);
        switch (setting)
        {
            case HostExecutionSwitchValue.True:
                return new HostExecutionState(
                    true, HostExecutionOrigin.Environment, HostExecutionReason.Allowed, hostUpstreams,
                    $"{HostExecutionSwitch.Name}=true — native Ausführung ist ausdrücklich erlaubt.");

            case HostExecutionSwitchValue.False:
                return new HostExecutionState(
                    false, HostExecutionOrigin.Environment, HostExecutionReason.Forbidden, hostUpstreams,
                    $"{HostExecutionSwitch.Name}=false — native Ausführung ist ausdrücklich verboten.");

            case HostExecutionSwitchValue.Invalid:
                return new HostExecutionState(
                    false, HostExecutionOrigin.Unreadable, HostExecutionReason.Undetermined, hostUpstreams,
                    $"{HostExecutionSwitch.Name} ist gesetzt, aber der Wert ist nicht deutbar. "
                    + "Ein unverständlicher Wert wird weder als Abschaltung gelesen noch ignoriert.");
        }

        // 2. Der festgeschriebene Wert. Auch eine frühere Übernahme steht hier — mit ihrer Herkunft,
        //    damit die Warnung nicht nach dem ersten Neustart verschwindet.
        HostExecutionSettingRecord? persisted;
        try
        {
            persisted = _store.Read();
        }
        catch (HostExecutionSettingException exception)
        {
            return new HostExecutionState(
                false, HostExecutionOrigin.Unreadable, HostExecutionReason.Undetermined, hostUpstreams,
                exception.Message);
        }

        if (persisted is not null)
        {
            var origin = persisted.Origin is HostExecutionOrigin.AdoptedFromExistingInstance
                ? HostExecutionOrigin.AdoptedFromExistingInstance
                : HostExecutionOrigin.Persisted;

            return new HostExecutionState(
                persisted.Allowed,
                origin,
                persisted.Allowed
                    ? (origin is HostExecutionOrigin.AdoptedFromExistingInstance
                        ? HostExecutionReason.AdoptedFromExistingInstance
                        : HostExecutionReason.Allowed)
                    : HostExecutionReason.Forbidden,
                // Die Namen aus dem Ist-Zustand, nicht aus dem Eintrag: Was heute nativ läuft, ist
                // das, was der Betreiber umstellen muss.
                hostUpstreams,
                $"Festgeschriebener Wert aus '{_store.Location}': {HostExecutionSwitch.Format(persisted.Allowed)} "
                + $"({persisted.Note})");
        }

        // 3. Keine Einstellung, kein Wert — jetzt entscheidet der Bestand (ADR-0025 E3).
        if (hostUpstreams.Count > 0)
        {
            var note = $"Beim Start liefen {hostUpstreams.Count} Upstream(s) nativ auf dem Host und es gab "
                + "keine ausdrückliche Einstellung. Der bisherige Zustand wurde übernommen, damit die "
                + "Instanz weiterläuft — nicht, weil er richtig wäre.";

            toWrite = new HostExecutionSettingRecord(
                true, HostExecutionOrigin.AdoptedFromExistingInstance, _time.GetUtcNow(), hostUpstreams, note);

            return new HostExecutionState(
                true,
                HostExecutionOrigin.AdoptedFromExistingInstance,
                HostExecutionReason.AdoptedFromExistingInstance,
                hostUpstreams,
                note);
        }

        // 4. Frische Instanz: verboten (ADR-0025 E2). Auch das wird festgeschrieben — sonst bliebe
        //    die Vorgabe eine Abwesenheit, und der Bestandsfall könnte beim nächsten Start doch noch
        //    greifen, wenn jemand inzwischen einen Host-Upstream angelegt hätte.
        var freshNote = $"{HostExecutionSwitch.Name} war nicht gesetzt und es gab keine nativ laufenden "
            + "Upstreams. Für eine neue Instanz gilt die Vorgabe: native Ausführung ist verboten.";
        toWrite = new HostExecutionSettingRecord(
            false, HostExecutionOrigin.FreshInstanceDefault, _time.GetUtcNow(), [], freshNote);

        return new HostExecutionState(
            false, HostExecutionOrigin.FreshInstanceDefault, HostExecutionReason.Forbidden, [], freshNote);
    }
}
