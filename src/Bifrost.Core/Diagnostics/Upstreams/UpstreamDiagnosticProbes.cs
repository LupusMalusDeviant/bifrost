using System.Net;
using System.Net.Sockets;

using Bifrost.Abstractions;

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
/// Was die Gegenstelle über sich preisgegeben hat — aus der <b>stehenden</b> Verbindung eines
/// bereits aktivierten Upstreams. Nur der laufende Serverprozess führt die.
/// <para>
/// <b>Sie ist nicht mehr die einzige Quelle.</b> Der transiente Verbindungstest liest die Angabe
/// inzwischen selbst, solange seine eigene Verbindung noch steht — auch für einen Upstream, der
/// noch gar nicht angeschlossen ist. Diese Sonde bleibt die <em>erste</em> Quelle: Sie beschreibt
/// die Verbindung, die tatsächlich den Verkehr trägt.
/// </para>
/// <para>
/// Wo keine der beiden Quellen etwas hat, steht „nicht ermittelt" mit Begründung — nie ein Wert aus
/// der Konfiguration, der dann wie eine Messung aussähe.
/// </para>
/// </summary>
public interface IUpstreamNegotiationProbe
{
    /// <summary>
    /// <c>null</c>, wenn zu diesem Slug nichts bekannt ist.
    /// <para>
    /// <paramref name="kind"/> ist der Transport der <b>geprüften</b> Konfiguration. Er wird
    /// mitgegeben, weil die Sonde ihn nicht kennt: Sie sieht eine Verbindung, nicht deren Bauart.
    /// Vorher stand deshalb pauschal „MCP" im Bericht — auch für einen OpenAPI- oder CLI-Upstream,
    /// der nie MCP gesprochen hat.
    /// </para>
    /// </summary>
    Task<UpstreamNegotiation?> DescribeAsync(
        string slug, UpstreamTransportKind kind, CancellationToken ct);
}
