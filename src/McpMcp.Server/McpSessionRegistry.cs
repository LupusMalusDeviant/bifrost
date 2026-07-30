using System.Collections.Concurrent;
using McpMcp.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMcp.Server;

/// <summary>
/// Verzeichnis der aktiven MCP-Sessions (FR-39). Quelle für tools/list_changed-Broadcasts (FR-07)
/// und für die Session-Anzeige im Dashboard (FR-33).
/// </summary>
public sealed class McpSessionRegistry : IActiveSessionSource
{
    private readonly ConcurrentDictionary<McpServer, IdentityId> _sessions = new();

    public int Count => _sessions.Count;

    public int ActiveSessions => _sessions.Count;

    public int ActiveAgents => _sessions.Values.Distinct().Count();

    public void Register(McpServer server, IdentityId identity) => _sessions[server] = identity;

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
    /// </summary>
    public bool ShouldLogCapabilities(McpServer server) => _capabilitiesLogged.TryAdd(server, 0);

    private readonly ConcurrentDictionary<McpServer, byte> _capabilitiesLogged = new();

    public async Task NotifyToolListChangedAsync(CancellationToken ct)
    {
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
}
