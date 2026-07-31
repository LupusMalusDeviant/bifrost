namespace McpMcp.Abstractions;

/// <summary>
/// OAuth-Anbindung an einen HTTP-Upstream (MCP-Autorisierung, RFC 9728 / RFC 8707 / OAuth 2.1).
/// <para>
/// <b>Warum vorregistriert und nicht dynamisch:</b> Dynamic Client Registration ist im Standard
/// inzwischen abgelöst, und Client-ID-Metadata-Documents verlangen ein öffentlich abrufbares
/// Dokument — ein selbst gehosteter Gateway steht aber oft nicht im Netz. Ein am Authorization
/// Server registrierter Client ist der Weg, der ohne öffentliche Erreichbarkeit funktioniert.
/// </para>
/// <para>
/// <see cref="ClientSecret"/> liegt im DataProtection-verschlüsselten Config-Blob wie jedes andere
/// Credential und wird in Ausgaben maskiert.
/// </para>
/// </summary>
/// <param name="Scopes">
/// Gewünschte Berechtigungen. Leer heißt: die Auswahl kommt aus der Protected Resource Metadata des
/// Upstreams (<c>scopes_supported</c>) bzw. aus der <c>WWW-Authenticate</c>-Aufforderung — der
/// Standard nennt genau diese Reihenfolge.
/// </param>
/// <param name="AllowPrivateTargets">
/// Erlaubt Discovery- und Token-Endpunkte im internen Netz. Vorgabe <c>false</c>: Der Upstream sagt
/// uns über seine Metadaten, <em>wohin</em> wir Anfragen schicken — das ist eine vom Gegenüber
/// gesteuerte Adresse und damit derselbe SSRF-Weg wie bei importierten Schemabeschreibungen.
/// </param>
public sealed record UpstreamOAuthOptions(
    string ClientId,
    string? ClientSecret = null,
    IReadOnlyList<string>? Scopes = null,
    bool AllowPrivateTargets = false);

/// <summary>
/// Ein gespeichertes Zugriffstoken für einen Upstream. Getrennt von der Upstream-Konfiguration
/// abgelegt: Die Konfiguration ist append-only versioniert, ein Token erneuert sich dagegen
/// laufend — beides in derselben Historie hieße, jede Erneuerung als Konfigurationsänderung zu
/// führen.
/// </summary>
public sealed record UpstreamOAuthToken(
    ServerId Server,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Scopes,
    /// <summary>Der Issuer, von dem dieses Token stammt. Ein Wechsel entwertet es (SEP-2352).</summary>
    string Issuer,
    DateTimeOffset ObtainedAt)
{
    /// <summary>
    /// Erneuerungsbedarf mit Sicherheitsabstand: Ein Token, das während des Aufrufs abläuft, ist so
    /// gut wie abgelaufen.
    /// </summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan skew)
        => ExpiresAt is { } expires && expires - skew <= now;
}

/// <summary>Ablage der Upstream-Token. Werte liegen verschlüsselt (NFR-04).</summary>
public interface IUpstreamOAuthTokenStore
{
    Task<UpstreamOAuthToken?> GetAsync(ServerId server, CancellationToken ct);

    Task SaveAsync(UpstreamOAuthToken token, CancellationToken ct);

    Task RemoveAsync(ServerId server, CancellationToken ct);
}

/// <summary>
/// Was der Gateway über einen laufenden Autorisierungsvorgang festhält, bis der Nutzer aus dem
/// Browser zurückkommt.
/// <para>
/// <see cref="ExpectedIssuer"/> ist nicht optional: RFC 9207 verlangt, den <c>iss</c>-Parameter der
/// Antwort gegen den <b>vorher notierten</b> Issuer zu prüfen. Ohne diese Notiz ist die Prüfung
/// wertlos — sie schützt genau gegen den Fall, dass jemand die Antwort eines anderen
/// Authorization Servers unterschiebt.
/// </para>
/// </summary>
public sealed record OAuthAuthorizationAttempt(
    string State,
    ServerId Server,
    string CodeVerifier,
    string ExpectedIssuer,
    Uri TokenEndpoint,
    Uri RedirectUri,
    string Resource,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Hat der Authorization Server beim Start ausgewiesen, dass er den <c>iss</c>-Parameter
    /// mitschickt (<c>authorization_response_iss_parameter_supported</c>)?
    /// <para>
    /// <b>Dann ist ein fehlender <c>iss</c> ein Abbruchgrund</b>, kein Grund zur Nachsicht: Genau so
    /// sähe der Angriff aus, gegen den RFC 9207 schützt — die Antwort eines anderen Authorization
    /// Servers, untergeschoben ohne den Parameter, der sie verraten würde. Die MCP-Autorisierung
    /// verlangt die Prüfung seit der Spec-Revision 2026-07-28 ausdrücklich.
    /// </para>
    /// <para>
    /// Weist der Server den Parameter nicht aus, bleibt es bei der Nachsicht — dort trägt die
    /// Bindung an den Token-Endpunkt aus demselben Metadaten-Dokument.
    /// </para>
    /// </summary>
    public bool IssuerParameterRequired { get; init; }
}
