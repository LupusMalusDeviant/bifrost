using McpMcp.Abstractions;
using McpMcp.Upstream.OAuth;
using ModelContextProtocol.Client;

namespace McpMcp.Upstream;

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
public sealed class StreamableHttpUpstreamConnector : IUpstreamConnector
{
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

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Http
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Http-Optionen.", nameof(config));

        var headers = options.Headers?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<string, string>();
        if (_gatewayIdentity is not null)
        {
            headers[GatewayIdentity.InstanceHeader] = _gatewayIdentity.InstanceId;
        }

        if (options.OAuth is { } oauth)
        {
            headers["Authorization"] = "Bearer " + await ResolveTokenAsync(id, config, oauth, ct)
                .ConfigureAwait(false);
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
        return new SdkUpstreamConnection(id, client);
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

        var token = await _tokens.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Für '{config.Slug}' liegt kein Zugriffstoken vor. Der Upstream muss einmal "
                + "verbunden werden (Server-Verwaltung → Verbinden).");

        var now = _time.GetUtcNow();
        if (!token.NeedsRefresh(now, RefreshSkew))
        {
            return token.AccessToken;
        }

        var resource = config.Http!.Endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var metadata = await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            new Uri(token.Issuer), oauth.AllowPrivateTargets, ct).ConfigureAwait(false);
        var refreshed = await OAuthFlow.RefreshAsync(
            token, metadata.TokenEndpoint, oauth, resource, now, ct).ConfigureAwait(false);
        await _tokens.SaveAsync(refreshed, ct).ConfigureAwait(false);
        return refreshed.AccessToken;
    }
}
