using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Bifrost.Server;

/// <summary>
/// Wem der Gateway die <c>X-Forwarded-*</c>-Header glaubt (<c>BIFROST_TRUSTED_PROXIES</c>).
/// <para>
/// <b>Warum das nicht einfach an ist:</b> Steht der Gateway direkt im Netz, kann jeder Client
/// <c>X-Forwarded-Proto: https</c> mitschicken und behaupten, die Verbindung sei sicher. Danach
/// baut der Gateway https-Adressen über eine Klartextverbindung — und die Warnung, dass das
/// Sitzungs-Cookie verworfen wird, bliebe aus. Deshalb ist die Auswertung <b>opt-in</b>.
/// </para>
/// <para>
/// <b>Warum es überhaupt nötig ist:</b> Hinter einem TLS-Proxy sieht der Gateway nur HTTP. Ohne
/// diese Header schickt er einen abgemeldeten Besucher von einer https-Seite auf eine
/// http-Adresse — beim ersten echten Betrieb ein „400 The plain HTTP request was sent to HTTPS
/// port".
/// </para>
/// </summary>
internal static class ForwardedProxyOptions
{
    /// <summary>Trau jedem Absender. Nur sinnvoll, wenn der Gateway ausschließlich über den Proxy erreichbar ist.</summary>
    public const string Any = "any";

    /// <summary>
    /// Liest die Angabe: leer ⇒ aus (<c>false</c>), <c>any</c> ⇒ jeder Absender, sonst eine
    /// Kommaliste aus IP-Adressen und CIDR-Bereichen.
    /// </summary>
    public static bool TryCreate(string? configured, out ForwardedHeadersOptions options)
    {
        options = new ForwardedHeadersOptions();
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor;

        // Ein Proxy, nicht eine Kette. Mehr zu erlauben hiesse, einem vorgelagerten Absender zu
        // glauben, den dieser Gateway nicht kennt.
        options.ForwardLimit = 1;

        // Die Vorgabe kennt nur Loopback. In einem Container kommt der Proxy über das
        // Docker-Netz — die Vorgabe passt dort nie, und ohne diese Zeilen wären die Header
        // stillschweigend wirkungslos.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        if (string.Equals(configured.Trim(), Any, StringComparison.OrdinalIgnoreCase))
        {
            // Beide Listen leer heisst für die Middleware: keine Herkunftsprüfung.
            return true;
        }

        foreach (var entry in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else if (System.Net.IPNetwork.TryParse(entry, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else
            {
                throw new InvalidOperationException(
                    $"BIFROST_TRUSTED_PROXIES enthält '{entry}' — das ist weder eine IP-Adresse noch "
                    + $"ein CIDR-Bereich noch '{Any}'. Ein Tippfehler würde hier still dazu führen, "
                    + "dass die Header ignoriert werden.");
            }
        }

        // Eine Angabe, aus der keine einzige Herkunft entstanden ist, wäre dasselbe wie 'any' —
        // und genau das darf ein Tippfehler nicht bewirken.
        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                "BIFROST_TRUSTED_PROXIES ist gesetzt, ergibt aber keine Herkunft. Leer lassen schaltet "
                + "die Auswertung ab; 'any' traut jedem Absender.");
        }

        return true;
    }
}
