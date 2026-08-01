using Bifrost.Abstractions;
using Bifrost.Core.Diagnostics.Upstreams;

namespace Bifrost.Server.Diagnostics;

/// <summary>
/// Was die Gegenstelle über sich preisgegeben hat (WP4.6, Punkt 2) — aus dem, was der Supervisor
/// ohnehin führt. Keine zweite Verbindung, keine zweite Zustandsmaschine.
/// <para>
/// <b>Warum das nur für einen bereits angeschlossenen Upstream geht:</b> Die ausgehandelte
/// Protokollfassung und die Fähigkeiten der Gegenstelle leben in der <i>stehenden</i> Verbindung.
/// Für einen Server, der hier nicht geführt wird, liefert diese Sonde deshalb <c>null</c>, und die
/// Diagnose fällt auf das zurück, was ihr eigener transienter Versuch gesehen hat — nicht auf einen
/// Wert aus der Konfiguration, der dann wie eine Messung aussähe.
/// </para>
/// <para>
/// <b>Die genaue Fassung steht jetzt hier.</b> Bis zur Erweiterung des Verbindungsvertrags reichte
/// <see cref="IUpstreamConnection"/> davon nur
/// <see cref="IUpstreamConnection.PushesCatalogChanges"/> nach oben; daraus liess sich die
/// Fassungs<em>familie</em> ablesen („vor Revision 2026-07-28"), nicht die Fassung. Mit
/// <see cref="IUpstreamConnection.Protocol"/> kommt die ausgehandelte Angabe selbst nach oben — und
/// wo es keine gibt, kommt der Grund mit.
/// </para>
/// </summary>
public sealed class SupervisorNegotiationProbe : IUpstreamNegotiationProbe
{
    private readonly IUpstreamSupervisor _supervisor;

    public SupervisorNegotiationProbe(IUpstreamSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        _supervisor = supervisor;
    }

    public Task<UpstreamNegotiation?> DescribeAsync(
        string slug, UpstreamTransportKind kind, CancellationToken ct)
    {
        var status = _supervisor.Statuses
            .FirstOrDefault(candidate => string.Equals(candidate.Slug, slug, StringComparison.Ordinal));
        if (status is null)
        {
            return Task.FromResult<UpstreamNegotiation?>(null);
        }

        var connection = _supervisor.GetConnection(status.Id);
        var inventory = _supervisor.GetInventory(status.Id);
        if (connection is null && inventory is null)
        {
            return Task.FromResult<UpstreamNegotiation?>(null);
        }

        // Beobachtet aus dem letzten Katalog: WAS tatsaechlich ankam. Das bleibt neben den
        // gemeldeten Faehigkeiten stehen — ein Server darf 'prompts' anbieten und keine liefern.
        var capabilities = new List<string>();
        if (inventory is not null)
        {
            if (inventory.Tools.Count > 0)
            {
                capabilities.Add("tools");
            }

            if (inventory.Resources.Count > 0)
            {
                capabilities.Add("resources");
            }

            if (inventory.Prompts.Count > 0)
            {
                capabilities.Add("prompts");
            }
        }

        if (connection is null)
        {
            return Task.FromResult<UpstreamNegotiation?>(new UpstreamNegotiation(
                kind.ToString(),
                ProtocolVersion: null,
                capabilities,
                status.ToolCount,
                "Es steht gerade keine Verbindung; die Angaben stammen aus dem letzten Katalog. Die "
                + "ausgehandelte Fassung gehört zur Verbindung und ist mit ihr weg.",
                UpstreamProtocolAvailability.Unknown));
        }

        var protocol = connection.Protocol;
        capabilities.AddRange(protocol.Capabilities.Except(capabilities, StringComparer.Ordinal));

        // Diese eine Angabe steht NICHT im Capability-Objekt: Sie folgt aus der Revision selbst
        // (ADR-0023). Sie bleibt sichtbar, weil an ihr haengt, ob der Katalog nachgefragt wird —
        // aber nur dort, wo sie etwas bedeutet. Bei einem Upstream ohne MCP steht
        // 'PushesCatalogChanges' auf der Vorgabe true, und die heisst dort "hat keine
        // Katalogaenderungen zu melden", nicht "meldet sie von selbst".
        if (protocol.Availability is not UpstreamProtocolAvailability.NotApplicable
            && connection.PushesCatalogChanges)
        {
            capabilities.Add("list_changed");
        }

        return Task.FromResult<UpstreamNegotiation?>(new UpstreamNegotiation(
            kind.ToString(),
            protocol.Version,
            capabilities,
            status.ToolCount,
            Note(protocol, connection.PushesCatalogChanges),
            protocol.Availability));
    }

    /// <summary>
    /// Der Satz unter der Angabe. Er sagt entweder, was aus der Fassung folgt — oder, wenn keine
    /// dasteht, <b>warum</b> keine dasteht. Ein Bericht ohne diesen Satz liesse offen, ob die Angabe
    /// fehlt oder gar nicht existiert.
    /// </summary>
    private static string Note(UpstreamProtocolInfo protocol, bool pushes) => protocol.Availability switch
    {
        // Kein MCP: Die Frage nach Revision und list_changed stellt sich gar nicht. Sie hier
        // trotzdem zu beantworten waere eine Auskunft ueber ein Protokoll, das niemand spricht.
        UpstreamProtocolAvailability.NotApplicable => protocol.Reason ?? string.Empty,
        UpstreamProtocolAvailability.Negotiated => Consequence(pushes),
        _ => $"{protocol.Reason} {Consequence(pushes)}",
    };

    private static string Consequence(bool pushes) => pushes
        ? "Die Gegenstelle meldet Katalogänderungen von sich aus."
        : "Die Gegenstelle meldet keine Katalogänderungen mehr von sich aus; der Katalog wird "
            + "turnusmäßig nachgefragt.";
}
