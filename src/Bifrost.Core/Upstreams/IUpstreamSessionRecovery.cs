namespace Bifrost.Core.Upstreams;

/// <summary>
/// Der Weg, auf dem eine Verbindung ihre verlorene Sitzung neu aufbauen lässt (Vertrag
/// „Selbsttätige Wiederverbindung", Abschnitt 4).
/// <para>
/// <b>Warum das über den Supervisor geht und die Verbindung sich nicht selbst heilt:</b> Ein
/// Upstream, der seine Sitzungen vergessen hat, ist in aller Regel neu gestartet worden — und kann
/// danach andere Werkzeuge anbieten als vorher. Der Neuaufbau muss deshalb denselben Weg nehmen wie
/// der erste Anschluss: Katalog holen, jede Definition gegen den festgehaltenen Stand prüfen. Eine
/// Verbindung, die nur ihren Client austauscht, liefe an dieser Prüfung vorbei.
/// </para>
/// </summary>
public interface IUpstreamSessionRecovery
{
    /// <summary>
    /// Liefert die Verbindung, die <paramref name="lost"/> ersetzt. Viele gleichzeitige Aufrufer
    /// bekommen denselben Neuaufbau; ist er schon geschehen, kommt die neue Verbindung sofort.
    /// Scheitert er, wirft die Methode — ein zweiter Versuch für denselben Aufruf findet nicht statt.
    /// </summary>
    Task<GuardedUpstreamConnection> RebuildAsync(GuardedUpstreamConnection lost, CancellationToken ct);

    /// <summary>
    /// Steht das Werkzeug im <b>geprüften</b> Katalog der aktuellen Verbindung? Ein Werkzeug, das
    /// nach dem Neuaufbau fehlt oder dessen geänderte Definition zurückgehalten wird, darf nicht
    /// wiederholt werden.
    /// </summary>
    bool IsToolAvailable(string toolName);
}
