using System.Collections.Concurrent;
using Faolan.Abstractions;
using Faolan.Persistence.Audit;
using Faolan.Upstream.OAuth;

namespace Faolan.Server;

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
        var group = app.MapGroup("/oauth/upstream").RequireAuthorization(Faolan.Web.UiPolicies.Admin);

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
                // Nur ein bekannter Fehlercode, nicht error_description: Beides kommt aus der
                // Adresszeile und ist Fremdtext, der in der Antwort an den Browser stünde (Codex H1).
                return Results.BadRequest(new
                {
                    error = $"Der Authorization Server hat abgelehnt ({OAuthFlow.KnownErrorCode(error) ?? "unbekannter Fehlercode"}).",
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

            UpstreamOAuthToken token;
            try
            {
                OAuthFlow.EnsureBindingMatches(attempt, config);
                token = await OAuthFlow.RedeemAsync(attempt, code, oauth, time.GetUtcNow(), ct);
            }
            catch (OAuthDiscoveryException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            return await StoreGrantAsync(
                attempt, token, config.Slug, ctx.User.Identity?.Name,
                c => CurrentConfigAsync(configStore, attempt.Server, c), tokens,
                supervisor as IUpstreamCredentialObserver, audit, time, ct);
        });

        // Trennt die Verbindung: Token weg, Upstream läuft ohne Autorisierung nicht mehr hoch.
        group.MapPost("/{serverId:guid}/disconnect", async (
            Guid serverId, HttpContext ctx, IUpstreamOAuthTokenStore tokens, TimeProvider time,
            IAuditSink audit, CancellationToken ct) =>
        {
            var id = new ServerId(serverId);

            // Erst das Audit, dann die Wirkung — wie beim Rückweg (Plan 0005 F3).
            try
            {
                AuditOAuth(audit, time, ctx, id, "upstream-oauth-disconnected");
            }
            catch (AuditUnavailableException)
            {
                return Results.Json(
                    new { error = "Das Audit nimmt gerade nichts an; die Verbindung wurde nicht getrennt." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            await tokens.RemoveAsync(id, ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Was nach dem Einlösen des Codes geschieht: Token ablegen, Bindung erneut prüfen, Upstream
    /// wecken. Herausgelöst, damit sich die Reihenfolge ohne Browser-Dialog prüfen lässt.
    /// </summary>
    [Faolan.Core.Execution.NoHostExecution(
        "Liest die Konfiguration nur zum Vergleich der Token-Bindung; gestartet wird über den Supervisor.")]
    internal static async Task<IResult> StoreGrantAsync(
        OAuthAuthorizationAttempt attempt, UpstreamOAuthToken token, string slug, string? user,
        Func<CancellationToken, Task<UpstreamServerConfig?>> currentConfig,
        IUpstreamOAuthTokenStore tokens, IUpstreamCredentialObserver? observer,
        IAuditSink audit, TimeProvider time, CancellationToken ct)
    {
        // Erst das Audit, dann die Wirkung (Plan 0005 F3): Weist es ab (Compliance-Modus, voller
        // Kanal), ist kein Token gespeichert und nichts geweckt.
        try
        {
            AuditOAuth(audit, time, user, attempt.Server, $"upstream-oauth-connected:{slug}");
        }
        catch (AuditUnavailableException)
        {
            return Results.Json(
                new
                {
                    error = "Das Audit nimmt gerade nichts an; die Autorisierung wurde nicht gespeichert. "
                        + "Bitte später erneut autorisieren.",
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Mit der Kennung, unter der es liegt: Nur dieser Stand darf unten wieder entfernt werden.
        var own = token with { Revision = await tokens.SaveWithRevisionAsync(token, ct) };
        try
        {
            // Nach dem Speichern noch einmal: Wurde die Anbindung zwischen Prüfung und Speichern
            // geändert, hat das Umkonfigurieren das Token womöglich vor uns entfernt — dann
            // entfernen wir es hier (E2). Umkonfigurieren speichert zuerst und entfernt danach,
            // also sieht einer der beiden Wege den neuen Stand. Entfernt wird nur das EIGENE Token
            // (F5): Hat ein zweiter Vorgang inzwischen eine neuere Autorisierung gespeichert, bleibt
            // sie stehen.
            var after = await currentConfig(ct);
            if (after is null)
            {
                await tokens.RemoveIfUnchangedAsync(own, CancellationToken.None);
                return Results.BadRequest(new { error = "Der Upstream wurde zwischenzeitlich entfernt." });
            }

            try
            {
                OAuthFlow.EnsureBindingMatches(attempt, after);
            }
            catch (OAuthDiscoveryException exception)
            {
                await tokens.RemoveIfUnchangedAsync(own, CancellationToken.None);
                return Results.BadRequest(new { error = exception.Message });
            }
        }
        finally
        {
            // Ab dem Speichern immer (F3): Wartet der Upstream gerade auf seinen nächsten Versuch —
            // nach abgelehnter Anmeldung bis zu 15 min —, verbindet er jetzt mit dem neuen Token,
            // statt die Frist abzusitzen (L5). Läuft gerade ein Versuch mit dem alten Token, folgt
            // auf sein Scheitern der nächste ohne Frist (D11). Hat die Nachprüfung das Token wieder
            // entfernt, scheitert dieser Versuch an der Anmeldung — ein Versuch zu viel ist billiger
            // als ein Upstream, der ein gültiges Token 15 min lang nicht benutzt. Verbunden,
            // abgeschaltet oder entfernt: nichts.
            observer?.CredentialsChanged(attempt.Server);
        }

        // Zurück in die Verwaltung.
        return Results.Redirect("/servers?oauth=verbunden");
    }

    private static async Task<(Uri Url, OAuthAuthorizationAttempt Attempt)> BeginAsync(
        ServerId id, UpstreamServerConfig config, UpstreamOAuthOptions oauth,
        HttpContext ctx, TimeProvider time, CancellationToken ct)
    {
        var endpoint = config.Http!.Endpoint;

        // Die kanonische URI des Upstreams ist der Resource Indicator (RFC 8707) — ohne Query und
        // ohne Fragment, mit Pfad, weil mehrere MCP-Server hinter einem Host liegen können.
        var resource = OAuthAuthorizationAttempt.ResourceFor(endpoint);

        // Diese Sonde ging ungeprüft gegen die vom Betreiber genannte Adresse — und drei Zeilen
        // weiter unten wird derselbe Schalter an jede Discovery-Anfrage weitergereicht. Der Code
        // kannte die Regel also und wandte sie eine Zeile zu spät an; gefunden hat das der
        // Architekturtest aus WP3.6, der fragt, WER überhaupt eine Verbindung öffnet.
        await OAuthDiscovery.EnsureTargetAllowedAsync(endpoint, oauth.AllowPrivateTargets, ct);

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
    /// Redirect-URI hinterlegt sein. <c>FAOLAN_PUBLIC_BASE_URL</c> gewinnt vor dem, was der Request
    /// behauptet: Host-Header sind vom Aufrufer setzbar, und eine erratene Redirect-URI führt
    /// entweder zu einer Ablehnung oder — schlimmer — zu einem Code an fremder Adresse.
    /// </summary>
    private static string PublicBase(HttpContext ctx)
    {
        var configured = ctx.RequestServices.GetService<IConfiguration>()?["FAOLAN_PUBLIC_BASE_URL"];
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
        => AuditOAuth(audit, time, ctx.User.Identity?.Name, server, detail);

    private static void AuditOAuth(
        IAuditSink audit, TimeProvider time, string? user, ServerId server, string detail)
        => audit.Record(new AuditEvent(
            time.GetUtcNow(), Caller: null, CallOrigin.Ui, AuditEventKind.ConfigChanged,
            server, Tool: null, Status: null, RedactedArguments: null,
            RequestBytes: null, ResponseBytes: null, Duration: null,
            Detail: $"{user ?? "?"}: {detail}"));
}
