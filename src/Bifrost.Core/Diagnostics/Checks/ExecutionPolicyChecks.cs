using Bifrost.Abstractions.Operations;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Diagnostics.Checks;

/// <summary>
/// Der Zustand der Ausführungs-Policy im Diagnosebericht (ADR-0025 E2, WP3.1 Punkt 5).
/// <para>
/// Der Bericht ist die Stelle, an der ein Betreiber nachliest, wie seine Instanz tatsächlich
/// eingestellt ist. Eine Sicherheitsvorgabe, die nur im Changelog steht, ist eine Behauptung.
/// </para>
/// </summary>
public sealed class HostExecutionPolicyCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.HostExecutionPolicy;

    public DiagnosticScope Scope => DiagnosticScope.Configuration;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HostExecution is not { } state)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code,
                "Die Ausführungs-Policy ist in diesem Lauf nicht ermittelbar — sie steht erst im "
                + "laufenden Gateway fest."));
        }

        var details = CheckOutcome.Details(
            ("einstellung", HostExecutionSwitch.Name),
            ("native_ausfuehrung", state.Allowed ? "erlaubt" : "verboten"),
            ("herkunft", state.Origin.ToString()),
            ("reason_code", state.ReasonCode),
            ("native_upstreams", state.HostUpstreams.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        if (state.Origin is HostExecutionOrigin.Unresolved or HostExecutionOrigin.Unreadable)
        {
            return Task.FromResult(CheckOutcome.Fail(
                Code,
                $"Die Ausführungs-Policy steht nicht fest: {state.Note}",
                $"{HostExecutionSwitch.Name} ausdrücklich auf true oder false setzen. Solange die Frage "
                + "offen ist, kommt kein nativ laufender Upstream hoch — das ist Absicht.",
                details));
        }

        if (!state.Allowed)
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code,
                $"Native Host-Ausführung ist verboten ({state.Note})",
                details));
        }

        if (state.Adopted)
        {
            // Der Befund selbst steht in BFR-POL-0011; hier bleibt es bei der Zustandsangabe, damit
            // derselbe Sachverhalt nicht zweimal als Warnung im Bericht steht.
            return Task.FromResult(CheckOutcome.Pass(
                Code,
                "Native Host-Ausführung ist erlaubt, weil diese Instanz ihren bisherigen Zustand "
                + "übernommen hat (siehe " + DiagnosticCodes.HostExecutionAdoption + ").",
                details));
        }

        return Task.FromResult(state.HostUpstreams.Count == 0
            ? CheckOutcome.Pass(
                Code,
                $"Native Host-Ausführung ist erlaubt, wird aber von keinem Upstream genutzt ({state.Note})",
                details)
            : CheckOutcome.Warning(
                Code,
                $"Native Host-Ausführung ist ausdrücklich erlaubt; {state.HostUpstreams.Count} Upstream(s) "
                + $"nutzen sie: {string.Join(", ", state.HostUpstreams)}.",
                "Ein nativ gestartetes Programm läuft mit den Rechten des Gateways und damit am "
                + "Schlüsselring aller Upstreams. Wo möglich auf Container-Isolation umstellen "
                + $"(Cli.Isolation.Mode=Container) und {HostExecutionSwitch.Name} danach auf false setzen.",
                details));
    }
}

/// <summary>
/// Die Bestandsübernahme als eigener Befund (ADR-0025 E3, Punkt 3).
/// <para>
/// <b>Warum ein eigener Code:</b> „erlaubt, weil jemand das wollte" und „erlaubt, weil es schon
/// immer so lief" sind verschiedene Aussagen, und nur die zweite verlangt eine Handlung. Ein
/// gemeinsamer Befund hätte beide zu derselben Zeile gemacht — und die Handlung wäre in der Menge
/// der grünen Häkchen verschwunden.
/// </para>
/// </summary>
public sealed class HostExecutionAdoptionCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.HostExecutionAdoption;

    public DiagnosticScope Scope => DiagnosticScope.Configuration;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HostExecution is not { } state)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code, "Die Ausführungs-Policy ist in diesem Lauf nicht ermittelbar."));
        }

        if (!state.Adopted)
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code, "Diese Instanz hat keinen Bestandszustand übernommen."));
        }

        // Namentlich. Eine Warnung über „3 Upstreams" verlangt vom Betreiber die Arbeit, die der
        // Gateway bereits getan hat — und sie ist der Grund, warum diese Warnung überhaupt existiert.
        var details = CheckOutcome.Details(
            ("uebernommen_am_start", "ja"),
            ("betroffene_upstreams", string.Join(", ", state.HostUpstreams)));

        return Task.FromResult(CheckOutcome.Warning(
            Code,
            $"Diese Instanz führt {state.HostUpstreams.Count} Upstream(s) nativ auf dem Host aus, weil "
            + "beim Start ihr bisheriger Zustand übernommen wurde — nicht, weil jemand das entschieden "
            + $"hat: {string.Join(", ", state.HostUpstreams)}.",
            "Die Übernahme hält die Instanz am Laufen, macht sie aber nicht sicherer. Die genannten "
            + "Upstreams auf Container-Isolation umstellen (Cli.Isolation.Mode=Container) und "
            + $"{HostExecutionSwitch.Name} anschließend auf false setzen. Wer nativ ausführen will, "
            + $"setzt {HostExecutionSwitch.Name}=true — dann steht die Entscheidung als Entscheidung da.",
            details));
    }
}
