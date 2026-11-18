using Bifrost.Abstractions;
using Bifrost.Upstream.Http;
using Bifrost.Upstream.OAuth;
using ModelContextProtocol.Client;

namespace Bifrost.Upstream;

/// <summary>
/// Verbindet Remote-MCP-Server über Streamable HTTP (FR-02); markiert ausgehende Calls für die
/// Loop-Erkennung (FR-05).
/// <para>
/// Ist für den Upstream OAuth konfiguriert, geht das gespeicherte Zugriffstoken als
/// <c>Authorization: Bearer</c> mit und wird vor Ablauf erneuert. <b>Ohne gültiges Token kommt der
/// Upstream nicht hoch</b> — ein Verbindungsversuch ohne Autorisierung liefe in ein 401 und
/// hinterliesse einen Server im Fehlerzustand, dessen Ursache niemand ansieht.
/// </para>
/// </summary>
public sealed class StreamableHttpUpstreamConnector : IUpstreamConnector, IUpstreamCredentialRenewal
{
    /// <summary>
    /// Upstreams, deren Token abgewiesen wurde und erneuert werden muss, gleich wie lange es noch
    /// gälte (V1) — mit dem abgewiesenen Token, sobald es beim nächsten Aufbau gelesen ist. Der
    /// Vermerk bleibt, bis ein anderes Token gespeichert ist: Ein gestörter Token-Endpunkt darf ihn
    /// nicht verbrauchen, sonst legte der nächste Versuch das abgewiesene Token wieder vor (Codex H3).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ServerId, string> _renew = new();

    /// <summary>Platzhalter, solange das abgewiesene Token noch nicht gelesen ist.</summary>
    private const string RejectedTokenUnknown = "";

    /// <summary>Sicherheitsabstand vor dem Ablauf — ein Token, das während des Aufrufs verfällt, ist keins.</summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly GatewayIdentity? _gatewayIdentity;
    private readonly IUpstreamOAuthTokenStore? _tokens;
    private readonly TimeProvider _time;

    public StreamableHttpUpstreamConnector(
        GatewayIdentity? gatewayIdentity = null,
        IUpstreamOAuthTokenStore? tokens = null,
        TimeProvider? time = null)
    {
        _gatewayIdentity = gatewayIdentity;
        _tokens = tokens;
        _time = time ?? TimeProvider.System;
    }

    public UpstreamTransportKind Kind => UpstreamTransportKind.StreamableHttp;

    /// <inheritdoc />
    public void RequestCredentialRenewal(ServerId id)
        // Abgewiesen ist das Token, das die stehende Verbindung vorgelegt hat — nicht das, was beim
        // nächsten Aufbau in der Ablage liegt: Hat jemand inzwischen neu autorisiert, ist dieses
        // frisch und nicht abgewiesen (Plan 0005 D6).
        => _renew[id] = _presented.TryGetValue(id, out var presented) ? presented : RejectedTokenUnknown;

    /// <summary>Das Token, das die zuletzt aufgebaute Verbindung je Upstream vorgelegt hat.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ServerId, string> _presented = new();

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Http
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Http-Optionen.", nameof(config));

        // MCP über HTTP war der einzige Transport ohne Zielprüfung: Der Endpunkt ging direkt in den
        // Transport, während OpenAPI, OpenRPC und der OAuth-Issuer die Adresse auflösen und private
        // Ziele abweisen. Gefunden von den Architekturtests aus WP3.6.
        //
        // 'null' heißt „nicht entschieden": Bestandsinstanzen haben den Schalter nie gesetzt, und
        // ein MCP-Server im eigenen Netz ist der Regelfall. Sie beim Neustart abzuklemmen wäre die
        // stille Verhaltensänderung, die ADR-0025 E3 ablehnt.
        await RemoteSpecFetcher.EnsureTargetAllowedAsync(
            options.Endpoint,
            options.AllowPrivateTargets ?? true,
            message => new InvalidOperationException(
                $"Upstream '{config.Slug}': {message}"),
            ct).ConfigureAwait(false);

        var headers = options.Headers?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<string, string>();
        if (_gatewayIdentity is not null)
        {
            headers[GatewayIdentity.InstanceHeader] = _gatewayIdentity.InstanceId;
        }

        string? accessToken = null;
        if (options.OAuth is { } oauth)
        {
            accessToken = await ResolveTokenAsync(id, config, oauth, ct).ConfigureAwait(false);
            headers["Authorization"] = "Bearer " + accessToken;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = config.Slug,
            Endpoint = options.Endpoint,
            AdditionalHeaders = headers,
            // FR-02: explizit gesetzt statt auf den SDK-Default zu vertrauen — AutoDetect probiert
            // Streamable HTTP und fällt auf HTTP+SSE zurück. Ein SDK-Upgrade darf das nicht
            // stillschweigend ändern; der Default ist zusätzlich per Test festgenagelt.
            TransportMode = options.AllowLegacySse
                ? HttpTransportMode.AutoDetect
                : HttpTransportMode.StreamableHttp,
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        if (accessToken is null)
        {
            return new SdkUpstreamConnection(id, client);
        }

        _presented[id] = accessToken;
        return new SdkUpstreamConnection(id, client, [accessToken]);
    }

    /// <summary>
    /// Liefert ein gültiges Zugriffstoken — erneuert es, wenn es demnächst abläuft.
    /// <para>
    /// Scheitert die Erneuerung, wird <b>nicht</b> mit dem alten Token weitergemacht: Es ist
    /// abgelaufen oder widerrufen, und ein Verbindungsversuch damit endete in einem 401, dessen
    /// Ursache dann im Ping-Fehler untergeht statt in einer klaren Meldung zu stehen.
    /// </para>
    /// </summary>
    private async Task<string> ResolveTokenAsync(
        ServerId id, UpstreamServerConfig config, UpstreamOAuthOptions oauth, CancellationToken ct)
    {
        if (_tokens is null)
        {
            throw new InvalidOperationException(
                $"Config '{config.Slug}' ist auf OAuth eingestellt, aber in dieser Zusammenstellung "
                + "gibt es keine Token-Ablage.");
        }

        // Fehlt das Token oder lässt es sich nicht erneuern, ist das eine Sache der Anmeldung und
        // keine der Erreichbarkeit: Der Supervisor wartet darauf lange statt im Sekundentakt
        // (UpstreamFailureKind.Authentication) — von selbst wird hier nichts besser, es braucht
        // einen Menschen, der neu autorisiert.
        var token = await _tokens.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new UpstreamAuthenticationException(
                $"Für '{config.Slug}' liegt kein Zugriffstoken vor. Der Upstream muss einmal "
                + "autorisiert werden (Server-Verwaltung → Autorisieren).");

        // Verlangt der Supervisor die Erneuerung (der Upstream hat das Token abgewiesen), wird
        // erneuert, auch wenn die Ablaufzeit noch nicht erreicht ist — sie ist dann offenbar falsch
        // oder das Token widerrufen (V1).
        var renewalRequested = false;
        if (_renew.TryGetValue(id, out var rejected))
        {
            if (rejected.Length == 0)
            {
                // Die Anforderung kam vom abgewiesenen Lebenszeichen; das gespeicherte Token ist das
                // abgewiesene.
                _renew.TryUpdate(id, token.AccessToken, rejected);
                renewalRequested = true;
            }
            else if (rejected == token.AccessToken)
            {
                renewalRequested = true;
            }
            else
            {
                // Inzwischen liegt ein anderes Token (neu autorisiert): Das ist nicht abgewiesen.
                _renew.TryRemove(new KeyValuePair<ServerId, string>(id, rejected));
            }
        }

        var now = _time.GetUtcNow();
        if (!renewalRequested && !token.NeedsRefresh(now, RefreshSkew))
        {
            return token.AccessToken;
        }

        var resource = config.Http!.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var metadata = await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri(token.Issuer), oauth.AllowPrivateTargets, ct).ConfigureAwait(false);
        UpstreamOAuthToken refreshed;
        try
        {
            refreshed = await OAuthFlow.RefreshAsync(
                token, metadata.TokenEndpoint, oauth, resource, now, ct).ConfigureAwait(false);
        }
        catch (OAuthDiscoveryException exception)
            when (exception.StatusCode is { } status
                && ((int)status >= 500 || status == System.Net.HttpStatusCode.TooManyRequests))
        {
            // Der Token-Endpunkt ist gestört oder überlastet, nicht ablehnend: Das wird von selbst
            // besser und gehört in die kurze Frist der Unerreichbarkeit, nicht in die 15 min einer
            // abgelehnten Anmeldung (L5). Die Meldung nennt den Antwortkörper nicht.
            throw new HttpRequestException(
                $"Der Token-Endpunkt antwortete mit HTTP {(int)status}.", exception, status);
        }
        catch (OAuthDiscoveryException exception)
        {
            // Der Token-Endpunkt hat geantwortet und abgelehnt (oder es gibt kein Refresh-Token).
            // Ein Netzfehler auf dem Weg dorthin ist KEIN Fall für diesen Zweig — der kommt als
            // HttpRequestException durch und wird wie jede Unerreichbarkeit bald erneut versucht.
            // Die Meldung nennt bewusst nicht, was der Token-Endpunkt geschrieben hat.
            throw new UpstreamAuthenticationException(
                $"Das Zugriffstoken für '{config.Slug}' ließ sich nicht erneuern. Der Upstream muss "
                + "neu autorisiert werden (Server-Verwaltung → Autorisieren).",
                exception);
        }

        // Nur speichern, wenn noch das gelesene Token gilt (Codex M3): Wer inzwischen neu autorisiert
        // oder getrennt hat, hat Vorrang vor dieser Erneuerung.
        if (!await _tokens.ReplaceAsync(token, refreshed, ct).ConfigureAwait(false))
        {
            var current = await _tokens.GetAsync(id, ct).ConfigureAwait(false)
                ?? throw new UpstreamAuthenticationException(
                    $"Für '{config.Slug}' liegt kein Zugriffstoken mehr vor — die Verbindung wurde "
                    + "getrennt. Der Upstream muss neu autorisiert werden (Server-Verwaltung → Autorisieren).");
            _renew.TryRemove(id, out _);
            return current.AccessToken;
        }

        _renew.TryRemove(id, out _);
        return refreshed.AccessToken;
    }
}
