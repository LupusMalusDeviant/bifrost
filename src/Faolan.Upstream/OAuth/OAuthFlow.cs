using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Faolan.Abstractions;
using Faolan.Upstream.Http;

namespace Faolan.Upstream.OAuth;

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
            ClientId = options.ClientId,
        });
    }

    /// <summary>
    /// Gehört der Vorgang noch zur aktuellen Konfiguration (Plan 0005 E2)? Wurde zwischen Start und
    /// Rückkehr der Endpunkt oder die Client-ID geändert, wird der Code nicht eingelöst — sonst
    /// landete ein Token für die alte Ressource oder den alten Client unter dem Upstream.
    /// </summary>
    public static void EnsureBindingMatches(OAuthAuthorizationAttempt attempt, UpstreamServerConfig current)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(current);
        if (current.Http?.OAuth is not { } oauth
            || !string.Equals(attempt.ClientId, oauth.ClientId, StringComparison.Ordinal)
            || !string.Equals(
                attempt.Resource, OAuthAuthorizationAttempt.ResourceFor(current.Http.Endpoint), StringComparison.Ordinal))
        {
            throw new OAuthDiscoveryException(
                "Die OAuth-Anbindung des Upstreams wurde während des Vorgangs geändert. Der Vorgang "
                + "wird abgebrochen; bitte erneut autorisieren.");
        }
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
            // Der empfangene Wert steht nicht in der Meldung: Er kommt aus der Adresszeile und
            // ginge wörtlich an den Browser (Codex-Review, Plan 0005 D2).
            throw new OAuthDiscoveryException(
                $"Die Antwort stammt nicht vom erwarteten Issuer '{attempt.ExpectedIssuer}'. "
                + "Der Vorgang wird abgebrochen.");
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

        return KeepRefreshTokenAsync(PostTokenAsync(tokenEndpoint, options, existing.Server, existing.Issuer, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["resource"] = resource,
        }, existing.Scopes, now, ct), refresh);
    }

    /// <summary>
    /// Eine Erneuerung darf das Refresh-Token weglassen (RFC 6749 §6) — dann gilt das bisherige
    /// weiter. Ohne das ginge es verloren, und der nächste Ablauf verlangte ein neues Autorisieren
    /// von Hand (Plan 0005 D9).
    /// </summary>
    private static async Task<UpstreamOAuthToken> KeepRefreshTokenAsync(Task<UpstreamOAuthToken> refreshing, string previous)
    {
        var token = await refreshing.ConfigureAwait(false);
        return token.RefreshToken is { Length: > 0 } ? token : token with { RefreshToken = previous };
    }

    private static async Task<UpstreamOAuthToken> PostTokenAsync(
        Uri tokenEndpoint, UpstreamOAuthOptions options, ServerId server, string issuer,
        Dictionary<string, string> form, IReadOnlyList<string> requestedScopes,
        DateTimeOffset now, CancellationToken ct)
    {
        // Ein unauflösbarer Name ist ein Netzfehler, keine Ablehnung (Plan 0005 G3): Er reist mit
        // seiner SocketException, damit die Erneuerung ihn als Unerreichbarkeit einordnet.
        await RemoteSpecFetcher
            .CheckTargetAsync(tokenEndpoint, options.AllowPrivateTargets, Fail, OAuthDiscovery.Unresolvable, ct)
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
            // Nur Status und bekannter Fehlercode, nie der Antwortkörper (Codex H1): Ein Server oder
            // Proxy, der die Anfrage spiegelt, gäbe sonst Code, Verifier oder den Basic-Header mit
            // dem Client-Secret wieder — und die Meldung landet in der Antwort an den Browser.
            var code = KnownErrorCode(body);
            throw new OAuthDiscoveryException(
                $"Token-Anfrage scheiterte mit HTTP {(int)response.StatusCode}"
                + (code is null ? "." : $" ({code})."))
            {
                StatusCode = response.StatusCode,
            };
        }

        JsonElement json;
        try
        {
            using var document = JsonDocument.Parse(body);
            json = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new OAuthDiscoveryException("Token-Antwort ist kein gültiges JSON.");
        }

        // Die Form wird geprüft, bevor gelesen wird (Plan 0005 H7, wie G3 bei der Discovery): Ein Array
        // oder ein Feld vom falschen Typ würfe sonst eine InvalidOperationException, die am Connector
        // vorbeiläuft und als „nicht erreichbar" (kurze Frist) endet, beim Rückruf als 500.
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw new OAuthDiscoveryException("Token-Antwort ist kein JSON-Objekt.");
        }

        var accessToken = ReadTokenString(json, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new OAuthDiscoveryException("Token-Antwort ohne 'access_token'.");
        }

        // Begrenzt: Ein absurder Wert würfe sonst in TimeSpan eine Ausnahme am selben Fehlerweg vorbei.
        var expires = ReadSeconds(json, "expires_in") is { } seconds
            ? now + TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaximumTokenLifetimeSeconds))
            : (DateTimeOffset?)null;

        // Der Server darf weniger gewähren, als angefragt wurde — dann gilt seine Angabe.
        var granted = ReadTokenString(json, "scope") is { Length: > 0 } text
            ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [.. requestedScopes];

        return new UpstreamOAuthToken(
            server,
            accessToken,
            ReadTokenString(json, "refresh_token"),
            expires,
            granted,
            issuer,
            now)
        {
            // Gebunden an genau das, was in der Anfrage stand (Plan 0005 F5): den Resource Indicator
            // und die Client-ID. Eine Erneuerung übernimmt damit die Bindung des Vorgängers — der
            // Connector hat vorher geprüft, dass sie zur Konfiguration passt — und schreibt sie bei
            // einem Token aus der Zeit vor der Bindung nach.
            Resource = form["resource"],
            ClientId = options.ClientId,
        };
    }

    private static Exception Fail(string message) => new OAuthDiscoveryException(message);

    /// <summary>Zehn Jahre — länger lebt kein Token, das ein Anbieter ernst meint.</summary>
    private const long MaximumTokenLifetimeSeconds = 10L * 365 * 24 * 60 * 60;

    /// <summary>Ein Textfeld der Token-Antwort; fehlt es oder ist es <c>null</c>, gibt es nichts. Ein anderer Typ ist ein Formfehler.</summary>
    private static string? ReadTokenString(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new OAuthDiscoveryException($"Token-Antwort: '{name}' ist kein Text.");
        }

        // Plan 0005 I5: Ein Text mit ungültigem Escape (ein einzelnes Surrogat wie "\uD800") hat die
        // Art String, wirft beim Lesen aber eine InvalidOperationException — am Connector vorbei.
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            throw new OAuthDiscoveryException($"Token-Antwort: '{name}' ist kein gültiger Text.");
        }
    }

    /// <summary>
    /// Eine Laufzeit in Sekunden. Manche Anbieter senden sie als Text („3600"); der wird gelesen,
    /// solange er eine ganze Zahl ist. Alles andere ist ein Formfehler.
    /// </summary>
    private static long? ReadSeconds(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var number))
            {
                return number;
            }

            // Eine Kommazahl galt vorher als „keine Angabe"; abgerundet ist sie nützlicher. Zu große
            // Zahlen liest .NET als ±Unendlich; begrenzt wird vor der Umwandlung (Plan 0005 I8).
            var fraction = value.GetDouble();
            return fraction >= MaximumTokenLifetimeSeconds ? MaximumTokenLifetimeSeconds
                : fraction <= 0 ? 0
                : (long)Math.Floor(fraction);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string? text;
            try
            {
                text = value.GetString();
            }
            catch (InvalidOperationException)
            {
                text = null;
            }

            if (long.TryParse(text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            // I8: Ziffern über long.MaxValue sind wie die gleiche JSON-Zahl — begrenzt, nicht abgelehnt.
            if (!string.IsNullOrEmpty(text) && text.All(char.IsAsciiDigit))
            {
                return MaximumTokenLifetimeSeconds;
            }
        }

        throw new OAuthDiscoveryException($"Token-Antwort: '{name}' ist keine ganze Zahl.");
    }

    /// <summary>
    /// Die Fehlercodes aus RFC 6749 (5.2, 4.1.2.1) und RFC 8707. Nur sie werden aus einer Antwort
    /// übernommen; alles andere, auch <c>error_description</c>, ist Fremdtext.
    /// </summary>
    private static readonly HashSet<string> KnownErrorCodes = new(StringComparer.Ordinal)
    {
        "invalid_request", "invalid_client", "invalid_grant", "unauthorized_client",
        "unsupported_grant_type", "invalid_scope", "access_denied", "unsupported_response_type",
        "server_error", "temporarily_unavailable", "invalid_target",
    };

    /// <summary>Der Fehlercode einer OAuth-Antwort, wenn er bekannt ist — sonst <c>null</c>.</summary>
    public static string? KnownErrorCode(string? codeOrBody)
    {
        if (string.IsNullOrEmpty(codeOrBody))
        {
            return null;
        }

        if (KnownErrorCodes.Contains(codeOrBody))
        {
            return codeOrBody;
        }

        try
        {
            using var document = JsonDocument.Parse(codeOrBody);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { } code
                && KnownErrorCodes.Contains(code)
                    ? code
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // Plan 0005 J7: Ein Text mit ungültigem Escape (einzelnes Surrogat) ist kein bekannter Code.
            // Würfe das hier, ginge der HTTP-Status verloren — die Ablehnung würde „nicht erreichbar".
            return null;
        }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
