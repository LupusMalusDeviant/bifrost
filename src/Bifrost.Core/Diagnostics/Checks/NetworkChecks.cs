using System.Net;

using Bifrost.Abstractions.Operations;

namespace Bifrost.Core.Diagnostics.Checks;

/// <summary>
/// BFR-NET-0001 — ist der konfigurierte Port frei?
/// <para>
/// Läuft die Diagnose im Gateway-Prozess selbst, ist ein belegter Port der Normalfall — dann sind
/// wir es. Aus der CLI heraus, bei stehendem Gateway, ist er der Grund, warum der Start scheitert.
/// </para>
/// </summary>
public sealed class ListenPortCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.ListenPort;

    public DiagnosticScope Scope => DiagnosticScope.Network;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ports = context.ListenPorts();
        if (ports.Count == 0)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code,
                "Keine Lauschadresse bekannt (ASPNETCORE_URLS ist nicht gesetzt). Ohne sie lässt "
                + "sich kein Port prüfen."));
        }

        var occupied = new List<int>();
        var unknown = new List<int>();
        foreach (var port in ports)
        {
            switch (context.Ports.Inspect(port))
            {
                case PortState.Occupied:
                    occupied.Add(port);
                    break;
                case PortState.Unknown:
                    unknown.Add(port);
                    break;
                default:
                    break;
            }
        }

        var details = CheckOutcome.Details(
            ("ports", string.Join(", ", ports)),
            ("belegt", occupied.Count == 0 ? "-" : string.Join(", ", occupied)));

        if (occupied.Count > 0)
        {
            return Task.FromResult(context.GatewayRunsInThisProcess
                ? CheckOutcome.Pass(
                    Code,
                    $"Port {string.Join(", ", occupied)} ist belegt — erwartbar, der Gateway läuft "
                    + "in diesem Prozess.",
                    details)
                : CheckOutcome.Fail(
                    Code,
                    $"Port {string.Join(", ", occupied)} ist bereits belegt.",
                    "Läuft der Gateway schon? Sonst hält ein anderer Dienst den Port. Entweder ihn "
                    + "beenden oder ASPNETCORE_URLS auf einen freien Port setzen.",
                    details));
        }

        if (unknown.Count > 0)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code,
                $"Der Zustand von Port {string.Join(", ", unknown)} liess sich nicht feststellen. "
                + "Das ist ausdrücklich kein „frei\".",
                details));
        }

        return Task.FromResult(CheckOutcome.Pass(
            Code, $"Port {string.Join(", ", ports)} ist frei.", details));
    }
}

/// <summary>
/// BFR-NET-0002 — nur HTTP, kein TLS-Proxy deklariert, Sitzungs-Cookie trägt trotzdem 'Secure'.
/// <para>
/// <b>Das ist der Befund zum teuersten stillen Fehler dieses Produkts.</b> Ausserhalb von
/// Development trägt das Cookie der Web-UI immer <c>Secure</c>. Ein Browser verwirft ein solches
/// Cookie über Klartext-HTTP <b>stillschweigend</b>: Die Anmeldung geht durch, der Server antwortet
/// mit 302, und der nächste Seitenaufruf ist wieder die Login-Maske. Weder im Browser noch im
/// Server steht eine Fehlermeldung — das Symptom zeigt nicht auf die Ursache.
/// </para>
/// <para>
/// Eine <b>Warnung</b>, kein Fehler: Ob ein TLS-Proxy davorsteht, lässt sich von hier aus nicht
/// beantworten. Was sich beantworten lässt, ist die Kombination, in der es schiefgeht — und die
/// steht hier.
/// </para>
/// </summary>
public sealed class InsecureCookieTransportCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.InsecureCookieTransport;

    public DiagnosticScope Scope => DiagnosticScope.Network;

    public TimeSpan Timeout => TimeSpan.FromSeconds(2);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IsDevelopment)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code, "Im Development trägt das Sitzungs-Cookie kein 'Secure'; die Anmeldung hält über HTTP."));
        }

        if (context.ListenAddresses.Count == 0)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code, "Keine Lauschadresse bekannt (ASPNETCORE_URLS ist nicht gesetzt)."));
        }

        var details = CheckOutcome.Details(
            ("adressen", string.Join(", ", context.ListenAddresses)),
            ("vertraute_proxies_gesetzt", DetailFormat.YesNo(context.TrustedProxies is not null)));

        if (!context.ListensOnlyOnPlainHttp())
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code, "Der Gateway lauscht auf HTTPS; das Sitzungs-Cookie kommt an.", details));
        }

        if (context.ListensOnlyOnLoopback())
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code,
                "Der Gateway lauscht nur auf Loopback. Browser behandeln 'localhost' als sicheren "
                + "Ursprung — die Anmeldung hält auch über Klartext-HTTP, ebenso per SSH-Tunnel.",
                details));
        }

        if (context.TrustedProxies is not null)
        {
            return Task.FromResult(CheckOutcome.Pass(
                Code,
                "Der Gateway lauscht nur auf HTTP, aber ein vertrauenswürdiger Proxy ist deklariert "
                + "(BIFROST_TRUSTED_PROXIES) — der vorgesehene Produktionsaufbau.",
                details));
        }

        return Task.FromResult(CheckOutcome.Warning(
            Code,
            "Der Gateway lauscht nur auf HTTP und es ist kein vertrauenswürdiger Proxy deklariert. "
            + "Das Sitzungs-Cookie der Web-UI trägt 'Secure' — ruft jemand die Oberfläche über "
            + "http://<adresse> auf, verwirft der Browser es stillschweigend: Die Anmeldung geht "
            + "durch, der nächste Seitenaufruf ist wieder die Login-Maske, und nirgends steht ein Grund.",
            "TLS-Proxy davorsetzen, der X-Forwarded-Proto: https und den Port im Host mitgibt "
            + "(nginx: 'proxy_set_header Host $http_host'), und BIFROST_TRUSTED_PROXIES auf dessen "
            + "Adresse setzen. Der Zugang über /mcp und /api ist nicht betroffen — Agenten "
            + "authentifizieren sich mit einem Header, nicht mit einem Cookie.",
            details));
    }
}

/// <summary>
/// BFR-NET-0003 — <c>BIFROST_TRUSTED_PROXIES</c> ist lesbar.
/// <para>
/// Ein Tippfehler im Wert bricht den Start ab, statt still auf „aus" zu fallen. Das ist richtig so
/// — aber ein Betreiber soll es hier erfahren und nicht am nicht mehr startenden Container.
/// </para>
/// </summary>
public sealed class TrustedProxiesCheck : IDiagnosticCheck
{
    public string Code => DiagnosticCodes.TrustedProxies;

    public DiagnosticScope Scope => DiagnosticScope.Network;

    public TimeSpan Timeout => TimeSpan.FromSeconds(2);

    public Task<DiagnosticCheck> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var value = context.TrustedProxies;
        if (value is null)
        {
            return Task.FromResult(CheckOutcome.Skipped(
                Code,
                "BIFROST_TRUSTED_PROXIES ist nicht gesetzt; Forwarded-Header werden ignoriert. Das "
                + "ist richtig, wenn der Gateway direkt erreichbar ist."));
        }

        if (value.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(CheckOutcome.Warning(
                Code,
                "BIFROST_TRUSTED_PROXIES steht auf 'any' — jedem Absender wird geglaubt.",
                "Nur zulässig, wenn der Gateway ausschliesslich über den Proxy erreichbar ist. Steht "
                + "er zusätzlich direkt im Netz, kann jeder Client 'X-Forwarded-Proto: https' "
                + "behaupten. Besser die Adresse oder das Netz des Proxy eintragen.",
                CheckOutcome.Details(("wert", "any"))));
        }

        var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var invalid = entries.Where(entry => !IsAddressOrCidr(entry)).ToList();
        if (invalid.Count > 0)
        {
            return Task.FromResult(CheckOutcome.Fail(
                Code,
                $"BIFROST_TRUSTED_PROXIES enthält {invalid.Count} unlesbare(n) Eintrag/Einträge: "
                + string.Join(", ", invalid),
                "Erlaubt sind 'any' oder eine Kommaliste aus IP-Adressen und CIDR-Bereichen "
                + "(z. B. '172.17.0.1, 10.0.0.0/8'). Ein ungültiger Wert bricht den Start ab.",
                CheckOutcome.Details(("eintraege", DetailFormat.Count(entries.Length)))));
        }

        return Task.FromResult(CheckOutcome.Pass(
            Code,
            $"BIFROST_TRUSTED_PROXIES nennt {entries.Length} auswertbare(n) Eintrag/Einträge.",
            CheckOutcome.Details(("eintraege", DetailFormat.Count(entries.Length)))));
    }

    private static bool IsAddressOrCidr(string entry)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return TryParseStrict(entry, out _);
        }

        if (!TryParseStrict(entry[..slash], out var address)
            || !int.TryParse(entry[(slash + 1)..], out var prefix))
        {
            return false;
        }

        var maximum = address!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return prefix >= 0 && prefix <= maximum;
    }

    /// <summary>
    /// <see cref="IPAddress.TryParse(string, out IPAddress)"/> nimmt Kurzformen an: Aus
    /// <c>10.0.0</c> wird <c>10.0.0.0</c>. Ein Tippfehler würde damit als gültige Adresse
    /// durchgehen — und der Betreiber vertraute anschliessend einem anderen Netz als gemeint.
    /// Deshalb der Rückvergleich mit der kanonischen Schreibweise.
    /// </summary>
    private static bool TryParseStrict(string entry, out IPAddress? address)
        => IPAddress.TryParse(entry, out address)
            && string.Equals(address.ToString(), entry, StringComparison.OrdinalIgnoreCase);
}
