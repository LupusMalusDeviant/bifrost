using System.Net;
using System.Net.Sockets;

namespace Bifrost.Upstream.Http;

/// <summary>
/// Holt eine Beschreibung von einer URL — größenbegrenzt und mit Zielprüfung. Gemeinsame Grundlage
/// für alle Konnektoren, die eine vom Betreiber genannte Adresse abrufen (OpenAPI, OpenRPC).
/// <para>
/// Ohne diese Prüfung ist ein Gateway, das eine konfigurierte URL abruft, ein Werkzeug, um
/// <b>interne</b> Dienste zu erreichen: Cloud-Metadaten unter <c>169.254.169.254</c>, ein
/// Admin-Port auf <c>127.0.0.1</c>, ein Nachbar im Firmennetz. Deshalb wird das Ziel aufgelöst und
/// geprüft, <b>bevor</b> die Verbindung steht — und Weiterleitungen werden einzeln erneut geprüft,
/// weil sonst genau über sie umgangen würde, was vorne geprüft wurde.
/// </para>
/// <para>
/// Die Fehlerart kommt vom Aufrufer (<c>fail</c>): Ein OpenAPI-Import soll an einer
/// <c>OpenApiImportException</c> scheitern und nicht an einem Typ, der nach einem anderen
/// Konnektor benannt ist — die gemeinsame Prüfung darf die Diagnose nicht verwischen.
/// </para>
/// </summary>
internal static class RemoteSpecFetcher
{
    /// <summary>Obergrenze gegen Memory-Exhaustion über eine riesige Beschreibung.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>So viele Weiterleitungen werden verfolgt — jede davon geprüft.</summary>
    private const int MaxRedirects = 3;

    public static async Task<string> FetchAsync(
        Uri location, bool allowPrivateTargets, TimeSpan timeout,
        Func<string, Exception> fail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(fail);

        if (location.IsFile)
        {
            var info = new FileInfo(location.LocalPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                throw fail($"Dokument überschreitet {MaxBytes / (1024 * 1024)} MB.");
            }

            return await File.ReadAllTextAsync(location.LocalPath, ct).ConfigureAwait(false);
        }

        if (location.Scheme is not ("http" or "https"))
        {
            throw fail(
                $"Quelle '{location.Scheme}' wird nicht unterstützt (nur file://, http:// und https://).");
        }

        // Weiterleitungen selbst verfolgen: Mit AllowAutoRedirect prüfte niemand das Ziel dahinter,
        // und ein Server könnte über einen Redirect auf 127.0.0.1 zeigen.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = timeout };
        var current = location;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            await EnsureTargetAllowedAsync(current, allowPrivateTargets, fail, ct).ConfigureAwait(false);
            using var response = await http
                .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var next = response.Headers.Location ?? throw fail("Weiterleitung ohne Ziel.");
                current = next.IsAbsoluteUri ? next : new Uri(current, next);
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                throw fail($"Dokument überschreitet {MaxBytes / (1024 * 1024)} MB.");
            }

            return await ReadLimitedAsync(response, fail, ct).ConfigureAwait(false);
        }

        throw fail($"Mehr als {MaxRedirects} Weiterleitungen — abgebrochen.");
    }

    /// <summary>
    /// Löst den Hostnamen auf und weist private, Loopback- und Link-Local-Ziele ab. Geprüft werden
    /// <b>alle</b> Adressen des Namens: Ein Name, der auf eine öffentliche und eine private Adresse
    /// zeigt, wäre sonst ein Schlupfloch.
    /// </summary>
    public static async Task EnsureTargetAllowedAsync(
        Uri target, bool allowPrivateTargets, Func<string, Exception> fail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(fail);
        if (allowPrivateTargets)
        {
            return;
        }

        // Eine file://-Quelle hat kein Netzwerkziel; die Grenze dafür ist die Pfad-Allowlist des
        // Betriebssystems, nicht die Adressprüfung.
        if (target.IsFile)
        {
            return;
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(target.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(target.Host, ct).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                throw fail($"Host '{target.Host}' ließ sich nicht auflösen: {exception.Message}");
            }
        }

        foreach (var address in addresses)
        {
            if (IsPrivate(address))
            {
                throw fail(
                    $"Ziel '{target.Host}' zeigt auf die interne Adresse {address}. "
                    + "Wer das braucht, setzt AllowPrivateTargets ausdrücklich — sonst wäre das Gateway "
                    + "ein Weg, interne Dienste zu erreichen.");
            }
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            // Link-local (fe80::/10) und Unique-Local (fc00::/7); dazu IPv4-gemappte Adressen,
            // die sonst an der v4-Prüfung vorbeiliefen.
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal)
            {
                return true;
            }

            return address.IsIPv4MappedToIPv6 && IsPrivate(address.MapToIPv4());
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,
            127 => true,
            // 169.254.0.0/16 — hier liegt auch der Cloud-Metadatendienst.
            169 when octets[1] == 254 => true,
            172 when octets[1] >= 16 && octets[1] <= 31 => true,
            192 when octets[1] == 168 => true,
            // 100.64.0.0/10, Carrier-Grade-NAT.
            100 when octets[1] >= 64 && octets[1] <= 127 => true,
            0 => true,
            _ => false,
        };
    }

    private static bool IsRedirect(HttpStatusCode status) => status
        is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Liest mit harter Obergrenze. Ein fehlender <c>Content-Length</c> darf die Grenze nicht
    /// aushebeln — sonst wäre die Prüfung oben nur eine Bitte.
    /// </summary>
    private static async Task<string> ReadLimitedAsync(
        HttpResponseMessage response, Func<string, Exception> fail, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var builder = new System.Text.StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length > MaxBytes)
            {
                throw fail($"Dokument überschreitet {MaxBytes / (1024 * 1024)} MB.");
            }
        }

        return builder.ToString();
    }
}
