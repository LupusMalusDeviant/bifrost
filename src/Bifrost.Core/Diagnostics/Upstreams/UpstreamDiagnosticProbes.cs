using System.Net;
using System.Net.Sockets;

namespace Bifrost.Core.Diagnostics.Upstreams;

/// <summary>
/// Das Ergebnis einer Namensauflösung.
/// </summary>
/// <param name="Addresses">
/// <b>Alle</b> Adressen des Namens, als Text. Alle — weil ein Name, der auf eine öffentliche und
/// eine private Adresse zeigt, sonst ein Schlupfloch wäre; die Stufe Zielschutz prüft jede einzeln.
/// </param>
/// <param name="Failure">Fremdtext des Auflösers; <c>null</c>, wenn es geklappt hat.</param>
public sealed record HostResolution(
    bool Resolved,
    IReadOnlyList<string> Addresses,
    string? Failure = null)
{
    /// <summary>Der Name war bereits eine Adresse — es war nichts aufzulösen.</summary>
    public bool WasLiteral { get; init; }
}

/// <summary>
/// Löst einen Hostnamen auf. Als Schnittstelle, damit die Stufen „Runtime/DNS" und „Zielschutz"
/// gegen erfundene Antworten prüfbar sind — ein Test, der echtes DNS befragt, prüft das Netz des
/// Bauknechts und nicht den Gateway.
/// <para>
/// <b>Das ist keine Verbindung.</b> Aufgelöst wird ein Name; es wird kein Socket geöffnet und kein
/// Byte gesendet. Die verbindliche Zielprüfung sitzt weiterhin dort, wo tatsächlich verbunden wird
/// (<c>RemoteSpecFetcher.EnsureTargetAllowedAsync</c> in <c>Bifrost.Upstream</c>) — diese Sonde
/// ersetzt sie nicht, sie macht die Ursache <i>vorher</i> benennbar.
/// </para>
/// </summary>
public interface IHostResolutionProbe
{
    Task<HostResolution> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Namensauflösung des laufenden Prozesses.</summary>
public sealed class SystemHostResolutionProbe : IHostResolutionProbe
{
    public static SystemHostResolutionProbe Instance { get; } = new();

    public async Task<HostResolution> ResolveAsync(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new HostResolution(false, [], "Es ist kein Host angegeben.");
        }

        // Uri.Host liefert IPv6 ohne Klammern, eine rohe Angabe kann sie tragen.
        var value = host.Trim().Trim('[', ']');
        if (IPAddress.TryParse(value, out var literal))
        {
            return new HostResolution(true, [literal.ToString()]) { WasLiteral = true };
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(value, ct).ConfigureAwait(false);
            return addresses.Length == 0
                ? new HostResolution(false, [], "Der Name lieferte keine Adresse.")
                : new HostResolution(true, [.. addresses.Select(address => address.ToString())]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException exception)
        {
            return new HostResolution(false, [], exception.Message);
        }
        catch (ArgumentException exception)
        {
            return new HostResolution(false, [], exception.Message);
        }
    }
}

/// <summary>
/// Was die Gegenstelle über sich preisgegeben hat. Nur der laufende Serverprozess kann das
/// beantworten, und auch er nur für einen <b>bereits aktivierten</b> Upstream: Die ausgehandelte
/// Protokollfassung lebt in der stehenden Verbindung, und der transiente Test räumt seine
/// Verbindung wieder ab.
/// <para>
/// Ohne Sonde meldet die Anzeige „nicht ermittelt" mit Begründung — nicht einen Wert aus der
/// Konfiguration, der dann wie eine Messung aussähe.
/// </para>
/// </summary>
public interface IUpstreamNegotiationProbe
{
    /// <summary><c>null</c>, wenn zu diesem Slug nichts bekannt ist.</summary>
    Task<UpstreamNegotiation?> DescribeAsync(string slug, CancellationToken ct);
}
