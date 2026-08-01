using Bifrost.Abstractions;
using Bifrost.Core.Diagnostics.Upstreams;

namespace Bifrost.Server.Diagnostics;

/// <summary>
/// Was die Gegenstelle über sich preisgegeben hat (WP4.6, Punkt 2) — aus dem, was der Supervisor
/// ohnehin führt. Keine zweite Verbindung, keine zweite Zustandsmaschine.
/// <para>
/// <b>Warum das nur für einen bereits angeschlossenen Upstream geht:</b> Die ausgehandelte
/// Protokollfassung und die Fähigkeiten der Gegenstelle leben in der <i>stehenden</i> Verbindung.
/// Der Verbindungstest ist absichtlich transient — er räumt seine Verbindung wieder ab, bevor
/// irgendetwas persistiert wird. Für einen noch nicht angeschlossenen Server liefert diese Sonde
/// deshalb <c>null</c>, und die Anzeige sagt „nicht ermittelt" statt einen Wert zu erfinden, der
/// aus der Konfiguration stammt und dann wie eine Messung aussieht.
/// </para>
/// <para>
/// <b>Was hier NICHT steht, und warum:</b> Die genaue Protokollfassung (<c>2025-11-25</c>,
/// <c>2026-07-28</c>, …) kennt nur das MCP-SDK, und sie endet in
/// <c>SdkUpstreamConnection</c>; über <see cref="IUpstreamConnection"/> geht davon einzig
/// <see cref="IUpstreamConnection.PushesCatalogChanges"/> nach oben. Daraus lässt sich die
/// <em>Fassungsfamilie</em> ablesen, nicht die Fassung — und genau so steht es im Bericht. Die
/// exakte Angabe durchzureichen wäre eine Änderung an <c>Bifrost.Abstractions</c> und
/// <c>Bifrost.Upstream</c>; beides liegt ausserhalb dieses Arbeitspakets und ist als Fundstelle
/// gemeldet.
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

    public Task<UpstreamNegotiation?> DescribeAsync(string slug, CancellationToken ct)
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

        string? family = null;
        string? note = null;
        if (connection is null)
        {
            note = "Es steht gerade keine Verbindung; die Angaben stammen aus dem letzten Katalog.";
        }
        else if (connection.PushesCatalogChanges)
        {
            capabilities.Add("list_changed");
            family = "vor Revision 2026-07-28";
            note = "Die Gegenstelle meldet Katalogänderungen von sich aus. Die genaue Fassung "
                + "reicht der Verbindungsvertrag nicht durch — nur diese Familie.";
        }
        else
        {
            family = "Revision 2026-07-28 oder neuer";
            note = "Die Gegenstelle meldet keine Katalogänderungen mehr von sich aus; der Katalog "
                + "wird turnusmäßig nachgefragt.";
        }

        return Task.FromResult<UpstreamNegotiation?>(new UpstreamNegotiation(
            "MCP",
            family,
            capabilities,
            status.ToolCount,
            note));
    }
}
