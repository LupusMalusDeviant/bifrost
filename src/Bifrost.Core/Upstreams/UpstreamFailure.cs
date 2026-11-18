using System.Net.Sockets;
using Bifrost.Abstractions;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Upstreams;

/// <summary>
/// Ordnet einen gescheiterten Verbindungsversuch ein und formuliert, was darüber gesagt wird
/// (Vertrag „Selbsttätige Wiederverbindung", Abschnitte 2 und 6).
/// <para>
/// <b>Warum der Grund hier formuliert und nicht aus der Ausnahme übernommen wird:</b> Die Meldung
/// einer fremden Ausnahme ist unkontrollierter Text. Gemessen hängt das MCP-SDK an eine
/// HTTP-Fehlermeldung den <i>Antwortkörper des Upstreams</i> an — zitiert der in seiner 401-Antwort
/// den vorgelegten Schlüssel oder dessen Anfang, stünde er in Oberfläche, REST, Audit und Log. Der
/// Text, der dorthin geht, entsteht deshalb aus dem, was das Gateway selbst weiß: Fehlerart,
/// Statuscode, Ziel.
/// </para>
/// </summary>
public static class UpstreamFailure
{
    /// <summary>Was vom SDK an eine Meldung angehängt wird — ab hier wird abgeschnitten.</summary>
    private const string ResponseBodyMarker = "Response body:";

    /// <summary>Ein Grund steht in einer Tabellenzelle und in einer Audit-Zeile, nicht in einem Bericht.</summary>
    private const int MaxReasonLength = 300;

    /// <summary>
    /// Die Fehlerart. Geht die ganze Kette der inneren Ausnahmen durch: Ein 401 bleibt ein 401,
    /// auch wenn es jemand in eine „Health-Ping fehlgeschlagen"-Ausnahme verpackt hat.
    /// </summary>
    [NoHostExecution("Liest eine Ausnahme; startet nichts.")]
    public static UpstreamFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is UpstreamAuthenticationException
                || current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                || current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden })
            {
                return UpstreamFailureKind.Authentication;
            }
        }

        return UpstreamFailureKind.Unreachable;
    }

    /// <summary>
    /// Der Grund, wie er in Status, Audit und Log steht — ohne Antwortkörper der Gegenstelle und
    /// ohne die Geheimwerte der Konfiguration, auch nicht deren Anfang.
    /// </summary>
    [NoHostExecution("Formuliert einen Text aus einer Ausnahme; startet nichts.")]
    public static string Describe(Exception exception, UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(config);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // Vom Gateway selbst formuliert — der Text ist bereits bereinigt bzw. enthält
                // nichts Fremdes. Er wird trotzdem noch einmal durch die Bereinigung geschickt:
                // Eine Zusage, die an einer Konvention hängt, ist keine.
                case UpstreamAuthenticationException or UpstreamConnectionLostException
                    or UpstreamSessionLostException:
                    return Clean(current.Message, config);

                case HttpRequestException { StatusCode: { } status }:
                    var code = (int)status;
                    return code is 401 or 403
                        ? $"Authentifizierung abgelehnt (HTTP {code}){Target(config)}."
                        : $"Die Gegenstelle antwortete mit HTTP {code}{Target(config)}.";

                case SocketException socket:
                    return $"Nicht erreichbar: {Describe(socket.SocketErrorCode)}{Target(config)}.";

                case HttpRequestException http when http.InnerException is null:
                    return $"Nicht erreichbar: {Describe(http.HttpRequestError)}{Target(config)}.";
            }
        }

        return Clean(exception.Message, config);
    }

    /// <summary>
    /// Die Typnamen der Ausnahmekette. Das geht ins Log anstelle des Ausnahmeobjekts — dessen
    /// Textform trüge die ungefilterte Meldung.
    /// </summary>
    [NoHostExecution("Liest Typnamen; startet nichts.")]
    public static string TypeChain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var names = new List<string>();
        for (var current = exception; current is not null && names.Count < 6; current = current.InnerException)
        {
            names.Add(current.GetType().Name);
        }

        return string.Join(" → ", names);
    }

    /// <summary>
    /// Der Grund eines gescheiterten Aufrufs, wenn die Konfiguration des Upstreams nicht bekannt ist:
    /// ohne Antwortkörper der Gegenstelle, eine Zeile, begrenzt. Geheimwerte der Konfiguration kann
    /// diese Fassung nicht maskieren — wo die Konfiguration bekannt ist, gilt
    /// <see cref="Describe(Exception, UpstreamServerConfig)"/>.
    /// </summary>
    [NoHostExecution("Formuliert einen Text aus einer Ausnahme; startet nichts.")]
    public static string DescribeWithoutConfig(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                var code = (int)status;
                return code is 401 or 403
                    ? $"Authentifizierung abgelehnt (HTTP {code})."
                    : $"Die Gegenstelle antwortete mit HTTP {code}.";
            }
        }

        return Shape(exception.Message);
    }

    /// <summary>Fremdtext auf das bringen, was hinausdarf: kein Antwortkörper, keine Geheimwerte, eine Zeile.</summary>
    [NoHostExecution("Textbereinigung; startet nichts.")]
    public static string Clean(string? message, UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unbekannter Fehler.";
        }

        return Shape(UpstreamConnectionTester.ScrubSecrets(CutBody(message), config));
    }

    private static string CutBody(string message)
    {
        var cut = message.IndexOf(ResponseBodyMarker, StringComparison.OrdinalIgnoreCase);
        return cut >= 0 ? message[..cut] : message;
    }

    private static string Shape(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unbekannter Fehler.";
        }

        var text = CutBody(message);
        text = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (text.Length == 0)
        {
            return "Unbekannter Fehler.";
        }

        return text.Length > MaxReasonLength ? text[..MaxReasonLength] + "…" : text;
    }

    /// <summary>
    /// Zielrechner und Port aus der <b>Konfiguration</b>, nicht aus der Fehlermeldung — ohne Pfad,
    /// Abfragezeichenkette und Benutzerangabe.
    /// </summary>
    private static string Target(UpstreamServerConfig config)
        => config.Http?.Endpoint is { IsAbsoluteUri: true } endpoint
            ? $" ({endpoint.Host}:{endpoint.Port})"
            : string.Empty;

    private static string Describe(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "Verbindung verweigert",
        SocketError.TimedOut => "Zeitüberschreitung beim Verbindungsaufbau",
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
            => "der Name ließ sich nicht auflösen",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown
            => "Zielrechner im Netzwerk nicht erreichbar",
        SocketError.ConnectionReset or SocketError.ConnectionAborted => "Verbindung zurückgesetzt",
        _ => $"Netzwerkfehler ({error})",
    };

    private static string Describe(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => "der Name ließ sich nicht auflösen",
        HttpRequestError.ConnectionError => "Verbindung kam nicht zustande",
        HttpRequestError.SecureConnectionError => "die TLS-Verbindung kam nicht zustande",
        HttpRequestError.ResponseEnded => "die Antwort brach ab",
        _ => $"HTTP-Anfrage scheiterte ({error})",
    };
}
