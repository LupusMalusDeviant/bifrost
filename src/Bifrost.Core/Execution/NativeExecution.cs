using Bifrost.Abstractions;

namespace Bifrost.Core.Execution;

/// <summary>
/// Die eine Stelle, die weiß, was „nativ auf dem Host" <b>heute</b> bedeutet (ADR-0025, Kontext).
/// <para>
/// Zwei Wege führen dorthin: CLI-Upstreams ohne Container-Isolation und stdio-Upstreams, die
/// überhaupt kein Isolationsmodell haben. Alles andere — HTTP, OpenAPI, OpenRPC, WASI — startet
/// kein fremdes Programm im Prozessraum des Gateways.
/// </para>
/// <para>
/// <b>Warum das hier steht und nicht in der Policy:</b> Die Frage „läuft das nativ?" und die Frage
/// „darf es das?" sind verschiedene Fragen. Die erste ändert sich, sobald stdio ein Isolationsmodell
/// bekommt (ADR-0025 E5, umgesetzt von WP3.2) — die zweite nicht. Getrennt gehalten, ist die
/// Erweiterung ein Eingriff an einer Stelle.
/// </para>
/// </summary>
public static class NativeExecution
{
    /// <summary>
    /// Startet diese Konfiguration ein Programm nativ auf dem Host?
    /// <para>
    /// <b>Unbekannter Transport heißt ja.</b> Ein Transport, den diese Methode nicht kennt, ist neu;
    /// ihn als „nicht nativ" durchzuwinken wäre genau der stille Rückfall, den ADR-0025 E1 verbietet.
    /// Wer einen Transport hinzufügt, trägt ihn hier ein — und merkt es, weil sonst nichts startet.
    /// </para>
    /// </summary>
    [NoHostExecution("Beantwortet die Frage 'laeuft das nativ?', nicht die Frage 'darf es das?'.")]
    public static bool RunsOnHost(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return RunsOnHost(config.Kind, config.Cli);
    }

    /// <summary>
    /// Dieselbe Frage für einen Fall, in dem noch keine vollständige Konfiguration existiert — etwa
    /// ein Paketmanifest, das nur seinen Transport nennt (ADR-0025 E4).
    /// </summary>
    public static bool RunsOnHost(UpstreamTransportKind kind, CliTransportOptions? cli) => kind switch
    {
        // stdio hat heute kein Isolationsmodell: Das Programm läuft mit den Rechten des Gateways,
        // und das Gateway hält den Schlüsselring. Gehärtet ist keine Sandbox.
        UpstreamTransportKind.Stdio => true,

        // CLI kann seit ADR-0018 in den Container — die Vorgabe ist es nicht. Fehlt die Angabe,
        // gilt der bisherige Host-Modus; ein fehlendes Feld darf hier nichts erlauben, was ein
        // gesetztes verbieten würde.
        UpstreamTransportKind.Cli => cli?.Isolation is not { Mode: CliIsolationMode.Container },

        UpstreamTransportKind.StreamableHttp => false,
        UpstreamTransportKind.OpenApi => false,
        UpstreamTransportKind.OpenRpc => false,

        // WASI läuft in einem eigenen, capability-begrenzten Host mit default-deny (ADR-0020). Der
        // Host-Prozess selbst ist Teil der Auslieferung, nicht das fremde Programm.
        UpstreamTransportKind.Wasi => false,

        _ => true,
    };

    /// <summary>
    /// Wie der betroffene Upstream in einer Warnung genannt wird. Namentlich — eine Meldung über
    /// „3 Upstreams" verlangt vom Betreiber die Arbeit, die der Gateway schon getan hat.
    /// </summary>
    [NoHostExecution("Formuliert einen Namen fuer eine Meldung.")]
    public static string Describe(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var program = config.Kind switch
        {
            UpstreamTransportKind.Stdio => config.Stdio?.Command,
            UpstreamTransportKind.Cli => config.Cli?.Executable,
            _ => null,
        };

        return program is { Length: > 0 }
            ? $"{config.Slug} ({config.Kind}: {program})"
            : $"{config.Slug} ({config.Kind})";
    }

    /// <summary>Die nativ laufenden Upstreams einer Menge, nach Slug sortiert.</summary>
    [NoHostExecution("Formuliert Namen fuer eine Meldung.")]
    public static IReadOnlyList<string> DescribeAll(IEnumerable<UpstreamServerConfig> configs)
    {
        ArgumentNullException.ThrowIfNull(configs);

        // Sortiert, damit Warnung und Audit bei jedem Start gleich aussehen und ein unveränderter
        // Zustand nicht wie eine neue Meldung wirkt.
        return [.. configs
            .Where(RunsOnHost)
            .OrderBy(config => config.Slug, StringComparer.Ordinal)
            .Select(Describe)];
    }
}
