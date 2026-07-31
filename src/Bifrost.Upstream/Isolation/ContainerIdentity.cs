using System.Globalization;

namespace Bifrost.Upstream.Isolation;

/// <summary>
/// Name und Etiketten eines Containers — die Grundlage jeder <b>sicheren</b> Zuordnung zwischen
/// Gateway und laufendem Container (ADR-0018, WP3.2 Punkt 4).
/// <para>
/// <b>Warum das nötig ist:</b> Ein <c>docker run</c> ist ein Client zum Daemon, kein Elternprozess.
/// Stirbt der Client, ist der Container nicht mitgestorben — der Prozessbaum-Kill des Host-Modus
/// hat hier kein Gegenstück. Aufräumen heißt also: den Container <em>benennen</em> können. Ihn über
/// die Prozessliste zu suchen wäre Raten; ein Etikett ist eine Zusage.
/// </para>
/// <para>
/// Die Etiketten tragen ausdrücklich die <b>Instanz</b>-Kennung mit. Zwei Gateways auf demselben
/// Docker-Daemon sind ein realer Betriebsfall, und ein Aufräumlauf, der fremde Container abräumt,
/// ist gefährlicher als der Zustand, den er beheben soll.
/// </para>
/// </summary>
/// <param name="Name">
/// Der Containername. Eindeutig je <em>Start</em>, nicht je Upstream: Ein Neustart darf nicht daran
/// scheitern, dass eine Leiche denselben Namen hält.
/// </param>
/// <param name="Slug">Der Upstream, zu dem dieser Container gehört.</param>
/// <param name="InstanceId">Die Gateway-Instanz, die ihn gestartet hat.</param>
public sealed record ContainerIdentity(string Name, string Slug, string InstanceId)
{
    /// <summary>Etikett auf jedem von diesem Gateway gestarteten Container.</summary>
    public const string OwnerLabel = "de.bifrost.owner";

    /// <summary>Wert des Besitz-Etiketts. Konstant und wörtlich — danach wird gefiltert.</summary>
    public const string OwnerValue = "bifrost-gateway";

    /// <summary>Etikett mit der Kennung der Gateway-Instanz.</summary>
    public const string InstanceLabel = "de.bifrost.instance";

    /// <summary>Etikett mit dem Upstream-Slug.</summary>
    public const string SlugLabel = "de.bifrost.upstream";

    /// <summary>Namenspräfix; auch ohne Etiketten erkennbar, etwa in einer Prozessliste.</summary>
    public const string NamePrefix = "bifrost-";

    /// <summary>
    /// Ersatzkennung, wenn keine <see cref="Abstractions.GatewayIdentity"/> gereicht wurde — etwa
    /// im Test oder in einem Werkzeug ohne Wirt. Sie ist je Prozess konstant, damit ein Aufräumlauf
    /// dieselbe Menge trifft wie der Start.
    /// </summary>
    public static string ProcessInstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Baut Name und Etiketten für einen Start. Der Slug wird auf die von Container-Runtimes
    /// erlaubten Zeichen reduziert — ein Name, den die Runtime ablehnt, verhindert den Start und
    /// damit auch das Aufräumen.
    /// </summary>
    public static ContainerIdentity ForUpstream(string slug, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var safeSlug = new string([.. slug
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')]);
        if (safeSlug.Length > 40)
        {
            safeSlug = safeSlug[..40];
        }

        // Eine eigene Kennung je Start. Denkbar waere der Slug allein — dann kollidiert aber ein
        // Neustart mit dem noch nicht abgeraeumten Vorgaenger, und der Upstream kaeme mit der
        // Meldung "name already in use" nicht hoch.
        var launch = Guid.NewGuid().ToString("N")[..8];
        return new ContainerIdentity(
            string.Create(CultureInfo.InvariantCulture, $"{NamePrefix}{safeSlug}-{launch}"),
            slug,
            instanceId);
    }

    /// <summary>Die Etiketten als <c>--label</c>-Argumente, in fester Reihenfolge.</summary>
    public IReadOnlyList<string> LabelArguments() =>
    [
        "--label", $"{OwnerLabel}={OwnerValue}",
        "--label", $"{InstanceLabel}={InstanceId}",
        "--label", $"{SlugLabel}={Slug}",
    ];

    /// <summary>Der Filterausdruck, der genau die Container dieser Instanz trifft.</summary>
    public static string InstanceFilter(string instanceId)
        => $"label={InstanceLabel}={instanceId}";
}
