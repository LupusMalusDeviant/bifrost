namespace Bifrost.Abstractions;

/// <summary>
/// Die Container-Runtimes, die ein Upstream nennen darf (<see cref="IsolationOptions.Runtime"/>).
/// <para>
/// <b>Warum eine feste Liste.</b> Die Runtime wird <em>nativ auf dem Host</em> gestartet, mit den
/// Rechten des Gateways — der Container entsteht erst durch sie. Ein Container-Upstream gilt für die
/// Host-Policy als isoliert (ADR-0025). Wäre das Feld frei, ließe sich jedes Programm auf dem Host als
/// „Runtime" eintragen, und das Verbot nativer Ausführung wäre mit einem Feld umgangen.
/// </para>
/// <para>
/// Erlaubt sind nur Namen, keine Pfade: Die Runtime wird über den <c>PATH</c> gefunden. Ein Pfad
/// müsste sonst seinerseits geprüft werden, und „liegt unter /usr/bin" sagt nichts darüber, was dort
/// liegt.
/// </para>
/// </summary>
public static class ContainerRuntimes
{
    /// <summary>Die zulässigen Namen, in der Reihenfolge, in der Meldungen sie nennen.</summary>
    public static IReadOnlyList<string> Allowed { get; } = ["docker", "podman", "nerdctl", "finch"];

    public static bool IsAllowed(string? runtime) => runtime is "docker" or "podman" or "nerdctl" or "finch";

    /// <summary>
    /// Der Name, mit dem die Runtime gestartet wird. Gibt bewusst die Konstante aus der Liste zurück
    /// und nicht den übergebenen Wert: Was den Prozess startet, stammt damit nie aus der
    /// Konfiguration, sondern aus diesem Code.
    /// </summary>
    /// <exception cref="ArgumentException">Der Name steht nicht auf der Liste.</exception>
    public static string Resolve(string? runtime) => runtime switch
    {
        "docker" => "docker",
        "podman" => "podman",
        "nerdctl" => "nerdctl",
        "finch" => "finch",
        _ => throw new ArgumentException(Rejection(runtime), nameof(runtime)),
    };

    /// <summary>Die Absage für einen nicht zulässigen Namen — dieselbe für Prüfung und Start.</summary>
    public static string Rejection(string? runtime) =>
        $"Container-Runtime '{runtime}' ist nicht zulässig. Erlaubt sind {string.Join(", ", Allowed)} "
        + "— als Name, nicht als Pfad. Die Runtime läuft nativ auf dem Host; ein freies Feld wäre ein "
        + "Weg am Verbot nativer Ausführung vorbei (ADR-0025).";
}
