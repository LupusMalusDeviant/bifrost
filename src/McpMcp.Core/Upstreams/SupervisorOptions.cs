using McpMcp.Abstractions;

namespace McpMcp.Core.Upstreams;

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
}
