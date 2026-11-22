namespace Faolan.Core.Upstreams;

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
    /// Steht das Werkzeug im <b>geprüften</b> Katalog von <paramref name="connection"/>? Ein
    /// Werkzeug, das nach dem Neuaufbau fehlt oder dessen geänderte Definition zurückgehalten wird,
    /// darf nicht wiederholt werden.
    /// <para>
    /// Geprüft wird gegen den Katalog der Verbindung, die den Aufruf ausführt — nicht gegen den
    /// gerade veröffentlichten (Plan 0005 E14). Ist <paramref name="connection"/> nicht mehr die
    /// aktuelle Verbindung, lautet die Antwort <see cref="ToolAvailability.Stale"/>.
    /// </para>
    /// </summary>
    ToolAvailability CheckTool(GuardedUpstreamConnection connection, string toolName);
}

/// <summary>Ergebnis von <see cref="IUpstreamSessionRecovery.CheckTool"/>.</summary>
public enum ToolAvailability
{
    /// <summary>Das Werkzeug steht im geprüften Katalog dieser Verbindung.</summary>
    Available,

    /// <summary>Das Werkzeug fehlt oder wird zurückgehalten.</summary>
    Withheld,

    /// <summary>Die Verbindung ist nicht mehr die aktuelle; ihr Katalog gilt nicht mehr.</summary>
    Stale,
}
