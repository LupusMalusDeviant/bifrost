using System.Net.Sockets;
using Bifrost.Abstractions;
using ModelContextProtocol;

namespace Bifrost.Upstream;

/// <summary>
/// Bereinigt den Fehlschlag eines Verbindungsaufbaus mit genau den Zugangsdaten, die dieser Aufbau
/// vorgelegt hat (Plan 0005 G1). Gebraucht wird das, wenn der Handshake scheitert, nachdem er das
/// Token schon gesendet hat: Dann gibt es keine Verbindung, die es nennen könnte, und hinter dem
/// Connector kennt es niemand mehr.
/// <para>
/// <b>Die Einordnung bleibt erhalten.</b> Jedes Glied der Kette wird mit bereinigter Meldung neu
/// gebaut, bekannte Typen als derselbe Typ: Ein 401 bleibt ein <see cref="HttpRequestException"/> mit
/// Statuscode (abgelehnte Anmeldung), ein JSON-RPC-Fehler eine <see cref="McpProtocolException"/> mit
/// Fehlercode. Ein unbekannter Typ wird zur <see cref="McpException"/> — für die Einordnung ist er
/// ohnehin bedeutungslos. Die Typnamen der ursprünglichen Kette trägt die neue Ausnahme unter
/// <see cref="CredentialRedaction.OriginalTypesDataKey"/>; die ursprünglichen Ausnahmen hängen nicht
/// mehr daran.
/// </para>
/// </summary>
internal static class HandshakeFailure
{
    /// <summary>So tief wird die Kette höchstens nachgebaut; tiefer liegt nichts, was die Einordnung braucht.</summary>
    private const int MaxDepth = 8;

    public static Exception Sanitize(Exception exception, IReadOnlyList<string> presented)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(presented);

        var chain = new List<Exception>();
        var current = exception;
        for (; current is not null && chain.Count < MaxDepth; current = current.InnerException)
        {
            chain.Add(current);
        }

        // Hinter der Tiefengrenze kann noch die Ablehnung stecken, die die Fehlerart bestimmt (Plan
        // 0005 H9, N42 „die Fehlerart bleibt"). Dann wird dieses eine Glied als innerstes angehängt —
        // sonst kippte eine abgelehnte Anmeldung zu „nicht erreichbar", nur weil ihr Text bereinigt wurde.
        for (; current is not null; current = current.InnerException)
        {
            if (current is UpstreamAuthenticationException
                || current is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
            {
                chain.Add(current);
                break;
            }
        }

        Exception? rebuilt = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            rebuilt = Rebuild(chain[i], CredentialRedaction.Scrub(chain[i].Message, presented), rebuilt);
        }

        rebuilt!.Data[CredentialRedaction.OriginalTypesDataKey] = CredentialRedaction.TypeChain(exception);
        return rebuilt;
    }

    private static Exception Rebuild(Exception original, string message, Exception? inner) => original switch
    {
        UpstreamAuthenticationException => inner is null
            ? new UpstreamAuthenticationException(message)
            : new UpstreamAuthenticationException(message, inner),
        HttpRequestException http => new HttpRequestException(http.HttpRequestError, message, inner, http.StatusCode),

        // Der Text einer SocketException kommt vom Betriebssystem, nicht von der Gegenstelle.
        SocketException socket => new SocketException((int)socket.SocketErrorCode),
        OperationCanceledException canceled => new OperationCanceledException(message, inner, canceled.CancellationToken),
        TimeoutException => new TimeoutException(message, inner),
        McpProtocolException protocol => new McpProtocolException(message, inner, protocol.ErrorCode),
        _ => new McpException(message, inner),
    };
}
