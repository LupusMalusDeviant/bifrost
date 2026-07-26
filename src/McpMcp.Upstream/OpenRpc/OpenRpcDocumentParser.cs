using System.Text.Json;
using System.Text.Json.Nodes;
using McpMcp.Abstractions;

namespace McpMcp.Upstream.OpenRpc;

/// <summary>Wie die Parameter einer Methode über die Leitung gehen (OpenRPC <c>paramStructure</c>).</summary>
internal enum ParamStructure
{
    /// <summary>JSON-Objekt mit benannten Feldern.</summary>
    ByName = 0,

    /// <summary>Geordnetes Array — die Reihenfolge der Descriptors ist der Vertrag.</summary>
    ByPosition = 1,
}

/// <summary>Eine importierte Methode.</summary>
internal sealed record OpenRpcMethod(
    string Name,
    string? Description,
    ParamStructure ParamStructure,
    IReadOnlyList<string> ParameterOrder,
    JsonElement InputSchema);

/// <summary>
/// Liest ein OpenRPC-Dokument in aufrufbare Methoden (Spike `docs/spikes/openrpc-import.md`).
/// <para>
/// <b>Externe Referenzen werden nicht aufgelöst</b>, sondern abgewiesen. Ein <c>$ref</c> auf eine
/// URL wäre ein zweiter, ungeprüfter Ladevorgang mitten in der Schemaverarbeitung — und damit ein
/// Weg an der Zielprüfung vorbei, die beim Dokument selbst greift. Lokale Referenzen werden mit
/// Tiefenbegrenzung und Zyklenerkennung aufgelöst.
/// </para>
/// </summary>
internal static class OpenRpcDocumentParser
{
    /// <summary>Tiefenlimit für lokale Referenzen — fängt Ketten, die kein Zyklus sind, aber ausufern.</summary>
    private const int MaxRefDepth = 32;

    public static IReadOnlyList<OpenRpcMethod> Parse(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(document);
        }
        catch (JsonException exception)
        {
            throw new OpenRpcImportException($"Dokument ist kein gültiges JSON: {exception.Message}");
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new OpenRpcImportException("Dokument ist kein JSON-Objekt.");
            }

            if (!root.TryGetProperty("methods", out var methods)
                || methods.ValueKind is not JsonValueKind.Array)
            {
                throw new OpenRpcImportException("Dokument enthält keine 'methods'-Liste.");
            }

            var result = new List<OpenRpcMethod>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in methods.EnumerateArray())
            {
                var imported = ParseMethod(method, root);
                // Doppelte Namen sind nicht auflösbar: Beim Aufruf wäre nicht bestimmbar, welche
                // Signatur gilt. Eine der beiden still zu verwerfen wäre schlimmer — der Katalog
                // zeigte dann eine Methode, die anders aufgerufen wird, als er beschreibt.
                if (!seen.Add(imported.Name))
                {
                    throw new OpenRpcImportException(
                        $"Methode '{imported.Name}' ist mehrfach beschrieben — die Zuordnung wäre nicht eindeutig.");
                }

                result.Add(imported);
            }

            if (result.Count == 0)
            {
                throw new OpenRpcImportException("Dokument beschreibt keine Methoden.");
            }

            return result;
        }
    }

    private static OpenRpcMethod ParseMethod(JsonElement method, JsonElement root)
    {
        if (method.ValueKind is not JsonValueKind.Object
            || !method.TryGetProperty("name", out var nameElement)
            || nameElement.GetString() is not { Length: > 0 } name)
        {
            throw new OpenRpcImportException("Methode ohne 'name'.");
        }

        var structure = method.TryGetProperty("paramStructure", out var structureElement)
            && structureElement.GetString() is "by-position"
                ? ParamStructure.ByPosition
                : ParamStructure.ByName;

        var order = new List<string>();
        var properties = new JsonObject();
        var required = new JsonArray();
        if (method.TryGetProperty("params", out var parameters)
            && parameters.ValueKind is JsonValueKind.Array)
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                var resolved = ResolveRefs(parameter, root, 0, $"params von '{name}'");
                var descriptor = resolved.AsObject();
                var parameterName = descriptor["name"]?.GetValue<string>()
                    ?? throw new OpenRpcImportException($"Parameter ohne 'name' in Methode '{name}'.");
                order.Add(parameterName);
                properties[parameterName] = descriptor["schema"]?.DeepClone()
                    // Ein Content Descriptor ohne Schema beschreibt keinen Typ. Statt zu raten
                    // bleibt das Feld offen — der Upstream validiert selbst.
                    ?? new JsonObject();
                if (descriptor["required"]?.GetValue<bool>() == true)
                {
                    required.Add(parameterName);
                }
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            // Strikt: Ein unbekanntes Feld fällt im Gateway auf und nicht erst im Upstream.
            ["additionalProperties"] = false,
        };

        return new OpenRpcMethod(
            name,
            method.TryGetProperty("summary", out var summary) ? summary.GetString()
                : method.TryGetProperty("description", out var description) ? description.GetString()
                : null,
            structure,
            order,
            JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString()));
    }

    /// <summary>
    /// Löst <c>$ref</c> auf — <b>nur lokal</b>, mit Tiefenlimit und Zyklenerkennung. Ein externer
    /// Verweis wird abgewiesen, nicht geladen.
    /// </summary>
    private static JsonNode ResolveRefs(
        JsonElement element, JsonElement root, int depth, string context,
        HashSet<string>? visiting = null)
    {
        if (depth > MaxRefDepth)
        {
            throw new OpenRpcImportException(
                $"Referenztiefe über {MaxRefDepth} in {context} — abgebrochen.");
        }

        if (element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty("$ref", out var reference))
        {
            var pointer = reference.GetString() ?? string.Empty;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal))
            {
                throw new OpenRpcImportException(
                    $"Externe Referenz '{pointer}' in {context} wird nicht aufgelöst. Ein Verweis nach "
                    + "außen wäre ein zweiter, ungeprüfter Ladevorgang mitten im Schema.");
            }

            visiting ??= new HashSet<string>(StringComparer.Ordinal);
            if (!visiting.Add(pointer))
            {
                throw new OpenRpcImportException(
                    $"Zyklische Referenz '{pointer}' in {context}.");
            }

            var target = Follow(pointer, root, context);
            var resolved = ResolveRefs(target, root, depth + 1, context, visiting);
            visiting.Remove(pointer);
            return resolved;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = ResolveRefs(property.Value, root, depth + 1, context, visiting);
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(ResolveRefs(item, root, depth + 1, context, visiting));
                }

                return array;
            default:
                return JsonNode.Parse(element.GetRawText())!;
        }
    }

    private static JsonElement Follow(string pointer, JsonElement root, string context)
    {
        var current = root;
        foreach (var segment in pointer[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var decoded = segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind is not JsonValueKind.Object
                || !current.TryGetProperty(decoded, out current))
            {
                throw new OpenRpcImportException($"Referenz '{pointer}' in {context} zeigt ins Leere.");
            }
        }

        return current;
    }

    /// <summary>Katalogtauglicher Tool-Deskriptor. Lesend, bis der Dienst etwas anderes sagt.</summary>
    public static ToolDescriptor ToToolDescriptor(OpenRpcMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return new ToolDescriptor(
            method.Name,
            method.Description,
            method.InputSchema,
            // OpenRPC kennt keine Seiteneffekt-Angabe. `Read` anzunehmen wäre eine Behauptung über
            // fremden Code — aber `Write` als Vorgabe machte jede Methode freigabeverdächtig. Der
            // Betreiber entscheidet über die Approval-Policy; hier bleibt es bei der neutralen
            // Vorgabe, und der Katalog nennt den Ursprung.
            CapabilityRisk.Read);
    }
}
