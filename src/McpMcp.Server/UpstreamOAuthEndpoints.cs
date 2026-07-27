using System.Collections.Concurrent;
using McpMcp.Abstractions;
using McpMcp.Upstream.OAuth;

namespace McpMcp.Server;

/// <summary>
/// Der interaktive Teil der Upstream-Autorisierung: Ein Administrator verbindet einen HTTP-Upstream
/// einmal im Browser, danach hält der Gateway das Token.
/// <para>
/// Die Endpunkte liegen unter <c>/oauth/upstream</c> und laufen über die <b>UI-Anmeldung</b>, nicht
/// über einen Agenten-Schlüssel: Ein Autorisierungsvorgang endet in einer Weiterleitung aus dem
/// Browser zurück, und der Rückweg trägt kein Bearer-Token.
/// </para>
/// </summary>
internal static class UpstreamOAuthEndpoints
{
    /// <summary>
    /// Laufende Vorgänge. Absichtlich nur im Speicher: Ein angefangener Vorgang lebt zehn Minuten
    /// und enthält den PKCE-Verifier — ihn zu persistieren hieße, ein kurzlebiges Geheimnis
    /// dauerhaft abzulegen, ohne dass ein Neustart mitten im Browser-Dialog ein Fall ist, den
    /// jemand überstehen müsste.
    /// </summary>
    private static readonly ConcurrentDictionary<string, OAuthAuthorizationAttempt> Attempts = new(StringComparer.Ordinal);

    public static void MapUpstreamOAuth(this WebApplication app)
    {
        var group = app.MapGroup("/oauth/upstream").RequireAuthorization(McpMcp.Web.UiPolicies.Admin);

        // Startet den Vorgang und schickt den Browser zum Authorization Server.
        group.MapGet("/{serverId:guid}/connect", async (
            Guid serverId, HttpContext ctx, IUpstreamSupervisor supervisor,
            IUpstreamConfigStore configStore, TimeProvider time, IAuditSink audit,
            CancellationToken ct) =>
        {
            var id = new ServerId(serverId);
            var status = supervisor.GetStatus(id);
            if (status is null)
            {
                return Results.NotFound();
            }

            var config = await CurrentConfigAsync(configStore, id, ct);
            if (config?.Http?.OAuth is not { } oauth)
            {
                return Results.BadRequest(new
                {
                    error = "Für diesen Upstream ist keine OAuth-Anbindung konfiguriert.",
                });
            }

            try
            {
                var (url, attempt) = await BeginAsync(id, config, oauth, ctx, time, ct);
                Prune(time.GetUtcNow());
                Attempts[attempt.State] = attempt;
                AuditOAuth(audit, time, ctx, id, $"upstream-oauth-start:{config.Slug}");

                // Weiterleitung statt JSON: Der Nutzer sitzt im Browser, und der Vorgang lebt davon,
                // dass er beim Authorization Server zustimmt.
                return Results.Redirect(url.ToString());
            }
            catch (OAuthDiscoveryException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        // Rückweg aus dem Browser.
        group.MapGet("/callback", async (
            string? code, string? state, string? iss, string? error, string? error_description,
            HttpContext ctx, IUpstreamConfigStore configStore, IUpstreamOAuthTokenStore tokens,
            IUpstreamSupervisor supervisor, TimeProvider time, IAuditSink audit,
            CancellationToken ct) =>
        {
            // Der State ist die Klammer um den Vorgang: Ohne ihn wüsste niemand, zu welchem
            // Upstream diese Antwort gehört — und jede Antwort wäre annehmbar.
            if (string.IsNullOrEmpty(state) || !Attempts.TryRemove(state, out var attempt))
            {
                return Results.BadRequest(new
                {
                    error = "Unbekannter oder abgelaufener Autorisierungsvorgang.",
                });
            }

            if (attempt.ExpiresAt <= time.GetUtcNow())
            {
                return Results.BadRequest(new { error = "Der Autorisierungsvorgang ist abgelaufen." });
            }

            try
            {
                // Auch die Fehlerantwort wird gegen den Issuer geprüft, bevor irgendetwas davon
                // angezeigt wird — sonst zeigte der Gateway den Fehlertext einer fremden Gegenstelle.
                OAuthFlow.EnsureIssuerMatches(attempt, iss);
            }
            catch (OAuthDiscoveryException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            if (!string.IsNullOrEmpty(error))
            {
                return Results.BadRequest(new
                {
                    error = $"Der Authorization Server hat abgelehnt: {error}. {error_description}",
                });
            }

            if (string.IsNullOrEmpty(code))
            {
                return Results.BadRequest(new { error = "Antwort ohne Autorisierungscode." });
            }

            var config = await CurrentConfigAsync(configStore, attempt.Server, ct);
            if (config?.Http?.OAuth is not { } oauth)
            {
                return Results.BadRequest(new { error = "Die OAuth-Anbindung wurde zwischenzeitlich entfernt." });
            }

            try
            {
                var token = await OAuthFlow.RedeemAsync(attempt, code, oauth, time.GetUtcNow(), ct);
                await tokens.SaveAsync(token, ct);
                AuditOAuth(audit, time, ctx, attempt.Server, $"upstream-oauth-connected:{config.Slug}");
            }
            catch (OAuthDiscoveryException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            // Zurück in die Verwaltung; der Upstream startet beim nächsten Verbindungsaufbau mit
            // dem Token.
            return Results.Redirect("/servers?oauth=verbunden");
        });

        // Trennt die Verbindung: Token weg, Upstream läuft ohne Autorisierung nicht mehr hoch.
        group.MapPost("/{serverId:guid}/disconnect", async (
            Guid serverId, HttpContext ctx, IUpstreamOAuthTokenStore tokens, TimeProvider time,
            IAuditSink audit, CancellationToken ct) =>
        {
            var id = new ServerId(serverId);
            await tokens.RemoveAsync(id, ct);
            AuditOAuth(audit, time, ctx, id, "upstream-oauth-disconnected");
            return Results.NoContent();
        });
    }

    private static async Task<(Uri Url, OAuthAuthorizationAttempt Attempt)> BeginAsync(
        ServerId id, UpstreamServerConfig config, UpstreamOAuthOptions oauth,
        HttpContext ctx, TimeProvider time, CancellationToken ct)
    {
        var endpoint = config.Http!.Endpoint;

        // Die kanonische URI des Upstreams ist der Resource Indicator (RFC 8707) — ohne Query und
        // ohne Fragment, mit Pfad, weil mehrere MCP-Server hinter einem Host liegen können.
        var resource = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');

        // Der Upstream sagt selbst, wo sein Authorization Server steht: unautorisiert anfragen und
        // die Aufforderung lesen. Geraten wird nichts.
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var unauthorized = await probe.GetAsync(endpoint, ct);
        var metadataUrl = OAuthDiscovery.ReadResourceMetadataUrl(unauthorized)
            ?? throw new OAuthDiscoveryException(
                $"'{endpoint}' fordert keine OAuth-Autorisierung an (kein 'resource_metadata' in "
                + "WWW-Authenticate). Entweder braucht der Server keine, oder er spricht eine andere.");

        var prm = await OAuthDiscovery.FetchResourceMetadataAsync(
            metadataUrl, oauth.AllowPrivateTargets, ct);
        var metadata = await OAuthDiscovery.FetchAuthorizationServerMetadataAsync(
            prm.AuthorizationServers[0], oauth.AllowPrivateTargets, ct);

        // Scope-Auswahl in der Reihenfolge des Standards: Aufforderung, dann konfigurierte Wunschliste,
        // dann was die Resource Metadata als unterstützt nennt.
        var scopes = OAuthDiscovery.ReadChallengedScopes(unauthorized);
        if (scopes.Count == 0)
        {
            scopes = oauth.Scopes is { Count: > 0 } configured ? configured : prm.ScopesSupported;
        }

        var redirect = new Uri($"{PublicBase(ctx)}/oauth/upstream/callback");
        return OAuthFlow.Begin(id, metadata, oauth, redirect, resource, scopes, time.GetUtcNow());
    }

    /// <summary>
    /// Die öffentlich erreichbare Adresse dieses Gateways — sie muss beim Authorization Server als
    /// Redirect-URI hinterlegt sein. <c>MCPMCP_PUBLIC_BASE_URL</c> gewinnt vor dem, was der Request
    /// behauptet: Host-Header sind vom Aufrufer setzbar, und eine erratene Redirect-URI führt
    /// entweder zu einer Ablehnung oder — schlimmer — zu einem Code an fremder Adresse.
    /// </summary>
    private static string PublicBase(HttpContext ctx)
    {
        var configured = ctx.RequestServices.GetService<IConfiguration>()?["MCPMCP_PUBLIC_BASE_URL"];
        return string.IsNullOrWhiteSpace(configured)
            ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
            : configured.TrimEnd('/');
    }

    private static async Task<UpstreamServerConfig?> CurrentConfigAsync(
        IUpstreamConfigStore store, ServerId id, CancellationToken ct)
    {
        var all = await store.GetAllLatestAsync(ct);
        return all.TryGetValue(id, out var version) ? version.Config : null;
    }

    private static void Prune(DateTimeOffset now)
    {
        foreach (var (key, attempt) in Attempts)
        {
            if (attempt.ExpiresAt <= now)
            {
                Attempts.TryRemove(key, out _);
            }
        }
    }

    private static void AuditOAuth(
        IAuditSink audit, TimeProvider time, HttpContext ctx, ServerId server, string detail)
        => audit.Record(new AuditEvent(
            time.GetUtcNow(), Caller: null, CallOrigin.Ui, AuditEventKind.ConfigChanged,
            server, Tool: null, Status: null, RedactedArguments: null,
            RequestBytes: null, ResponseBytes: null, Duration: null,
            Detail: $"{ctx.User.Identity?.Name ?? "?"}: {detail}"));
}
