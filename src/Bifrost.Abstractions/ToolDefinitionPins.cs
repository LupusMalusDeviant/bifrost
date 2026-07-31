using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bifrost.Abstractions;

/// <summary>
/// Der festgehaltene Stand einer Tool-Definition (Name, Beschreibung, Eingabeschema) eines
/// Upstreams.
/// <para>
/// Zweck ist die Erkennung von <b>Rug Pulls</b>: Ein Upstream, dem einmal vertraut wurde, ändert
/// später still die Beschreibung eines Tools und schmuggelt darüber Anweisungen in den Kontext des
/// Modells. Kein MCP-Standard verlangt Integrität von Tool-Definitionen — das OWASP-Cheat-Sheet
/// nennt es ausdrücklich („pin tool definitions using cryptographic hashes and alert on any
/// changes"), und CVE-2025-54136 zeigt den Fall in freier Wildbahn.
/// </para>
/// </summary>
/// <param name="PendingHash">
/// Gesetzt, wenn eine <em>abweichende</em> Definition gesehen wurde. Solange sie ansteht, ist das
/// Tool aus dem Katalog genommen — nicht aufrufbar, nicht sichtbar.
/// </param>
public sealed record ToolDefinitionPin(
    ServerId Server,
    string Tool,
    string AcceptedHash,
    DateTimeOffset AcceptedAt,
    string? PendingHash = null,
    DateTimeOffset? PendingSince = null)
{
    public bool HasPendingChange => PendingHash is { Length: > 0 };
}

/// <summary>Was die Prüfung einer Definition ergeben hat.</summary>
public enum ToolDefinitionVerdict
{
    /// <summary>
    /// Erstmals gesehen und damit übernommen (Trust-on-first-use). Das schützt gegen Änderungen
    /// <b>nach</b> der Aufnahme, <b>nicht</b> gegen einen von Anfang an bösartigen Upstream.
    /// </summary>
    FirstSeen = 0,

    /// <summary>Unverändert gegenüber dem angenommenen Stand.</summary>
    Unchanged = 1,

    /// <summary>Abweichend. Das Tool wird zurückgehalten, bis jemand die neue Fassung annimmt.</summary>
    Changed = 2,
}

/// <summary>
/// Verwaltung der festgehaltenen Tool-Definitionen. Schreibt selten (nur bei Erstsichtung,
/// Abweichung und Annahme), gelesen wird bei jeder Discovery.
/// </summary>
public interface IToolDefinitionPinStore
{
    IReadOnlyList<ToolDefinitionPin> All { get; }

    /// <summary>Feuert, wenn eine Fassung angenommen oder ein Pin entfernt wurde.</summary>
    event EventHandler<ToolDefinitionPinChangedEventArgs>? Changed;

    Task LoadAsync(CancellationToken ct);

    /// <summary>
    /// Prüft eine gesehene Definition gegen den festgehaltenen Stand und schreibt das Ergebnis
    /// fort: Erstsichtung wird angenommen, eine Abweichung als anstehend vermerkt.
    /// </summary>
    Task<ToolDefinitionVerdict> VerifyAsync(ServerId server, string tool, string hash, CancellationToken ct);

    /// <summary>
    /// Nimmt die anstehende Fassung an. Ausdrücklicher Schritt eines Administrators — genau hier
    /// liegt die Entscheidung, die ein Rug Pull umgehen möchte.
    /// </summary>
    Task AcceptAsync(ServerId server, string tool, CancellationToken ct);

    /// <summary>Entfernt alle Pins eines Upstreams (wenn er selbst entfernt wird).</summary>
    Task ForgetServerAsync(ServerId server, CancellationToken ct);
}

public sealed class ToolDefinitionPinChangedEventArgs : EventArgs
{
    public ToolDefinitionPinChangedEventArgs(ServerId server) => Server = server;

    public ServerId Server { get; }
}

/// <summary>
/// Bildet den Fingerabdruck einer Tool-Definition.
/// <para>
/// Das Schema wird <b>kanonisiert</b> (Objekteigenschaften sortiert, Formatierung entfernt), bevor
/// gehasht wird. Ohne das erzeugte jede Formatierungslaune eines Upstreams einen Fehlalarm — und
/// ein Alarm, der ständig grundlos anschlägt, wird abgeschaltet.
/// </para>
/// </summary>
public static class ToolDefinitionHash
{
    public static string Compute(ToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var material = new StringBuilder()
            .Append(tool.Name).Append('\n')
            // Die Beschreibung ist der eigentliche Angriffsweg: Sie landet unverändert im Kontext
            // des Modells, während das Schema nur die Argumente formt.
            .Append(tool.Description ?? string.Empty).Append('\n')
            .Append(Canonicalize(tool.InputSchema))
            .ToString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Stabile Textform eines JSON-Werts: Objekteigenschaften sortiert, Arrays in Reihenfolge.</summary>
    public static string Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(element, builder);
        return builder.ToString();
    }

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Write(property.Value, builder);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                // Reihenfolge bleibt: Bei `required` oder `enum` ist sie Teil der Bedeutung.
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    Write(item, builder);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Undefined:
                builder.Append("null");
                break;
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }
}
