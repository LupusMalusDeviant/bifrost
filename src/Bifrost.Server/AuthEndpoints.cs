using System.Security.Claims;
using Bifrost.Abstractions;
using Bifrost.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Bifrost.Server;

/// <summary>
/// Cookie-Login/Logout als native Form-POST-Endpoints (WP6.1). Ein Blazor-Circuit kann keine
/// Cookies setzen, daher läuft die Anmeldung über diese klassischen HTTP-Endpoints.
/// </summary>
internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", async (
            HttpContext ctx, IUiUserService users, IAuditSink audit, TimeProvider time,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var user = await users.ValidateCredentialsAsync(username, password, ct);
            if (user is null)
            {
                audit.Record(new AuditEvent(
                    time.GetUtcNow(), null, CallOrigin.Ui, AuditEventKind.Authentication, null,
                    $"ui-login-failed:{username}", InvocationStatus.Denied, null, null, null, null));
                return Results.Redirect("/login?failed=true");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(UiPolicies.RoleClaim, user.Role.ToString()),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            audit.Record(new AuditEvent(
                time.GetUtcNow(), null, CallOrigin.Ui, AuditEventKind.Authentication, null,
                $"ui-login:{user.Username}", InvocationStatus.Success, null, null, null, null));

            WarnIfCookieWillBeDropped(ctx, loggerFactory);
            return Results.Redirect(IsLocal(returnUrl) ? returnUrl : "/");
        }).DisableAntiforgery(); // Login besitzt kein gültiges Antiforgery-Token vor der Anmeldung; Cookie SameSite=Strict schützt.

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    /// <summary>
    /// Sagt es, wenn die gerade ausgestellte Anmeldung nicht halten wird.
    /// <para>
    /// Außerhalb von Development trägt das Sitzungs-Cookie immer <c>Secure</c> (NFR-04). Kommt die
    /// Anfrage über Klartext-HTTP und terminiert auch kein Proxy davor TLS, <b>verwirft der Browser
    /// das Cookie stillschweigend</b>: Die Anmeldung sieht erfolgreich aus, der nächste Seitenaufruf
    /// ist wieder anonym, und es gibt keine Fehlermeldung — weder im Browser noch im Server.
    /// </para>
    /// <para>
    /// Beim Start ist das nicht entscheidbar, hier schon: <see cref="HttpRequest.IsHttps"/> plus
    /// <c>X-Forwarded-Proto</c> beantworten genau die Frage, die offen war. Deshalb steht die
    /// Prüfung an dieser Stelle und nicht in <c>Program.cs</c>.
    /// </para>
    /// </summary>
    /// <summary>
    /// Die Entscheidung selbst — ohne <see cref="HttpContext"/>, damit sie prüfbar ist. Der
    /// wichtigste Fall ist nicht der Fehlalarm, sondern der <b>Nicht</b>-Alarm hinter einem
    /// TLS-Proxy: Eine Warnung, die bei jedem korrekten Aufbau erscheint, wird weggeklickt.
    /// </summary>
    internal static bool WouldDropSessionCookie(
        bool requestIsHttps, bool forwardedProtoIsHttps, CookieSecurePolicy policy)
        => !requestIsHttps && !forwardedProtoIsHttps && policy is CookieSecurePolicy.Always;

    private static void WarnIfCookieWillBeDropped(HttpContext ctx, ILoggerFactory loggerFactory)
    {
        var forwardedHttps = ctx.Request.Headers["X-Forwarded-Proto"]
            .Any(v => string.Equals(v, "https", StringComparison.OrdinalIgnoreCase));
        var policy = ctx.RequestServices
            .GetService<IOptionsMonitor<CookieAuthenticationOptions>>()
            ?.Get(CookieAuthenticationDefaults.AuthenticationScheme)
            ?.Cookie.SecurePolicy;

        if (policy is null || !WouldDropSessionCookie(ctx.Request.IsHttps, forwardedHttps, policy.Value))
        {
            return;
        }

#pragma warning disable CA1848 // Ein Login ist selten; der Codegen brächte hier nichts.
        loggerFactory.CreateLogger("Bifrost.Server.AuthEndpoints").LogWarning(
            "Anmeldung über HTTP von {Host}: Das Sitzungs-Cookie ist 'Secure' und wird vom Browser " +
            "verworfen — die Anmeldung hält nicht. Abhilfe: die Web-UI über HTTPS aufrufen (TLS-Proxy " +
            "davor, der X-Forwarded-Proto setzt) oder für einen Test über http://localhost, das " +
            "Browser als sicheren Ursprung behandeln.",
            ctx.Request.Host.Value);
#pragma warning restore CA1848
    }

    private static bool IsLocal(string? url)
        => !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
}
