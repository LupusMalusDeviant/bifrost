using System.Net.Sockets;
using Faolan.Abstractions;
using Faolan.Core.Execution;

namespace Faolan.Core.Upstreams;

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
        => Describe(exception, config, presented: null);

    /// <param name="presented">Vorgelegte Zugangsdaten, die in keiner Konfiguration stehen (OAuth-Token, Plan 0005 D1).</param>
    [NoHostExecution("Formuliert einen Text aus einer Ausnahme; startet nichts.")]
    public static string Describe(Exception exception, UpstreamServerConfig config, IReadOnlyList<string>? presented)
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
                    return Clean(current.Message, config, presented);

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

        return Clean(exception.Message, config, presented);
    }

    /// <summary>
    /// Die Typnamen der Ausnahmekette. Das geht ins Log anstelle des Ausnahmeobjekts — dessen
    /// Textform trüge die ungefilterte Meldung. Bei einer bereinigten Ausnahme (<see cref="Sanitize"/>)
    /// stehen danach die Typnamen der ursprünglichen Kette.
    /// </summary>
    [NoHostExecution("Liest Typnamen; startet nichts.")]
    public static string TypeChain(Exception exception) => CredentialRedaction.TypeChain(exception);

    /// <summary>Unter diesem Schlüssel trägt eine bereinigte Ausnahme die Typnamen der ursprünglichen Kette.</summary>
    private const string OriginalTypesKey = CredentialRedaction.OriginalTypesDataKey;

    /// <summary>
    /// Ersetzt den Fehlschlag einer schon aufgebauten Verbindung durch einen mit bereinigter Meldung
    /// (Plan 0005 F1). Die Meldung kann das Token zitieren, das genau diese Verbindung vorgelegt hat;
    /// außerhalb des Verbindungsaufbaus kennt es niemand mehr. Die ursprüngliche Ausnahme hängt
    /// deshalb <b>nicht</b> als innere daran — über den Neuaufbau einer Sitzung erreichte sie sonst
    /// auch den wartenden Agenten. Erhalten bleiben die Fehlerart (<see cref="Classify"/>) und die
    /// Typnamen der Kette (<see cref="TypeChain"/>).
    /// </summary>
    /// <param name="exception">Der ursprüngliche Fehlschlag.</param>
    /// <param name="sanitizedMessage">Der Grund, schon bereinigt — mit den vorgelegten Zugangsdaten der Verbindung.</param>
    [NoHostExecution("Verpackt eine Ausnahme; startet nichts.")]
    public static Exception Sanitize(Exception exception, string sanitizedMessage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(sanitizedMessage);

        Exception sanitized = Classify(exception) == UpstreamFailureKind.Authentication
            ? new UpstreamAuthenticationException(sanitizedMessage)
            : new UpstreamConnectionLostException(sanitizedMessage);
        sanitized.Data[OriginalTypesKey] = TypeChain(exception);
        return sanitized;
    }

    /// <summary>
    /// Ersetzt den Fehlschlag eines Aufrufs durch einen mit bereinigter Meldung, solange noch feststeht,
    /// welche Verbindung ihn ausgeführt hat (Plan 0005 G5). Später, beim Formulieren über die Hülle,
    /// die der Aufrufer in der Hand hält, ist das nicht mehr sicher: Deren Ersatzverbindung kann
    /// inzwischen eine andere sein — mit einem anderen Token.
    /// <para>
    /// Zeitüberschreitung, Abbruch und Sitzungsverlust behalten ihren Typ: Aufrufer und Wiederholung
    /// unterscheiden daran. Alles andere wird wie bei <see cref="Sanitize"/> ersetzt (Fehlerart und
    /// Typnamen bleiben).
    /// </para>
    /// </summary>
    /// <param name="exception">Der ursprüngliche Fehlschlag.</param>
    /// <param name="sanitizedMessage">Der Grund, schon bereinigt — mit den Zugangsdaten der ausführenden Verbindung.</param>
    [NoHostExecution("Verpackt eine Ausnahme; startet nichts.")]
    internal static Exception SanitizeAttempt(Exception exception, string sanitizedMessage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(sanitizedMessage);

        Exception? sanitized = exception switch
        {
            TimeoutException => new TimeoutException(sanitizedMessage),
            OperationCanceledException canceled => new OperationCanceledException(sanitizedMessage, canceled.CancellationToken),
            UpstreamSessionLostException => new UpstreamSessionLostException(sanitizedMessage),
            _ => null,
        };
        if (sanitized is null)
        {
            return Sanitize(exception, sanitizedMessage);
        }

        sanitized.Data[OriginalTypesKey] = TypeChain(exception);
        return sanitized;
    }

    /// <summary>
    /// Der Grund eines gescheiterten Aufrufs, wenn die Konfiguration des Upstreams nicht bekannt ist:
    /// ohne Antwortkörper der Gegenstelle, eine Zeile, begrenzt. Geheimwerte der Konfiguration kann
    /// diese Fassung nicht maskieren — wo die Konfiguration bekannt ist, gilt
    /// <see cref="Describe(Exception, UpstreamServerConfig)"/>.
    /// </summary>
    [NoHostExecution("Formuliert einen Text aus einer Ausnahme; startet nichts.")]
    public static string DescribeWithoutConfig(Exception exception, IReadOnlyList<string>? presented = null)
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

        return Shape(UpstreamConnectionTester.ScrubSecrets(CutBody(exception.Message), null, presented));
    }

    /// <summary>Fremdtext auf das bringen, was hinausdarf: kein Antwortkörper, keine Geheimwerte, eine Zeile.</summary>
    [NoHostExecution("Textbereinigung; startet nichts.")]
    public static string Clean(string? message, UpstreamServerConfig config, IReadOnlyList<string>? presented = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Unbekannter Fehler.";
        }

        return Shape(UpstreamConnectionTester.ScrubSecrets(CutBody(message), config, presented));
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
