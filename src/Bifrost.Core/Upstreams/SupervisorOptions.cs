using Bifrost.Abstractions;

namespace Bifrost.Core.Upstreams;

/// <summary>Betriebsparameter des Supervisors. Defaults sind produktionsnah; Tests setzen kurze Intervalle.</summary>
public sealed record SupervisorOptions
{
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Nach so langer ununterbrochener Healthy-Zeit wird der Restart-Zähler zurückgesetzt.</summary>
    public TimeSpan HealthyResetWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeout pro Upstream-Call, wenn die Server-Config keinen eigenen setzt (FR-09).</summary>
    public TimeSpan DefaultCallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Drain-Gnadenfrist für Reconfigure/Disable, wenn keine explizite DrainPolicy übergeben wird.</summary>
    public TimeSpan DefaultDrainGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// So viele aufeinanderfolgende Health-Ping-Fehler gelten als <see cref="UpstreamState.Degraded"/>,
    /// bevor der Server auf <see cref="UpstreamState.Failed"/> geht und neu gestartet wird (FR-08).
    /// Ein einzelner verlorener Ping ist meist eine Netzdelle — Verbindung und In-Flight-Calls
    /// deswegen sofort wegzuwerfen wäre teurer als eine Runde abzuwarten.
    /// </summary>
    public int DegradedPingTolerance { get; init; } = 1;

    /// <summary>
    /// Wie oft der Katalog eines Upstreams neu abgefragt wird, der seine Änderungen <b>nicht</b>
    /// von selbst meldet (<see cref="IUpstreamConnection.PushesCatalogChanges"/>).
    /// <para>
    /// Das betrifft jeden Server auf der Spec-Revision 2026-07-28: Sie hat unaufgeforderte
    /// Server-zu-Client-Nachrichten gestrichen, also gibt es kein <c>tools/list_changed</c> mehr.
    /// Ohne diese Abfrage bliebe ein dort neu hinzugekommenes Werkzeug unsichtbar, bis jemand in
    /// der Oberfläche „Neu einlesen" drückt — ein stiller Ausfall, den niemand meldet.
    /// </para>
    /// <para>
    /// Eine Minute ist der Kompromiss: Werkzeuglisten ändern sich selten, aber ein Deployment soll
    /// nicht eine Viertelstunde brauchen, bis es ankommt. Upstreams, die weiter pushen, sind davon
    /// nicht betroffen — dort passiert unverändert nichts.
    /// </para>
    /// </summary>
    public TimeSpan CatalogPollInterval { get; init; } = TimeSpan.FromMinutes(1);

    public RestartPolicy DefaultRestartPolicy { get; init; } = RestartPolicy.Default;

    /// <summary>
    /// Frist bis zum nächsten Versuch, nachdem die Gegenstelle die Anmeldung abgelehnt hat
    /// (<see cref="UpstreamFailureKind.Authentication"/>). Fest statt wachsend: Ein falscher
    /// Schlüssel wird durch Warten nicht richtig, und jeder Versuch steht beim Upstream als
    /// abgelehnte Anmeldung im Log.
    /// </summary>
    public TimeSpan AuthenticationRetryInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Wie lange ein Verbindungsversuch samt Katalogabfrage dauern darf. Die Frist steht hier und
    /// nicht nur im Transport: Mit unbegrenzten Wiederholungen ist ein Versuch, der hängt, derselbe
    /// stille Stillstand wie ein Supervisor, der aufgegeben hat.
    /// </summary>
    public TimeSpan ConnectAttemptTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Wie lange der Neuaufbau einer verlorenen Sitzung dauern darf. Kürzer als ein gewöhnlicher
    /// Versuch, weil darauf ein Aufrufer wartet.
    /// </summary>
    public TimeSpan SessionRebuildTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Anteil, um den eine Frist höchstens <b>verkürzt</b> wird. Nur nach unten: Der Deckel bleibt
    /// Deckel, und zwei Gateways, die denselben Upstream verloren haben, treffen ihn trotzdem nicht
    /// im Gleichschritt.
    /// </summary>
    public double ReconnectJitter { get; init; } = 0.2;

    /// <summary>
    /// Zufallsquelle des Jitters, Werte in [0, 1). Eine Eigenschaft und kein fester Aufruf, damit
    /// Tests die Fristen auf die Millisekunde prüfen können.
    /// </summary>
    public Func<double> JitterSource { get; init; } = static () => Random.Shared.NextDouble();
}
