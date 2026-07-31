using System.Security.Claims;

using Bifrost.Abstractions;
using Bifrost.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Bifrost.Server.Bootstrap;

/// <summary>
/// Der Einlösepfad als nativer Form-POST — dasselbe Muster wie die Anmeldung (WP6.1): Ein
/// Blazor-Circuit kann kein Cookie setzen, und am Ende dieses Vorgangs soll genau das passieren.
/// <para>
/// <b>Warum das der einzige anonyme Schreibweg bleiben darf.</b> Er tut nur dann etwas, wenn ein
/// Token vorliegt, das zum gespeicherten Hash passt, noch gilt und noch nicht verbraucht ist.
/// Fehlt eine dieser Bedingungen, ist die Antwort eine Umleitung mit einem Grund — und ein
/// Auditeintrag. Es gibt hier keinen Weg, ein Token <i>anzufordern</i>: Der entsteht ausschließlich
/// lokal (siehe <see cref="BootstrapOrigin"/>).
/// </para>
/// </summary>
public static class BootstrapEndpoints
{
    /// <summary>Die Adresse der Setup-Oberfläche.</summary>
    public const string SetupPath = "/setup";

    public static void MapBootstrapEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/auth/setup", async (
            HttpContext ctx, IBootstrapService bootstrap, CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var result = await bootstrap.RedeemAsync(
                form["token"].ToString().Trim(),
                form["username"].ToString().Trim(),
                form["password"].ToString(),
                ct);

            if (result.Outcome is not BootstrapOutcome.Redeemed)
            {
                // Der Grund reist als Code, nicht als Fließtext: Ein Fließtext in der Adresszeile
                // ist ein offener Weg für fremde Inhalte in die eigene Seite.
                return Results.Redirect($"{SetupPath}?failed={result.Outcome}");
            }

            // Direkt angemeldet weiterschicken: Wer gerade Benutzername und Passwort gesetzt hat,
            // soll sie nicht sofort noch einmal eintippen — und ein zweiter Weg durch die Anmeldung
            // wäre ein zweiter Weg, an dem etwas schiefgehen kann.
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, result.Username!),
                new(ClaimTypes.NameIdentifier, result.UserId!.Value.ToString()),
                new(UiPolicies.RoleClaim, nameof(UiRole.Admin)),
            };
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(
                    claims, CookieAuthenticationDefaults.AuthenticationScheme)));

            return Results.Redirect("/");
        }).DisableAntiforgery(); // Wie bei der Anmeldung: vor dem Zugang gibt es kein gültiges Token.
    }
}
