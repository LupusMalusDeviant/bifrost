using System.Net;
using System.Net.Sockets;

namespace Bifrost.Core.Importing;

/// <summary>Wie ein Ziel eingeschätzt wird, ohne es aufzulösen.</summary>
public enum ImportTargetReach
{
    /// <summary>Nichts deutet auf ein internes Ziel hin.</summary>
    Public = 0,

    /// <summary>Der Name oder die Adresse ist erkennbar intern.</summary>
    Private = 1,

    /// <summary>
    /// Der Name lässt sich ohne Namensauflösung nicht einordnen. Das ist keine Entwarnung — es ist
    /// die ehrliche Angabe, dass die Frage hier nicht abschließend beantwortet werden kann.
    /// </summary>
    Undecidable = 2,
}

/// <summary>
/// Beurteilt das Ziel einer importierten URL — <b>ohne Namensauflösung und ohne Netzzugriff</b>.
/// <para>
/// <b>Warum keine Auflösung:</b> Der Importplan ist eine Analyse, kein Verbindungsversuch. Eine
/// DNS-Abfrage aus dem Importpfad wäre bereits ein ausgehender Kontakt zu einem Namen, den ein
/// Fremder in eine Datei geschrieben hat — und sie wäre eine Aussage mit Verfallsdatum: Ein Name,
/// der heute öffentlich zeigt, kann morgen intern zeigen (DNS-Rebinding). Die verbindliche Prüfung
/// sitzt weiterhin dort, wo tatsächlich verbunden wird
/// (<c>RemoteSpecFetcher.EnsureTargetAllowedAsync</c>), und sie löst dabei jede Adresse des Namens
/// auf. Diese Klasse ersetzt sie nicht — sie macht den Verdacht früh sichtbar.
/// </para>
/// <para>
/// Die Adressbereiche sind dieselben wie dort. Sie stehen hier ein zweites Mal, weil
/// <c>Bifrost.Core</c> nicht auf <c>Bifrost.Upstream</c> zeigt (ADR-0004); gemeldet ist das als
/// Fundstelle im Bericht statt es durch eine neue Abhängigkeit in die falsche Richtung zu lösen.
/// </para>
/// </summary>
public static class ImportNetworkTarget
{
    /// <summary>Namen und Endungen, die per Definition nicht ins öffentliche Netz zeigen.</summary>
    private static readonly string[] PrivateSuffixes =
    [
        ".localhost",
        ".local",
        ".internal",
        ".intranet",
        ".lan",
        ".home",
        ".home.arpa",
        ".corp",
    ];

    // Bewusst NICHT in der Liste: '.test', '.example' und '.invalid'. Sie lösen nirgends auf — das
    // macht sie zu Platzhaltern, nicht zu internen Zielen. Sie hier zu melden hiesse, eine
    // Beispieladresse in einer Dokumentationsdatei als Angriffsflaeche auszugeben.

    /// <summary>Beurteilt den Host einer URL.</summary>
    public static ImportTargetReach Classify(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ClassifyHost(target.Host);
    }

    /// <summary>Beurteilt einen Hostnamen oder eine Adressangabe.</summary>
    public static ImportTargetReach ClassifyHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return ImportTargetReach.Undecidable;
        }

        // Uri.Host liefert IPv6 ohne Klammern, eine rohe Angabe kann sie tragen.
        var value = host.Trim().Trim('[', ']');

        if (IPAddress.TryParse(value, out var literal))
        {
            return IsPrivate(literal) ? ImportTargetReach.Private : ImportTargetReach.Public;
        }

        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return ImportTargetReach.Private;
        }

        foreach (var suffix in PrivateSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return ImportTargetReach.Private;
            }
        }

        // Ein Name ohne Punkt ist kein registrierter Name, sondern einer, den erst das Suchsuffix
        // des Zielrechners vervollständigt — also ein Rechner im eigenen Netz.
        if (!value.Contains('.', StringComparison.Ordinal))
        {
            return ImportTargetReach.Private;
        }

        return ImportTargetReach.Public;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
            {
                return true;
            }

            return address.IsIPv4MappedToIPv6 && IsPrivate(address.MapToIPv4());
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            0 => true,
            10 => true,
            127 => true,
            // 169.254.0.0/16 — hier liegt auch der Cloud-Metadatendienst.
            169 when octets[1] == 254 => true,
            172 when octets[1] is >= 16 and <= 31 => true,
            192 when octets[1] == 168 => true,
            // 100.64.0.0/10, Carrier-Grade-NAT.
            100 when octets[1] is >= 64 and <= 127 => true,
            _ => false,
        };
    }
}
