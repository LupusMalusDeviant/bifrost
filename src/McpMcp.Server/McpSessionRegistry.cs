using System.Collections.Concurrent;
using McpMcp.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMcp.Server;

/// <summary>
/// Verzeichnis der aktiven MCP-Sessions (FR-39). Quelle für tools/list_changed-Broadcasts (FR-07)
/// und für die Session-Anzeige im Dashboard (FR-33).
/// <para>
/// <b>Zwei Betriebsarten seit der Spec-Revision 2026-07-28.</b> Im <em>stateful</em> Betrieb ist
/// alles wie bisher: Eine Session lebt über viele Anfragen, der <c>McpServer</c> ist ihr Schlüssel,
/// und der Gateway kann von sich aus etwas hinschicken. Im <em>stateless</em> Betrieb — dem
/// Normalfall der neuen Revision — gibt es keine Session mehr: Jede Anfrage bekommt eine eigene
/// <c>McpServer</c>-Instanz, es gibt keine <c>Mcp-Session-Id</c>, und unaufgeforderte
/// Server-zu-Client-Nachrichten sind unmöglich.
/// </para>
/// <para>
/// Deshalb zählt diese Klasse im stateless Betrieb etwas anderes und sagt es auch: nicht offene
/// Sessions, sondern <b>wer im letzten Zeitfenster da war</b>. Die Alternative wäre gewesen, die
/// Session-Kacheln weiter zu füllen — mit einer Zahl, die dann die gerade laufenden HTTP-Anfragen
/// meint und zwischen zwei Aufrufen auf null fällt. Eine Kachel, die zwischen zwei Klicks von 3 auf
/// 0 springt, ist schlimmer als keine.
/// </para>
/// </summary>
public sealed class McpSessionRegistry : IActiveSessionSource
{
    /// <summary>
    /// Wie lange eine Identität nach ihrer letzten Anfrage als „da" gilt (stateless Betrieb).
    /// Fünf Minuten, weil ein Agent zwischen zwei Werkzeugaufrufen durchaus mal nachdenkt oder auf
    /// einen Menschen wartet — kürzer, und die Anzeige flackert bei normaler Nutzung.
    /// </summary>
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<McpServer, IdentityId> _sessions = new();
    private readonly ConcurrentDictionary<McpServer, byte> _capabilitiesLogged = new();

    /// <summary>Letzte Anfrage je Identität — die Grundlage der Zählung im stateless Betrieb.</summary>
    private readonly ConcurrentDictionary<IdentityId, DateTimeOffset> _lastSeen = new();

    private readonly TimeProvider _time;
    private readonly bool _stateless;

    public McpSessionRegistry(TimeProvider? time = null, bool stateless = false)
    {
        _time = time ?? TimeProvider.System;
        _stateless = stateless;
    }

    /// <summary>Ob dieser Gateway ohne Sessions arbeitet (Spec-Revision 2026-07-28).</summary>
    public bool Stateless => _stateless;

    public bool CountsOpenSessions => !_stateless;

    public int Count => ActiveSessions;

    public int ActiveSessions => _stateless ? CountRecent() : _sessions.Count;

    public int ActiveAgents => _stateless ? CountRecent() : _sessions.Values.Distinct().Count();

    /// <summary>
    /// Meldet eine Session an. Im stateless Betrieb ruft das SDK den Session-Handler <b>je
    /// Anfrage</b> auf — dann ist das hier kein Sitzungsbeginn, sondern ein Lebenszeichen der
    /// Identität.
    /// </summary>
    public void Register(McpServer server, IdentityId identity)
    {
        _sessions[server] = identity;
        _lastSeen[identity] = _time.GetUtcNow();
    }

    public void Unregister(McpServer server)
    {
        _sessions.TryRemove(server, out _);
        _capabilitiesLogged.TryRemove(server, out _);
    }

    /// <summary>
    /// Erster Aufruf je Session <c>true</c> — danach <c>false</c>. Damit laesst sich die
    /// Faehigkeiten-Zeile genau einmal schreiben.
    /// <para>
    /// Sie gehoert NICHT in den Session-Aufbau: Dort laeuft der Initialize-Handshake noch, und
    /// <c>ClientCapabilities</c> ist null. Genau daran ist der erste Versuch gescheitert — er meldete
    /// fuer jeden Client "kann nichts", was eine falsche Aussage aus einer zu fruehen Messung war.
    /// </para>
    /// <para>
    /// <b>Im stateless Betrieb zaehlt die Identitaet, nicht die Instanz.</b> Dort ist jede Anfrage
    /// eine eigene <c>McpServer</c>-Instanz — die Zeile stuende sonst bei JEDEM Werkzeugaufruf im
    /// Log. Einmal je Identitaet ist die Aussage, die gemeint war.
    /// </para>
    /// </summary>
    public bool ShouldLogCapabilities(McpServer server, IdentityId identity)
        => _stateless
            ? _capabilitiesLoggedPerIdentity.TryAdd(identity, 0)
            : _capabilitiesLogged.TryAdd(server, 0);

    private readonly ConcurrentDictionary<IdentityId, byte> _capabilitiesLoggedPerIdentity = new();

    /// <summary>
    /// Schickt <c>tools/list_changed</c> an alle offenen Sessions (FR-07).
    /// <para>
    /// <b>Im stateless Betrieb passiert hier nichts</b>, und das ist keine Nachlässigkeit: Die
    /// Revision 2026-07-28 kennt keine unaufgeforderten Server-zu-Client-Nachrichten mehr, weil die
    /// Antwort auf einer beliebigen anderen Instanz landen könnte. Den Weg dahin übernimmt der
    /// Cache-Hinweis auf <c>tools/list</c> (<c>ttlMs</c>): Der Client holt sich den Stand nach
    /// Ablauf der Frist selbst. Siehe <see cref="GatewayMcpHandlers"/>.
    /// </para>
    /// </summary>
    public async Task NotifyToolListChangedAsync(CancellationToken ct)
    {
        if (_stateless)
        {
            return;
        }

        foreach (var server in _sessions.Keys)
        {
            try
            {
                await server.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Session ist gerade im Abbau — der nächste tools/list-Aufruf holt den Stand ohnehin frisch.
            }
        }
    }

    /// <summary>
    /// Wer im Zeitfenster da war. Aufgeräumt wird beim Lesen: Die Tabelle ist so groß wie die Zahl
    /// der Identitäten, nicht der Anfragen — dafür lohnt kein eigener Hintergrundjob.
    /// </summary>
    private int CountRecent()
    {
        var cutoff = _time.GetUtcNow() - RecentWindow;
        var count = 0;
        foreach (var (identity, seen) in _lastSeen)
        {
            if (seen >= cutoff)
            {
                count++;
            }
            else
            {
                _lastSeen.TryRemove(new KeyValuePair<IdentityId, DateTimeOffset>(identity, seen));
                _capabilitiesLoggedPerIdentity.TryRemove(identity, out _);
            }
        }

        return count;
    }
}
