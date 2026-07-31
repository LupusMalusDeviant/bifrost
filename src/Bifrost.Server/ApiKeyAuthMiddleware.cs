using Bifrost.Abstractions;

namespace Bifrost.Server;

/// <summary>
/// API-Key-AuthN für den MCP-Endpoint (FR-27, WP4.4): Bearer-Token → Identität.
/// Fehlversuche werden auditiert (FR-22); Health-Endpoints bleiben anonym.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    public const string IdentityItemKey = "Bifrost.IdentityId";

    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context, IApiKeyValidator validator, IAuditSink audit, TimeProvider time,
        GatewayIdentity gateway)
    {
        // Bewusst aus dem Request-Container statt als Methodenparameter: ASP.NET löst
        // Middleware-Parameter zwingend auf und kennt keine optionalen — ohne registrierten
        // Validator (also im Normalfall, wenn kein Issuer konfiguriert ist) schlüge die Auflösung
        // fehl und jeder Request endete in einem 500.
        var oauth = context.RequestServices.GetService<IOAuthTokenValidator>();
        var oauthOptions = context.RequestServices.GetService<OAuthResourceServerOptions>();

        if (!context.Request.Path.StartsWithSegments("/mcp")
            && !context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Federations-Loop (FR-05): trägt der Aufruf unsere eigene Instanz-Kennung, ist der
        // Gateway direkt als sein eigener Upstream konfiguriert → abweisen.
        //
        // Grenze, bewusst und im Threat-Model dokumentiert: erkannt wird nur der DIREKTE Selbstbezug.
        // Für eine Kette A→B→A müsste die Instanz-Liste pro Call weitergereicht werden; die
        // Header der Upstream-Verbindung werden aber einmal beim Verbindungsaufbau gesetzt und
        // kennen den auslösenden Request nicht. Eine Meldung, die "transitiv" verspricht, wäre
        // eine Zusicherung, die der Mechanismus nicht einlöst.
        if (context.Request.Headers.TryGetValue(GatewayIdentity.InstanceHeader, out var instance)
            && instance == gateway.InstanceId)
        {
            context.Response.StatusCode = StatusCodes.Status508LoopDetected;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Federations-Loop erkannt: Dieser Gateway ist als sein eigener Upstream konfiguriert.",
            });
            return;
        }

        var token = ExtractBearer(context.Request);

        // API-Key zuerst: Er ist der bestehende Weg, und ein Agent, der heute läuft, soll ohne
        // Umstellung weiterlaufen. Erst wenn das nichts ergibt, wird das Token als JWT geprüft —
        // ein API-Key hat keine drei Punkte und fällt dort ohnehin sofort durch.
        var identity = token is null
            ? null
            : await validator.ValidateAsync(token, context.RequestAborted);

        if (identity is null && token is not null && oauth is not null)
        {
            identity = await oauth.ValidateAsync(token, context.RequestAborted);
        }

        if (identity is null)
        {
            audit.Record(new AuditEvent(
                time.GetUtcNow(), null, CallOrigin.Mcp, AuditEventKind.Authentication, null,
                null, InvocationStatus.Denied, null, null, null, null));
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;

            // Ist der Gateway Resource Server, MUSS die Aufforderung auf seine Protected Resource
            // Metadata zeigen (RFC 9728) — genau daran findet ein MCP-Client den zuständigen
            // Authorization Server. Ohne den Verweis müsste er raten.
            context.Response.Headers.WWWAuthenticate = oauthOptions is null
                ? "Bearer"
                : $"Bearer resource_metadata=\"{oauthOptions.MetadataUrl}\"";
            await context.Response.WriteAsJsonAsync(new
            {
                error = oauthOptions is null
                    ? "API-Key fehlt, ist ungültig oder widerrufen."
                    : "Zugangsdaten fehlen oder sind ungültig: weder gültiger API-Key noch gültiges Zugriffstoken.",
            });
            return;
        }

        context.Items[IdentityItemKey] = identity.Value;
        await _next(context);
    }

    private static string? ExtractBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}
