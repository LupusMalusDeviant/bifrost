using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpMcp.Abstractions;
using McpMcp.Upstream.Http;

namespace McpMcp.Upstream.OAuth;

/// <summary>
/// Der Autorisierungsablauf gegen einen Upstream: Authorization Code mit PKCE, Resource Indicator
/// und Issuer-Prüfung.
/// <para>
/// Die Klasse hält keinen Zustand. Was zwischen dem Öffnen des Browsers und der Rückkehr überlebt,
/// steht in einem <see cref="OAuthAuthorizationAttempt"/> — der Aufrufer legt ihn ab und reicht ihn
/// zurück. So bleibt der Ablauf testbar, ohne Persistenz oder HTTP-Kontext.
/// </para>
/// </summary>
public static class OAuthFlow
{
    /// <summary>
    /// Wie lange ein begonnener Vorgang gültig bleibt. Kurz, weil er nur die Zeitspanne überbrücken
    /// muss, in der jemand im Browser zustimmt.
    /// </summary>
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Baut die Adresse, an die der Browser geschickt wird, und den Vorgang, der dazu gehört.
    /// </summary>
    /// <param name="resource">
    /// Die kanonische URI des Upstreams. Sie geht als <c>resource</c> mit — der Standard verlangt
    /// das in Autorisierungs- <b>und</b> Token-Anfrage, und zwar unabhängig davon, ob der
    /// Authorization Server es unterstützt. Genau dieser Parameter bindet das Token an diesen einen
    /// Upstream und verhindert, dass es anderswo eingelöst wird.
    /// </param>
    public static (Uri AuthorizationUrl, OAuthAuthorizationAttempt Attempt) Begin(
        ServerId server,
        AuthorizationServerMetadata metadata,
        UpstreamOAuthOptions options,
        Uri redirectUri,
        string resource,
        IReadOnlyList<string> scopes,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        var query = new List<string>
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(options.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            "code_challenge_method=S256",
            $"resource={Uri.EscapeDataString(resource)}",
        };
        if (scopes.Count > 0)
        {
            query.Add($"scope={Uri.EscapeDataString(string.Join(' ', scopes))}");
        }

        var separator = string.IsNullOrEmpty(metadata.AuthorizationEndpoint.Query) ? "?" : "&";
        var url = new Uri(metadata.AuthorizationEndpoint + separator + string.Join('&', query));

        return (url, new OAuthAuthorizationAttempt(
            state, server, verifier, metadata.Issuer, metadata.TokenEndpoint, redirectUri,
            resource, scopes, now + AttemptLifetime)
        {
            // Was der Server beim Start über sich gesagt hat, gilt beim Zurückkommen. Der Wert
            // wurde bisher gelesen und weggeworfen — damit lief die Prüfung selbst dann nachsichtig,
            // wenn der Server den Parameter zugesagt hatte.
            IssuerParameterRequired = metadata.IssuerParameterSupported,
        });
    }

    /// <summary>
    /// Prüft den <c>iss</c>-Parameter der Antwort gegen den beim Start notierten Issuer (RFC 9207).
    /// <para>
    /// Ohne diese Prüfung lässt sich die Antwort eines <em>anderen</em> Authorization Servers
    /// unterschieben (Mix-up). Verglichen wird ohne Normalisierung — der Standard verbietet
    /// ausdrücklich, Groß-/Kleinschreibung, Standardports oder abschließende Schrägstriche
    /// anzugleichen, weil genau darin der Unterschied stecken kann.
    /// </para>
    /// </summary>
    public static void EnsureIssuerMatches(OAuthAuthorizationAttempt attempt, string? issuerFromResponse)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (issuerFromResponse is null)
        {
            // Fehlt der Parameter, entscheidet die Zusage des Servers aus den Metadaten. Hat er
            // 'authorization_response_iss_parameter_supported' ausgewiesen, ist sein Fehlen genau
            // das Bild, gegen das RFC 9207 schützt: eine Antwort, die nicht von ihm stammt. Dann
            // wird abgebrochen.
            if (attempt.IssuerParameterRequired)
            {
                throw new OAuthDiscoveryException(
                    $"Die Antwort trägt keinen 'iss'-Parameter, obwohl der Authorization Server "
                    + $"'{attempt.ExpectedIssuer}' ihn in seinen Metadaten zusagt. Der Vorgang wird "
                    + "abgebrochen.");
            }

            // Sonst bleibt es bei der Nachsicht — die Bindung an den Token-Endpunkt aus demselben
            // Metadaten-Dokument trägt hier weiter.
            return;
        }

        if (!string.Equals(issuerFromResponse, attempt.ExpectedIssuer, StringComparison.Ordinal))
        {
            throw new OAuthDiscoveryException(
                $"Die Antwort trägt den Issuer '{issuerFromResponse}', erwartet war "
                + $"'{attempt.ExpectedIssuer}'. Der Vorgang wird abgebrochen.");
        }
    }

    /// <summary>Tauscht den Autorisierungscode gegen ein Token.</summary>
    public static Task<UpstreamOAuthToken> RedeemAsync(
        OAuthAuthorizationAttempt attempt, string code, UpstreamOAuthOptions options,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return PostTokenAsync(attempt.TokenEndpoint, options, attempt.Server, attempt.ExpectedIssuer, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = attempt.RedirectUri.ToString(),
            ["code_verifier"] = attempt.CodeVerifier,
            ["resource"] = attempt.Resource,
        }, attempt.Scopes, now, ct);
    }

    /// <summary>Erneuert ein Token. Der Resource-Parameter geht auch hier mit.</summary>
    public static Task<UpstreamOAuthToken> RefreshAsync(
        UpstreamOAuthToken existing, Uri tokenEndpoint, UpstreamOAuthOptions options,
        string resource, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (existing.RefreshToken is not { Length: > 0 } refresh)
        {
            throw new OAuthDiscoveryException(
                "Für diesen Upstream gibt es kein Refresh-Token — die Verbindung muss neu "
                + "hergestellt werden.");
        }

        return PostTokenAsync(tokenEndpoint, options, existing.Server, existing.Issuer, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["resource"] = resource,
        }, existing.Scopes, now, ct);
    }

    private static async Task<UpstreamOAuthToken> PostTokenAsync(
        Uri tokenEndpoint, UpstreamOAuthOptions options, ServerId server, string issuer,
        Dictionary<string, string> form, IReadOnlyList<string> requestedScopes,
        DateTimeOffset now, CancellationToken ct)
    {
        await RemoteSpecFetcher
            .EnsureTargetAllowedAsync(tokenEndpoint, options.AllowPrivateTargets, Fail, ct)
            .ConfigureAwait(false);

        form["client_id"] = options.ClientId;

        // Kein automatisches Folgen von Weiterleitungen: Ein 302 am Token-Endpunkt schickte
        // Client-Secret und Code an eine Adresse, die nie geprüft wurde.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        // Client-Secret im Authorization-Header statt im Rumpf: So steht es nicht in
        // Zugriffsprotokollen, die den Body mitschreiben.
        if (options.ClientSecret is { Length: > 0 } secret)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{Uri.EscapeDataString(options.ClientId)}:{Uri.EscapeDataString(secret)}")));
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Der Fehlertext des Servers kann Details enthalten, aber kein Token — die Antwort
            // eines gescheiterten Token-Requests trägt `error`/`error_description`.
            throw new OAuthDiscoveryException(
                $"Token-Anfrage scheiterte mit {(int)response.StatusCode}: {Shorten(body)}");
        }

        JsonElement json;
        try
        {
            using var document = JsonDocument.Parse(body);
            json = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new OAuthDiscoveryException($"Token-Antwort ist kein gültiges JSON: {exception.Message}");
        }

        var accessToken = json.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new OAuthDiscoveryException("Token-Antwort ohne 'access_token'.");
        }

        var expires = json.TryGetProperty("expires_in", out var exp) && exp.TryGetInt64(out var seconds)
            ? now + TimeSpan.FromSeconds(seconds)
            : (DateTimeOffset?)null;

        // Der Server darf weniger gewähren, als angefragt wurde — dann gilt seine Angabe.
        var granted = json.TryGetProperty("scope", out var scope) && scope.GetString() is { Length: > 0 } text
            ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [.. requestedScopes];

        return new UpstreamOAuthToken(
            server,
            accessToken,
            json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            expires,
            granted,
            issuer,
            now);
    }

    private static Exception Fail(string message) => new OAuthDiscoveryException(message);

    private static string Shorten(string value)
        => value.Length <= 500 ? value : value[..500] + "…";

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
