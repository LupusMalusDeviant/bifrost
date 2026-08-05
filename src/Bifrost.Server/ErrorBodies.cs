using System.Net.Mime;
using System.Text.Json;

namespace Bifrost.Server;

/// <summary>
/// Gibt einer Fehlerantwort ohne Rumpf einen Rumpf (<c>BFR-F-0053</c>).
/// <para>
/// <b>Der Befund.</b> Im Piloten vom 2026-08-05 antwortete <c>/rbac/keys</c> — ein Pfad aus der
/// eigenen Dokumentation, der so nie existierte — mit <c>404</c> und <b>null Bytes</b>. Der
/// Statuscode war richtig, im Browser stand eine weiße Seite: kein Hinweis, kein Rückweg, kein
/// Kennzeichen des Produkts. Ein Tippfehler, ein altes Lesezeichen oder ein veralteter Link sieht so
/// aus wie ein Ausfall — und ein Betreiber, der einen Ausfall vermutet, fängt an, den falschen
/// Fehler zu suchen.
/// </para>
/// <para>
/// <b>Warum <see cref="StatusCodePagesExtensions.UseStatusCodePages(IApplicationBuilder, Func{StatusCodeContext, Task})"/>
/// und keine Blazor-<c>NotFound</c>-Vorlage.</b> Die griffe nur für Pfade, die überhaupt beim Router
/// ankommen. Der leere Rumpf entsteht aber schon davor, im Endpunkt-Routing — und er entsteht
/// genauso bei <c>/api</c>-Pfaden, die es nicht gibt. Diese Stelle sieht beide, und sie greift
/// ausschließlich dort, wo <b>niemand sonst</b> etwas geschrieben hat: Jede Fehlerantwort mit
/// eigenem Rumpf — die JSON-Absagen der Betriebsendpunkte, der Riegel aus <c>BFR-F-0047</c> —
/// bleibt unberührt.
/// </para>
/// </summary>
public static class ErrorBodies
{
    /// <summary>
    /// Wer eine Maschine ist, bekommt JSON. Die Form ist dieselbe wie bei den Betriebsendpunkten
    /// (<c>{"error":{"code","message"}}</c>) — ein zweites Fehlerformat für dieselbe Sache ließe
    /// jeden Client raten, welches gerade gilt.
    /// </summary>
    private static bool WantsJson(HttpContext context)
        => context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/mcp")
            || context.Request.Path.StartsWithSegments("/.well-known")
            || (context.Request.Headers.Accept.ToString() is { Length: > 0 } accept
                && accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
                && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase));

    public static IApplicationBuilder UseBifrostErrorBodies(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseStatusCodePages(async context =>
        {
            var response = context.HttpContext.Response;
            var status = response.StatusCode;

            var (code, message) = status switch
            {
                StatusCodes.Status404NotFound => (
                    "not-found",
                    "Diese Adresse gibt es nicht."),
                StatusCodes.Status401Unauthorized => (
                    "unauthenticated",
                    "Für diese Adresse ist eine Anmeldung nötig."),
                StatusCodes.Status403Forbidden => (
                    "forbidden",
                    "Für diese Adresse reicht die Berechtigung nicht."),
                StatusCodes.Status405MethodNotAllowed => (
                    "method-not-allowed",
                    "Diese Adresse gibt es, aber nicht mit dieser Methode."),
                _ => (
                    "error",
                    "Die Anfrage konnte nicht beantwortet werden."),
            };

            if (WantsJson(context.HttpContext))
            {
                response.ContentType = MediaTypeNames.Application.Json;
                await response.WriteAsync(
                    JsonSerializer.Serialize(new { error = new { code, message } }));
                return;
            }

            response.ContentType = "text/html; charset=utf-8";
            await response.WriteAsync(HtmlPage(status, message));
        });
    }

    /// <summary>
    /// Selbstenthaltend wie der Rest der Oberfläche: Stile inline, keine geladene Datei. Diese Seite
    /// erscheint gerade dann, wenn eine Adresse nicht stimmt — sie darf nicht ihrerseits von einer
    /// Adresse abhängen, die stimmen muss.
    /// <para>
    /// Der Rückweg ist der Punkt. Eine Fehlerseite ohne Weg zurück lässt den Besucher da stehen, wo
    /// er nicht sein wollte.
    /// </para>
    /// </summary>
    private static string HtmlPage(int status, string message) =>
        $$"""
        <!DOCTYPE html>
        <html lang="de">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{status}} — B.I.F.R.O.S.T</title>
          <style>
            :root { --void:#080d14; --text:#dbe9f7; --muted:#7c93ad; --ice:#6fd3ff; }
            html, body { height: 100%; margin: 0; background: var(--void); color: var(--text);
              font-family: system-ui, -apple-system, "Segoe UI", sans-serif; }
            main { min-height: 100%; display: grid; place-content: center; text-align: center;
              padding: 2rem; gap: .35rem; }
            .code { font-size: 3.4rem; font-weight: 700; letter-spacing: .04em; color: var(--ice);
              line-height: 1; }
            .brand { font-size: .62rem; letter-spacing: .19em; text-transform: uppercase;
              color: var(--muted); margin-bottom: 1.4rem;
              font-family: ui-monospace, "Cascadia Code", Consolas, monospace; }
            p { color: var(--muted); margin: .2rem 0 1.6rem; }
            a { color: var(--ice); text-decoration: none; }
            a:hover { text-decoration: underline; text-underline-offset: 3px; }
          </style>
        </head>
        <body>
          <main>
            <div class="brand">B.I.F.R.O.S.T &middot; MCP Gateway</div>
            <div class="code">{{status}}</div>
            <p>{{message}}</p>
            <a href="/">Zur Übersicht</a>
          </main>
        </body>
        </html>
        """;
}
