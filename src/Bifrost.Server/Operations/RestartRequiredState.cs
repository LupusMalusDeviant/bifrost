using System.Text.Json;

namespace Bifrost.Server.Operations;

/// <summary>
/// Der Riegel nach einem Restore (<c>BFR-F-0047</c>).
/// <para>
/// <b>Warum es ihn gibt.</b> <c>bifrost restore</c> tauscht Datenbank, Key-Ring und
/// Instanzkonfiguration auf der Platte. Der laufende Prozess merkt davon nichts: Er hält seine
/// Datenbankverbindung, seinen entschlüsselten Key-Ring und seine Zwischenspeicher weiter — und
/// entscheidet damit gegen den Zustand, den der Restore gerade ersetzt hat. Im Piloten vom
/// 2026-08-05 hieß das: Der API-Key aus dem Archiv bekam <c>401</c>, der Schlüssel des
/// überschriebenen Zustands weiter <c>200</c>. Der Befehl meldete Erfolg.
/// </para>
/// <para>
/// <b>Warum ein Riegel und nicht ein Neuaufbau im laufenden Betrieb.</b> „Der Prozess lädt alles
/// neu" wäre eine Zusage über den ganzen Anwendungszustand — jeden Zwischenspeicher, jede offene
/// Upstream-Verbindung, jede Sitzung. Eine solche Zusage lässt sich nicht prüfen, nur behaupten,
/// und sie fällt beim nächsten hinzugefügten Zwischenspeicher lautlos um. Der Riegel dagegen ist
/// eine Zusage über <em>eine</em> Sache und fail-closed: Bis zum Neustart antwortet der Gateway
/// niemandem mit Daten, für die er nicht mehr geradesteht.
/// </para>
/// <para>
/// <b>Warum das keine zweite Sperre neben BFR-DB-0101 ist.</b> Jene verhindert den <em>Start</em>
/// nach einer abgebrochenen Migration und lebt in einer Datei. Diese hier gilt genau für die
/// Lebenszeit dieses Prozesses und verschwindet mit ihm — der Neustart ist die Auflösung, nicht ein
/// weiterer Befehl. Ein Riegel, den man wegräumen muss, wäre eine zweite Gelegenheit, ihn
/// wegzuräumen statt neu zu starten.
/// </para>
/// </summary>
public sealed class RestartRequiredState
{
    private volatile string? _reason;

    public bool IsSet => _reason is not null;

    public string? Reason => _reason;

    /// <summary>
    /// Einmalig und ohne Rückweg innerhalb dieses Prozesses. Ein zweiter Aufruf lässt den ersten
    /// Grund stehen: Der erste ist der, der den Zustand verlassen hat.
    /// </summary>
    public void Require(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _reason ??= reason;
    }
}

/// <summary>
/// Setzt <see cref="RestartRequiredState"/> durch. Läuft <b>vor</b> der Authentifizierung — die
/// entscheidet gegen genau die Daten, für die der Prozess nicht mehr geradesteht.
/// </summary>
public sealed class RestartRequiredMiddleware
{
    private readonly RequestDelegate _next;

    public RestartRequiredMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, RestartRequiredState state)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);

        if (!state.IsSet)
        {
            await _next(context);
            return;
        }

        // 503 und nicht 500: Der Zustand ist bekannt, benannt und behebbar. Bewusst OHNE
        // 'Retry-After' — ein Wiederholen hilft nicht, es braucht einen Neustart. Ein Header, der
        // "gleich nochmal" sagt, wäre hier eine falsche Auskunft an jeden Client, der ihn liest.
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";

        // Auch die Health-Probes bekommen die 503. Sonst meldet der Container "healthy", während er
        // nichts mehr beantwortet — und genau diese Art Auseinanderfallen ist der Befund, der zu
        // dieser Datei geführt hat. Unter einem Orchestrator mit Liveness-Probe löst die 503 den
        // Neustart aus, der hier verlangt wird.
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = "restart-required",
            detail = state.Reason,
        }));
    }
}
