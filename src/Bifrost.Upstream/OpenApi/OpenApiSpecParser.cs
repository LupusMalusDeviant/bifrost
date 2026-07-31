using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bifrost.Upstream.OpenApi;

public enum OpenApiParameterLocation
{
    Path = 0,
    Query = 1,
    Header = 2,
}

public sealed record OpenApiParameterSpec(string Name, OpenApiParameterLocation Location, bool Required);

/// <summary>Eine importierbare Operation: wird 1:1 zu einem Tool (FR-19).</summary>
public sealed record OpenApiOperationSpec(
    string OperationId,
    string HttpMethod,
    string PathTemplate,
    IReadOnlyList<OpenApiParameterSpec> Parameters,
    bool HasBody,
    JsonElement InputSchema,
    string Description);

/// <summary>Von einem Import nicht unterstützte Spec-Features — führt IMMER zum Komplett-Abbruch (DON'T Nr. 6: kein Halbimport).</summary>
public sealed class OpenApiImportException : Exception
{
    public OpenApiImportException()
    {
    }

    public OpenApiImportException(string message)
        : base(message)
    {
    }

    public OpenApiImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Bewusst begrenzter OpenAPI-3.x-Parser (Plan-Änderungslog WP5): unterstützt JSON-Specs,
/// path/query/header-Parameter, application/json-Bodies und lokale <c>$ref</c>-Auflösung.
/// Alles außerhalb des Subsets bricht mit präziser Meldung ab — die Schemas bleiben dabei
/// Roh-JSON und wandern unverändert in <c>ToolDescriptor.InputSchema</c>.
/// </summary>
public static class OpenApiSpecParser
{
    private const int MaxRefDepth = 32;
    private static readonly string[] Methods = ["get", "put", "post", "delete", "patch", "head"];

    public static (IReadOnlyList<OpenApiOperationSpec> Operations, Uri? ServerUrl) Parse(string specJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(specJson);
        }
        catch (JsonException ex)
        {
            throw new OpenApiImportException(
                "Spec ist kein gültiges JSON. Hinweis: YAML-Specs werden in v1 nicht unterstützt — bitte als JSON exportieren.",
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new OpenApiImportException("Spec-Wurzel muss ein JSON-Objekt sein.");
            }

            if (root.TryGetProperty("swagger", out _))
            {
                throw new OpenApiImportException("Swagger 2.0 wird nicht unterstützt — bitte nach OpenAPI 3.x konvertieren.");
            }

            if (!root.TryGetProperty("openapi", out var versionProp)
                || versionProp.GetString() is not { } version
                || !version.StartsWith("3.", StringComparison.Ordinal))
            {
                throw new OpenApiImportException(
                    $"Nur OpenAPI 3.x wird unterstützt (gefunden: '{(root.TryGetProperty("openapi", out var v) ? v.ToString() : "kein openapi-Feld")}').");
            }

            Uri? serverUrl = null;
            if (root.TryGetProperty("servers", out var servers)
                && servers.ValueKind is JsonValueKind.Array
                && servers.GetArrayLength() > 0
                && servers[0].TryGetProperty("url", out var urlProp)
                && Uri.TryCreate(urlProp.GetString(), UriKind.Absolute, out var parsed))
            {
                serverUrl = parsed;
            }

            if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind is not JsonValueKind.Object)
            {
                throw new OpenApiImportException("Spec enthält kein 'paths'-Objekt.");
            }

            var operations = new List<OpenApiOperationSpec>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths.EnumerateObject())
            {
                var pathLevelParameters = path.Value.TryGetProperty("parameters", out var shared)
                    ? shared
                    : default;

                foreach (var method in Methods)
                {
                    if (!path.Value.TryGetProperty(method, out var operation))
                    {
                        continue;
                    }

                    var spec = ParseOperation(root, method, path.Name, operation, pathLevelParameters);
                    if (!seenIds.Add(spec.OperationId))
                    {
                        throw new OpenApiImportException($"operationId '{spec.OperationId}' ist mehrfach vergeben.");
                    }

                    operations.Add(spec);
                }
            }

            if (operations.Count == 0)
            {
                throw new OpenApiImportException("Spec enthält keine importierbaren Operationen.");
            }

            return (operations, serverUrl);
        }
    }

    private static OpenApiOperationSpec ParseOperation(
        JsonElement root, string method, string path, JsonElement operation, JsonElement sharedParameters)
    {
        var context = $"{method.ToUpperInvariant()} {path}";
        if (!operation.TryGetProperty("operationId", out var idProp)
            || idProp.GetString() is not { Length: > 0 } operationId)
        {
            throw new OpenApiImportException(
                $"Operation {context} hat keine operationId — ohne sie gibt es keinen stabilen Tool-Namen.");
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        var parameters = new List<OpenApiParameterSpec>();

        foreach (var parameterElement in EnumerateParameters(sharedParameters).Concat(
                     operation.TryGetProperty("parameters", out var own) ? EnumerateParameters(own) : []))
        {
            var resolved = ResolveRefs(parameterElement, root, 0, context);
            var name = resolved["name"]?.GetValue<string>()
                ?? throw new OpenApiImportException($"{context}: Parameter ohne 'name'.");
            var location = resolved["in"]?.GetValue<string>() switch
            {
                "path" => OpenApiParameterLocation.Path,
                "query" => OpenApiParameterLocation.Query,
                "header" => OpenApiParameterLocation.Header,
                var other => throw new OpenApiImportException(
                    $"{context}: Parameter-Ort '{other}' wird nicht unterstützt (nur path/query/header)."),
            };
            var isRequired = location is OpenApiParameterLocation.Path
                || (resolved["required"]?.GetValue<bool>() ?? false);

            if (name is "body")
            {
                throw new OpenApiImportException(
                    $"{context}: Parametername 'body' kollidiert mit dem Body-Feld des Tool-Schemas.");
            }

            parameters.Add(new OpenApiParameterSpec(name, location, isRequired));
            properties[name] = resolved["schema"] is { } schema
                ? schema.DeepClone()
                : new JsonObject { ["type"] = "string" };
            if (isRequired)
            {
                required.Add(name);
            }
        }

        var hasBody = false;
        if (operation.TryGetProperty("requestBody", out var requestBody))
        {
            hasBody = true;
            var resolvedBody = ResolveRefs(requestBody, root, 0, context);
            var content = resolvedBody["content"] as JsonObject
                ?? throw new OpenApiImportException($"{context}: requestBody ohne 'content'.");
            if (content["application/json"] is not JsonObject jsonContent)
            {
                throw new OpenApiImportException(
                    $"{context}: nur application/json-Bodies werden unterstützt (gefunden: {string.Join(", ", content.Select(c => c.Key))}).");
            }

            properties["body"] = jsonContent["schema"]?.DeepClone() ?? new JsonObject { ["type"] = "object" };
            if (resolvedBody["required"]?.GetValue<bool>() ?? false)
            {
                required.Add("body");
            }
        }

        var inputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            inputSchema["required"] = required;
        }

        var description = operation.TryGetProperty("summary", out var summary) && summary.GetString() is { Length: > 0 } s
            ? s
            : operation.TryGetProperty("description", out var desc) && desc.GetString() is { Length: > 0 } d
                ? d
                : $"{context} ({operationId})";

        return new OpenApiOperationSpec(
            operationId,
            method.ToUpperInvariant(),
            path,
            parameters,
            hasBody,
            JsonSerializer.SerializeToElement(inputSchema),
            description);
    }

    private static List<JsonElement> EnumerateParameters(JsonElement parameters)
    {
        var result = new List<JsonElement>();
        if (parameters.ValueKind is JsonValueKind.Array)
        {
            result.AddRange(parameters.EnumerateArray());
        }

        return result;
    }

    /// <summary>Löst lokale $ref-Verweise (#/...) rekursiv auf; externe Refs und Zyklen brechen ab.</summary>
    private static JsonNode ResolveRefs(JsonElement element, JsonElement root, int depth, string context)
    {
        if (depth > MaxRefDepth)
        {
            throw new OpenApiImportException($"{context}: $ref-Kette zu tief oder zyklisch (> {MaxRefDepth}).");
        }

        if (element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty("$ref", out var refProp))
        {
            var pointer = refProp.GetString() ?? string.Empty;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal))
            {
                throw new OpenApiImportException(
                    $"{context}: externer $ref '{pointer}' wird nicht unterstützt (nur dokument-lokale #/-Verweise).");
            }

            var target = root;
            foreach (var segment in pointer[2..].Split('/'))
            {
                var unescaped = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (target.ValueKind is not JsonValueKind.Object || !target.TryGetProperty(unescaped, out target))
                {
                    throw new OpenApiImportException($"{context}: $ref '{pointer}' zeigt ins Leere.");
                }
            }

            return ResolveRefs(target, root, depth + 1, context);
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    result[property.Name] = ResolveRefs(property.Value, root, depth + 1, context);
                }

                return result;
            }

            case JsonValueKind.Array:
            {
                var result = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    result.Add(ResolveRefs(item, root, depth + 1, context));
                }

                return result;
            }

            default:
                return JsonValue.Create(element.Clone())!;
        }
    }
}
