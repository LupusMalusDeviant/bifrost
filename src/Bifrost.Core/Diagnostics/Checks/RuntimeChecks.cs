using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Diagnostics.Checks;

/// <summary>
/// BFR-RT-0001 — Container-Runtime vorhanden <b>und</b> in einem Modus, der die Policy trägt.
/// <para>
/// Geprüft wird nicht nur, ob die Runtime antwortet: Docker im Windows-Container-Modus antwortet
/// bereitwillig und lehnt dann <c>--read-only</c>, <c>--cap-drop</c> und <c>--user</c> ab. Ein
/// CLI-Upstream im Container-Modus kommt dort nicht hoch — es gibt keinen stillen Rückfall auf den
/// Host (ADR-0018).
/// </para>
/// <para>
/// Fehlt die Runtime und verlangt <i>kein</i> Upstream Container-Isolation, ist das kein Befund
/// sondern ein <c>Skipped</c>: Eine Warnung, die beim korrekten Aufbau mitläuft, wird ignoriert.
/// </para>
/// </summary>
public sealed class ContainerRuntimeCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.ContainerRuntime;

    public DiagnosticScope Scope => DiagnosticScope.Runtime;

    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public async Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var runtime = context.ContainerRuntimeName;
        var required = context.ContainerIsolationConfigured;
        if (required == false)
        {
            return CheckOutcome.Skipped(
                Code, "Kein Upstream verlangt Container-Isolation; eine Container-Runtime wird nicht gebraucht.");
        }

        var details = CheckOutcome.Details(
            ("runtime", runtime),
            ("wird_gebraucht", required is null ? "unbekannt" : "ja"));

        // Die Plattform des SERVERS, nicht des Clients — Docker Desktop auf Windows kann beides.
        var probe = await context.Processes
            .RunAsync(runtime, ["version", "--format", "{{.Server.Os}}"], TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);

        if (!probe.Started || probe.ExitCode != 0)
        {
            var summary = $"Die Container-Runtime '{runtime}' antwortet nicht"
                + (probe.Failure is null ? "." : $": {probe.Failure}");
            return required == true
                ? CheckOutcome.Fail(
                    Code,
                    summary,
                    $"Mindestens ein Upstream verlangt Container-Isolation. Ohne erreichbare "
                    + $"Runtime kommt er nicht hoch — es gibt keinen Rückfall auf den Host. "
                    + $"'{runtime}' installieren bzw. den Zugriff auf den Socket freigeben.",
                    details)
                : CheckOutcome.Skipped(
                    Code,
                    summary + " Sie wird nur gebraucht, wenn ein CLI-Upstream im Container-Modus "
                    + "laufen soll.",
                    details);
        }

        var platform = probe.StandardOutput.Trim();
        if (platform.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            return CheckOutcome.Pass(
                Code,
                $"Die Container-Runtime '{runtime}' läuft im Linux-Modus und trägt die Mindestpolicy.",
                CheckOutcome.Details(("runtime", runtime), ("modus", platform)));
        }

        return CheckOutcome.Warning(
            Code,
            $"Die Container-Runtime '{runtime}' läuft im Modus '{platform}'. Die Mindestpolicy aus "
            + "ADR-0018 (read-only Wurzeldateisystem, cap-drop, Nicht-root-Benutzer) trägt nur mit "
            + "Linux-Containern.",
            "Auf einem Windows-Host Docker in den Linux-Modus schalten (WSL2-Backend). Sonst wird "
            + "ein Upstream im Container-Modus abgelehnt statt ungeschützt ausgeführt.",
            CheckOutcome.Details(("runtime", runtime), ("modus", platform)));
    }
}

/// <summary>
/// BFR-RT-0002 — das WASI-Host-Binary liegt am konfigurierten Pfad.
/// <para>
/// Ohne den Host lässt sich kein Connector-Paket proben, und ungeprobt wird nichts aktiv. Im
/// Container-Image liegt er unter <c>/usr/local/bin/bifrost-wasi-host</c>.
/// </para>
/// </summary>
public sealed class WasiHostCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.WasiHost;

    public DiagnosticScope Scope => DiagnosticScope.Runtime;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.WasiHostPath;
        if (path is null)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code,
                "BIFROST_WASI_HOST ist nicht gesetzt. Ohne den Host lassen sich keine "
                + "Connector-Pakete installieren; für alle anderen Upstream-Arten wird er nicht "
                + "gebraucht."));
        }

        var details = CheckOutcome.Details(("pfad", path));
        return Task.FromResult(context.Files.FileExists(path)
            ? CheckOutcome.Pass(Code, $"Das WASI-Host-Binary '{path}' ist vorhanden.", details)
            : CheckOutcome.Fail(
                Code,
                $"BIFROST_WASI_HOST zeigt auf '{path}', dort liegt keine Datei.",
                "Pfad korrigieren. Im Container-Image liegt der Host unter "
                + "/usr/local/bin/bifrost-wasi-host. Ohne ihn lässt sich ein Connector-Paket nicht "
                + "proben — und ungeprobt wird nichts aktiv.",
                details));
    }
}

/// <summary>
/// BFR-UP-0001 — Zustände der konfigurierten Upstreams.
/// <para>
/// Nur der laufende Serverprozess kennt sie; ohne Sonde meldet der Check <c>Skipped</c> statt eines
/// stillen „alles gut".
/// </para>
/// </summary>
public sealed class UpstreamStatesCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.UpstreamStates;

    public DiagnosticScope Scope => DiagnosticScope.Upstreams;

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Upstreams is null)
        {
            return CheckOutcome.Skipped(
                Code,
                "Keine Upstream-Sonde verdrahtet. Die Zustände kennt nur der laufende Gateway.");
        }

        var facts = await context.Upstreams.DescribeAsync(ct).ConfigureAwait(false);
        if (facts.Count == 0)
        {
            return CheckOutcome.Skipped(Code, "Es ist kein Upstream konfiguriert.");
        }

        var broken = facts.Where(fact => !fact.Healthy).ToList();
        var details = CheckOutcome.Details(
            ("upstreams", DetailFormat.Count(facts.Count)),
            ("nicht_bereit", DetailFormat.Count(broken.Count)));

        if (broken.Count == 0)
        {
            return CheckOutcome.Pass(Code, $"Alle {facts.Count} Upstreams sind bereit.", details);
        }

        // Slug und Zustand sind Konfigurationsnamen; der Fehlertext kommt von aussen und geht
        // deshalb durch die Redaktion des Dienstes.
        var lines = broken.Select(fact =>
            $"{fact.Slug} ({fact.State}){(fact.Failure is null ? string.Empty : $": {fact.Failure}")}");

        return CheckOutcome.Warning(
            Code,
            $"{broken.Count} von {facts.Count} Upstreams sind nicht bereit — {string.Join("; ", lines)}",
            "Der Gateway läuft weiter; die Tools dieser Server fehlen im Katalog. Die Meldung je "
            + "Server nennt den Grund, das Audit hält den Verlauf fest.",
            details);
    }
}
