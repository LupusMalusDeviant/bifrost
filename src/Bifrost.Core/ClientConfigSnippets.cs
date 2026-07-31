using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bifrost.Core;

/// <summary>Ein fertiges Konfigurations-Snippet für einen Agenten-Client (FR-41).</summary>
public sealed record ClientConfigSnippet(string Client, string Language, string Content);

/// <summary>
/// Erzeugt einsatzfertige Client-Konfigurationen aus Endpunkt, Server-Name und API-Key (FR-41).
///
/// Zweck: die Lücke zwischen „Key erzeugt" und „Agent läuft" schließen. Wer den Key von Hand in
/// eine Config überträgt, vertippt sich am Header-Namen oder hängt <c>/mcp</c> an die falsche
/// Stelle — beides Fehler, die als „Gateway antwortet nicht" zurückkommen.
///
/// Reine Funktion ohne Zustand, damit sie ohne UI testbar ist (NFR-08).
/// </summary>
public static class ClientConfigSnippets
{
    /// <summary>
    /// <paramref name="baseAddress"/> ist die von außen erreichbare Adresse des Gateways
    /// (z.B. <c>https://gateway.example.com</c>); der MCP-Pfad wird angehängt.
    /// </summary>
    public static IReadOnlyList<ClientConfigSnippet> Build(Uri baseAddress, string serverName, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var endpoint = new Uri(baseAddress, "/mcp").ToString();
        var name = Sanitize(serverName);

        return
        [
            new ClientConfigSnippet(
                "Claude Code (CLI)",
                "bash",
                $"claude mcp add --transport http {name} {endpoint} \\\n"
                    + $"  --header \"Authorization: Bearer {apiKey}\""),

            new ClientConfigSnippet(
                "Claude Desktop / generischer MCP-Client",
                "json",
                BuildJson(name, endpoint, apiKey)),
        ];
    }

    private static string BuildJson(string name, string endpoint, string apiKey)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [name] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = endpoint,
                    ["headers"] = new JsonObject
                    {
                        ["Authorization"] = $"Bearer {apiKey}",
                    },
                },
            },
        };

        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Client-Konfigurationen nutzen den Namen als Schlüssel; Leerzeichen und Sonderzeichen
    /// führen dort je nach Client zu stillen Fehlern statt zu einer Fehlermeldung.
    /// </summary>
    private static string Sanitize(string name)
    {
        // Bewusst nur ASCII: Der Name landet als Schlüssel in fremden Config-Dateien, deren
        // Encoding-Verhalten wir nicht kennen. Umlaute wären JSON-seitig zulässig, aber ein
        // Client, der die Datei falsch liest, scheitert dann ohne brauchbare Fehlermeldung.
        var cleaned = new string([.. name.Trim().ToLowerInvariant()
            .Select(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' ? c : '-')]);

        cleaned = cleaned.Trim('-');
        return cleaned.Length == 0 ? "bifrost" : cleaned;
    }
}
