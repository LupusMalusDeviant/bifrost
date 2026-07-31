using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bifrost.Upstream.Http;

namespace Bifrost.Upstream.OAuth;

/// <summary>Was ein Upstream über seine Autorisierung preisgibt (RFC 9728).</summary>
public sealed record ProtectedResourceMetadata(
    string Resource,
    IReadOnlyList<Uri> AuthorizationServers,
    IReadOnlyList<string> ScopesSupported);

/// <summary>Die Endpunkte eines Authorization Servers (RFC 8414 bzw. OIDC Discovery).</summary>
public sealed record AuthorizationServerMetadata(
    string Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    IReadOnlyList<string> CodeChallengeMethodsSupported,
    IReadOnlyList<string> ScopesSupported,
    bool IssuerParameterSupported);

/// <summary>
/// Findet heraus, wie ein HTTP-Upstream autorisiert werden will.
/// <para>
/// Der Ablauf ist der des Standards: unautorisiert anfragen → <c>401</c> mit
/// <c>WWW-Authenticate: Bearer resource_metadata="…"</c> → Protected Resource Metadata lesen →
/// daraus den Authorization Server bestimmen → dessen Metadaten holen.
/// </para>
/// <para>
/// <b>Jede dieser Adressen kommt vom Gegenüber.</b> Ein bösartiger oder übernommener Upstream kann
/// als Authorization Server auf einen internen Dienst zeigen und den Gateway so zum Abrufen fremder
/// Endpunkte bringen. Deshalb läuft jeder Abruf durch dieselbe Zielprüfung wie die Schemaimporte
/// (<see cref="RemoteSpecFetcher"/>) — dort wurde genau dieser Weg schon einmal geschlossen.
/// </para>
/// </summary>
public static class OAuthDiscovery
{
    /// <summary>Nur HTTPS. Ein Token über Klartext auszutauschen macht die ganze Übung sinnlos.</summary>
    private static void EnsureSecure(Uri uri, bool allowPrivateTargets)
    {
        if (uri.Scheme is not "https"
            && !(allowPrivateTargets && uri.IsLoopback))
        {
            throw new OAuthDiscoveryException(
                $"'{uri}' ist kein HTTPS. Autorisierungs-Endpunkte über Klartext sind nicht "
                + "verwendbar; für lokale Entwicklung greift AllowPrivateTargets auf Loopback.");
        }
    }

    /// <summary>
    /// Liest die <c>resource_metadata</c>-Adresse aus einer <c>401</c>-Antwort. Fehlt sie, spricht
    /// der Upstream diese Autorisierung nicht — dann ist ein Rateversuch auf einen
    /// Well-Known-Pfad die falsche Antwort, weil er eine fremde Adresse errät.
    /// </summary>
    public static Uri? ReadResourceMetadataUrl(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            if (!string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || header.Parameter is not { Length: > 0 } parameter)
            {
                continue;
            }

            foreach (var part in parameter.Split(','))
            {
                var trimmed = part.Trim();
                if (!trimmed.StartsWith("resource_metadata=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed["resource_metadata=".Length..].Trim().Trim('"');
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                {
                    return uri;
                }
            }
        }

        return null;
    }

    /// <summary>Die vom Server geforderten Scopes aus der Aufforderung (RFC 6750, Abschnitt 3).</summary>
    public static IReadOnlyList<string> ReadChallengedScopes(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            if (header.Parameter is not { Length: > 0 } parameter)
            {
                continue;
            }

            foreach (var part in parameter.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("scope=", StringComparison.OrdinalIgnoreCase))
                {
                    return [.. trimmed["scope=".Length..].Trim().Trim('"')
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)];
                }
            }
        }

        return [];
    }

    public static async Task<ProtectedResourceMetadata> FetchResourceMetadataAsync(
        Uri metadataUrl, bool allowPrivateTargets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadataUrl);
        EnsureSecure(metadataUrl, allowPrivateTargets);
        var json = await FetchJsonAsync(metadataUrl, allowPrivateTargets, ct).ConfigureAwait(false);

        var servers = ReadUriArray(json, "authorization_servers");
        if (servers.Count == 0)
        {
            throw new OAuthDiscoveryException(
                $"Die Protected Resource Metadata unter '{metadataUrl}' nennt keinen "
                + "Authorization Server (RFC 9728 verlangt mindestens einen).");
        }

        foreach (var server in servers)
        {
            EnsureSecure(server, allowPrivateTargets);
        }

        return new ProtectedResourceMetadata(
            json.TryGetProperty("resource", out var resource) ? resource.GetString() ?? string.Empty : string.Empty,
            servers,
            ReadStringArray(json, "scopes_supported"));
    }

    /// <summary>
    /// Holt die Metadaten des Authorization Servers. Probiert die beiden Wege des Standards in
    /// Reihenfolge: RFC 8414 und OpenID Connect Discovery — Clients müssen beide unterstützen.
    /// </summary>
    public static async Task<AuthorizationServerMetadata> FetchAuthorizationServerMetadataAsync(
        Uri issuer, bool allowPrivateTargets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        EnsureSecure(issuer, allowPrivateTargets);

        var basePath = issuer.AbsolutePath.TrimEnd('/');
        var candidates = new[]
        {
            new Uri(issuer, $"/.well-known/oauth-authorization-server{basePath}"),
            new Uri(issuer, $"/.well-known/openid-configuration{basePath}"),
            new Uri(issuer, $"{basePath}/.well-known/openid-configuration"),
        };

        OAuthDiscoveryException? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var json = await FetchJsonAsync(candidate, allowPrivateTargets, ct).ConfigureAwait(false);
                return ReadServerMetadata(json, issuer, allowPrivateTargets);
            }
            catch (OAuthDiscoveryException exception)
            {
                last = exception;
            }
        }

        throw new OAuthDiscoveryException(
            $"Keine Authorization-Server-Metadaten unter '{issuer}' gefunden. "
            + $"Zuletzt: {last?.Message}");
    }

    private static AuthorizationServerMetadata ReadServerMetadata(
        JsonElement json, Uri issuer, bool allowPrivateTargets)
    {
        var declaredIssuer = json.TryGetProperty("issuer", out var iss) ? iss.GetString() : null;
        if (string.IsNullOrEmpty(declaredIssuer))
        {
            throw new OAuthDiscoveryException("Metadaten ohne 'issuer'.");
        }

        // Der Issuer im Dokument muss der sein, den wir angefragt haben. Weicht er ab, beschreibt
        // das Dokument einen anderen Server — und die spätere iss-Prüfung liefe gegen einen Wert,
        // den wir uns von der Gegenseite haben vorgeben lassen.
        if (!string.Equals(declaredIssuer.TrimEnd('/'), issuer.ToString().TrimEnd('/'), StringComparison.Ordinal))
        {
            throw new OAuthDiscoveryException(
                $"Die Metadaten nennen den Issuer '{declaredIssuer}', angefragt war '{issuer}'.");
        }

        var authorization = ReadUri(json, "authorization_endpoint")
            ?? throw new OAuthDiscoveryException("Metadaten ohne 'authorization_endpoint'.");
        var token = ReadUri(json, "token_endpoint")
            ?? throw new OAuthDiscoveryException("Metadaten ohne 'token_endpoint'.");
        EnsureSecure(authorization, allowPrivateTargets);
        EnsureSecure(token, allowPrivateTargets);

        var methods = ReadStringArray(json, "code_challenge_methods_supported");

        // Der Standard ist hier ausdrücklich: Fehlt die Angabe oder fehlt S256, MUSS der Client
        // abbrechen. Ohne PKCE ist der Autorisierungscode abfangbar, und ein stiller Verzicht
        // wäre genau die Art Rückfall, die niemand bemerkt.
        if (!methods.Contains("S256", StringComparer.Ordinal))
        {
            throw new OAuthDiscoveryException(
                $"Der Authorization Server '{declaredIssuer}' weist PKCE mit S256 nicht aus "
                + "(code_challenge_methods_supported). Ohne PKCE wird nicht autorisiert.");
        }

        return new AuthorizationServerMetadata(
            declaredIssuer,
            authorization,
            token,
            methods,
            ReadStringArray(json, "scopes_supported"),
            json.TryGetProperty("authorization_response_iss_parameter_supported", out var supported)
                && supported.ValueKind is JsonValueKind.True);
    }

    private static async Task<JsonElement> FetchJsonAsync(
        Uri url, bool allowPrivateTargets, CancellationToken ct)
    {
        // Dieselbe Zielprüfung wie bei den Schemaimporten: Auflösung aller Adressen, Abweisung von
        // Loopback/privat/Link-Local, Weiterleitungen einzeln geprüft.
        var body = await RemoteSpecFetcher.FetchAsync(
                url, allowPrivateTargets, TimeSpan.FromSeconds(15), Fail, ct)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new OAuthDiscoveryException($"'{url}' lieferte kein gültiges JSON: {exception.Message}");
        }
    }

    private static Exception Fail(string message) => new OAuthDiscoveryException(message);

    private static Uri? ReadUri(JsonElement json, string name)
        => json.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.String
            && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
                ? uri
                : null;

    private static IReadOnlyList<Uri> ReadUriArray(JsonElement json, string name)
        => !json.TryGetProperty(name, out var array) || array.ValueKind is not JsonValueKind.Array
            ? []
            : [.. array.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => Uri.TryCreate(s, UriKind.Absolute, out _))
                .Select(s => new Uri(s!))];

    private static IReadOnlyList<string> ReadStringArray(JsonElement json, string name)
        => !json.TryGetProperty(name, out var array) || array.ValueKind is not JsonValueKind.Array
            ? []
            : [.. array.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)];
}

/// <summary>Die Autorisierung eines Upstreams ließ sich nicht ermitteln oder ist nicht verwendbar.</summary>
public sealed class OAuthDiscoveryException : Exception
{
    public OAuthDiscoveryException(string message) : base(message) { }

    public OAuthDiscoveryException() { }

    public OAuthDiscoveryException(string message, Exception innerException)
        : base(message, innerException) { }
}
