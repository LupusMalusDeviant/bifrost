using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;

namespace Bifrost.Core.Execution;

/// <summary>
/// Die Umsetzung von <see cref="IHostExecutionPolicy"/> (ADR-0025 E1). Sie beantwortet genau eine
/// Frage und trägt einen stabilen Reason-Code, damit die Begründung Umformulierungen überlebt.
/// <para>
/// Die Policy <b>entscheidet</b> nichts über den Zustand der Instanz — sie liest ihn aus einem
/// <see cref="HostExecutionState"/>, den <see cref="HostExecutionCoordinator"/> einmal ermittelt.
/// Zwei Stellen, die dieselbe Bestandsentscheidung treffen, treffen sie irgendwann verschieden
/// (M3-Vertrag §3).
/// </para>
/// </summary>
public sealed class HostExecutionPolicy : IHostExecutionPolicy
{
    /// <summary>
    /// Die Policy einer Instanz, die noch nichts ermittelt hat: verbietet native Ausführung mit
    /// <see cref="HostExecutionReason.Undetermined"/>.
    /// <para>
    /// <b>Das ist der Rückfall, wenn irgendwo keine Policy gereicht wurde.</b> Ein vergessener
    /// Verdrahtungsschritt führt damit zu einer Absage mit Begründung — und nicht zu einem
    /// Startweg, der die Prüfung nicht kennt.
    /// </para>
    /// </summary>
    public static HostExecutionPolicy Unresolved { get; } = new(HostExecutionState.Unresolved);

    public HostExecutionPolicy(HostExecutionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
    }

    public HostExecutionState State { get; }

    /// <summary>Eine Policy, die native Ausführung ausdrücklich erlaubt — für Tests und Werkzeuge.</summary>
    public static HostExecutionPolicy AllowedByOperator()
        => new(new HostExecutionState(
            true,
            HostExecutionOrigin.Environment,
            HostExecutionReason.Allowed,
            [],
            $"{HostExecutionSwitch.Name}=true — native Ausführung ist ausdrücklich erlaubt."));

    /// <summary>Die Vorgabe einer frischen Instanz: native Ausführung ist verboten (ADR-0025 E2).</summary>
    public static HostExecutionPolicy FreshInstance()
        => new(new HostExecutionState(
            false,
            HostExecutionOrigin.FreshInstanceDefault,
            HostExecutionReason.Forbidden,
            [],
            $"{HostExecutionSwitch.Name} ist nicht gesetzt; für eine neue Instanz gilt die Vorgabe: verboten."));

    [NoHostExecution("Das ist die Entscheidung selbst, nicht ein Weg zu ihr vorbei.")]
    public HostExecutionDecision Evaluate(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!NativeExecution.RunsOnHost(config))
        {
            return new HostExecutionDecision(
                true,
                HostExecutionReason.NotNative,
                $"Upstream '{config.Slug}' läuft isoliert ({config.Kind}); die Host-Policy ist nicht betroffen.");
        }

        return State.Origin switch
        {
            HostExecutionOrigin.Unresolved or HostExecutionOrigin.Unreadable => new HostExecutionDecision(
                false,
                HostExecutionReason.Undetermined,
                $"Für '{config.Slug}' ließ sich nicht feststellen, ob native Ausführung erlaubt ist: {State.Note}",
                $"Die Einstellung {HostExecutionSwitch.Name} ausdrücklich auf true oder false setzen. "
                + "Solange die Frage offen ist, startet nichts nativ — das ist Absicht."),

            _ when !State.Allowed => new HostExecutionDecision(
                false,
                HostExecutionReason.Forbidden,
                $"Upstream '{config.Slug}' würde nativ auf dem Host laufen; das ist auf dieser Instanz verboten.",
                $"Den Upstream auf Container-Isolation umstellen (Cli.Isolation.Mode=Container) — oder, "
                + $"wenn das Programm ausdrücklich vertrauenswürdig ist, {HostExecutionSwitch.Name}=true setzen. "
                + "Ein nativ gestartetes Programm läuft mit den Rechten des Gateways und damit am Schlüsselring."),

            HostExecutionOrigin.AdoptedFromExistingInstance => new HostExecutionDecision(
                true,
                HostExecutionReason.AdoptedFromExistingInstance,
                $"Upstream '{config.Slug}' läuft nativ, weil diese Instanz ihren bisherigen Zustand "
                + "übernommen hat — nicht, weil jemand es entschieden hat.",
                "Die Übernahme ist kein Dauerzustand: Upstreams auf Container-Isolation umstellen und "
                + $"{HostExecutionSwitch.Name} anschließend auf false setzen."),

            _ => new HostExecutionDecision(
                true,
                HostExecutionReason.Allowed,
                $"Upstream '{config.Slug}' darf nativ laufen: {State.Note}"),
        };
    }
}
