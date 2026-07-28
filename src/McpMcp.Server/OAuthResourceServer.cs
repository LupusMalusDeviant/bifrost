using McpMcp.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace McpMcp.Server;

/// <summary>
/// Der Gateway als OAuth-Resource-Server (MCP-Autorisierung, RFC 9728 / RFC 8707).
/// <para>
/// Eingeschaltet, sobald ein Issuer konfiguriert ist. Ohne Konfiguration bleibt alles wie bisher —
/// API-Keys sind dann der einzige Weg, und das ist kein Mangel: Der Standard nennt Autorisierung
/// ausdrücklich <em>optional</em>.
/// </para>
/// <para>
/// <b>API-Keys bleiben auch danach bestehen.</b> Sie abzuschaffen wäre ein Bruch ohne Not; ein
/// Agent, der heute läuft, soll morgen weiterlaufen.
/// </para>
/// </summary>
/// <param name="Issuer">
/// Der Authorization Server, dem vertraut wird. Genau einer — mehrere hiessen mehrere
/// Vertrauensanker, und die Frage „welcher hat dieses Token ausgestellt" wäre dann Teil der
/// Autorisierung statt ihrer Voraussetzung.
/// </param>
/// <param name="Audience">
/// Die kanonische URI dieses Gateways. Ein Token, das nicht <b>für uns</b> ausgestellt wurde, wird
/// abgelehnt — der Standard verlangt das ausdrücklich, und es ist die Bedingung, die
/// Token-Weitergabe zwischen Diensten verhindert.
/// </param>
public sealed record OAuthResourceServerOptions(string Issuer, string Audience)
{
    /// <summary>
    /// Liest die Konfiguration. <c>null</c> heisst: nicht eingeschaltet.
    /// </summary>
    public static OAuthResourceServerOptions? FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issuer = configuration["MCPMCP_OAUTH_ISSUER"];
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        // Die Audience ist die Adresse, unter der Agenten uns erreichen. Ohne öffentliche Adresse
        // gäbe es nichts, worauf ein Token lauten könnte — deshalb ist sie hier Pflicht und nicht
        // geraten.
        var audience = configuration["MCPMCP_OAUTH_AUDIENCE"]
            ?? configuration["MCPMCP_PUBLIC_BASE_URL"]
            ?? throw new InvalidOperationException(
                "MCPMCP_OAUTH_ISSUER ist gesetzt, aber weder MCPMCP_OAUTH_AUDIENCE noch "
                + "MCPMCP_PUBLIC_BASE_URL. Ohne die eigene kanonische Adresse lässt sich nicht "
                + "prüfen, ob ein Token für diesen Gateway ausgestellt wurde.");

        return new OAuthResourceServerOptions(issuer.TrimEnd('/'), audience.TrimEnd('/'));
    }

    /// <summary>Die Adresse der Protected Resource Metadata — sie steht in jeder 401-Antwort.</summary>
    public string MetadataUrl => $"{Audience}/.well-known/oauth-protected-resource";
}

/// <summary>Prüft ein Zugriffstoken und liefert die Identität dahinter.</summary>
public interface IOAuthTokenValidator
{
    Task<IdentityId?> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>
/// Prüft Zugriffstoken gegen den JWKS des konfigurierten Authorization Servers und bildet das
/// Subject auf eine Gateway-Identität ab.
/// <para>
/// <b>Eine unbekannte Identität wird angelegt — ohne jeden Grant.</b> Das klingt zunächst
/// grosszügig und ist das Gegenteil: Default-Deny heisst, sie kann nichts, bis ein Administrator
/// ihr eine Rolle gibt. Die Alternative wäre, unbekannte Subjects abzuweisen — dann sieht ein
/// Administrator aber nie, wer angeklopft hat, und muss Identitäten blind anlegen.
/// </para>
/// </summary>
public sealed class OAuthTokenValidator : IOAuthTokenValidator, IDisposable
{
    private readonly OAuthResourceServerOptions _options;
    private readonly IRbacManagement _rbac;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _metadata;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SemaphoreSlim _provisionLock = new(1, 1);

    /// <summary>
    /// Subject → Identität. Ohne diesen Zwischenspeicher liefe bei jedem Request eine Abfrage über
    /// alle Identitäten — auf dem Authentifizierungspfad, also vor jedem einzelnen Tool-Aufruf.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IdentityId> _byName =
        new(StringComparer.Ordinal);

    public OAuthTokenValidator(OAuthResourceServerOptions options, IRbacManagement rbac)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rbac);
        _options = options;
        _rbac = rbac;

        // Der ConfigurationManager holt die Metadaten samt JWKS, hält sie vor und erneuert sie —
        // Schlüsselrotation beim Authorization Server bricht damit nichts, ohne dass wir einen
        // eigenen Zwischenspeicher pflegen.
        _metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{_options.Issuer}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public async Task<IdentityId?> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Count(c => c == '.') != 2)
        {
            // Kein JWT — das ist der Normalfall für einen API-Key und kein Fehler.
            return null;
        }

        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _metadata.GetConfigurationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Erreichbarkeitsprobleme dürfen nicht wie ein gültiges Token aussehen.
            return null;
        }

        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _options.Issuer,
            ValidateIssuer = true,
            // Die Audience-Prüfung ist der Kern: Ein Token, das für einen anderen Dienst
            // ausgestellt wurde, darf hier nichts bewirken. Ohne sie wäre der Gateway die Stelle,
            // an der fremde Token eingelöst werden.
            ValidAudience = _options.Audience,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        }).ConfigureAwait(false);

        if (!result.IsValid || result.ClaimsIdentity is null)
        {
            return null;
        }

        var subject = result.ClaimsIdentity.FindFirst("sub")?.Value;
        return string.IsNullOrEmpty(subject)
            ? null
            : await ResolveIdentityAsync(subject, ct).ConfigureAwait(false);
    }

    public void Dispose() => _provisionLock.Dispose();

    /// <summary>
    /// Findet die Identität zum Subject oder legt sie an — ohne Rollen und ohne Profil.
    /// </summary>
    private async Task<IdentityId> ResolveIdentityAsync(string subject, CancellationToken ct)
    {
        // Der Name trägt den Issuer mit: Zwei Authorization Server können dasselbe `sub` vergeben,
        // und ohne das Präfix fielen zwei verschiedene Menschen auf eine Identität zusammen.
        var name = $"oauth:{_options.Issuer}#{subject}";
        if (_byName.TryGetValue(name, out var cached))
        {
            return cached;
        }

        await _provisionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Zweite Prüfung im Schloss: Zwei gleichzeitige Erstanfragen desselben Subjects sollen
            // eine Identität ergeben, nicht zwei.
            if (_byName.TryGetValue(name, out cached))
            {
                return cached;
            }

            // Der Bestand wird nur bei einem Fehltreffer gelesen — also einmal je Subject und
            // Prozessleben, nicht je Request.
            var existing = (await _rbac.ListIdentitiesAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
            if (existing is not null)
            {
                _byName[name] = existing.Id;
                return existing.Id;
            }

            var identity = new Identity(IdentityId.New(), name, IdentityKind.Agent, [], null);
            await _rbac.UpsertIdentityAsync(identity, ct).ConfigureAwait(false);
            _byName[name] = identity.Id;
            return identity.Id;
        }
        finally
        {
            _provisionLock.Release();
        }
    }
}
