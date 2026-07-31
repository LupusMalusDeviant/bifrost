using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bifrost.Abstractions;

namespace Bifrost.Upstream.Cli;

internal static partial class CliArgumentBinder
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._:/-]{0,255}$")]
    private static partial Regex SecretReferencePattern();

    public static IReadOnlyList<string> Bind(
        CliToolSpec tool, JsonElement arguments, CliTransportOptions options)
    {
        if (tool.AllowCallerArguments)
        {
            return ReadLegacyArguments(arguments);
        }

        var parameters = tool.Parameters ?? [];
        if (parameters.Count == 0)
        {
            EnsureNoUnknownArguments(arguments, new HashSet<string>(StringComparer.Ordinal));
            return [];
        }

        var knownNames = parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        EnsureNoUnknownArguments(arguments, knownNames);

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty(parameter.Name, out var supplied))
            {
                values[parameter.Name] = supplied;
            }
            else if (parameter.DefaultValue is { } defaultValue)
            {
                values[parameter.Name] = defaultValue;
            }
            else if (parameter.Required)
            {
                throw new ArgumentException($"CLI-Parameter '{parameter.Name}' ist erforderlich.");
            }
        }

        ValidateRelationships(parameters, values.Keys);

        var result = new List<string>();
        foreach (var parameter in parameters
                     .Where(parameter => parameter.Position is not null)
                     .OrderBy(parameter => parameter.Position))
        {
            if (values.TryGetValue(parameter.Name, out var value))
            {
                AppendParameter(result, parameter, value, options);
            }
        }

        foreach (var parameter in parameters.Where(parameter => parameter.Flag is not null))
        {
            if (values.TryGetValue(parameter.Name, out var value))
            {
                AppendParameter(result, parameter, value, options);
            }
        }

        return result;
    }

    public static JsonElement BuildSchema(CliToolSpec tool)
    {
        if (tool.AllowCallerArguments)
        {
            return JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    args = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "Legacy free-form arguments, appended literally without a shell.",
                    },
                },
                additionalProperties = false,
            });
        }

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var parameter in tool.Parameters ?? [])
        {
            properties[parameter.Name] = BuildParameterSchema(parameter);
            if (parameter.Required && parameter.DefaultValue is null)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return JsonSerializer.SerializeToElement(schema);
    }

    private static Dictionary<string, object?> BuildParameterSchema(CliParameterSpec parameter)
    {
        var scalar = new Dictionary<string, object?>();
        scalar["type"] = parameter.Type switch
        {
            CliParameterType.Integer => "integer",
            CliParameterType.Number => "number",
            CliParameterType.Boolean => "boolean",
            _ => "string",
        };
        if (parameter.Description is not null)
        {
            scalar["description"] = parameter.Description;
        }

        if (parameter.AllowedValues is { Count: > 0 })
        {
            scalar["enum"] = parameter.AllowedValues;
        }

        if (parameter.Pattern is not null)
        {
            scalar["pattern"] = parameter.Pattern;
        }

        if (parameter.Minimum is not null)
        {
            scalar["minimum"] = parameter.Minimum;
        }

        if (parameter.Maximum is not null)
        {
            scalar["maximum"] = parameter.Maximum;
        }

        if (parameter.MaxLength is not null)
        {
            scalar["maxLength"] = parameter.MaxLength;
        }

        if (parameter.DefaultValue is not null)
        {
            scalar["default"] = parameter.DefaultValue;
        }

        if (parameter.Sensitive || parameter.Type == CliParameterType.SecretReference)
        {
            scalar["writeOnly"] = true;
        }

        if (parameter.Type == CliParameterType.SecretReference)
        {
            scalar["pattern"] = SecretReferencePattern().ToString();
        }

        if (!parameter.Repeatable)
        {
            return scalar;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "array",
            ["items"] = scalar,
            ["description"] = parameter.Description,
        };
    }

    private static void AppendParameter(
        List<string> result,
        CliParameterSpec parameter,
        JsonElement value,
        CliTransportOptions options)
    {
        if (parameter.Repeatable)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException(
                    $"CLI-Parameter '{parameter.Name}' muss ein Array sein.");
            }

            foreach (var element in value.EnumerateArray())
            {
                AppendScalar(result, parameter, element, options);
            }
            return;
        }

        AppendScalar(result, parameter, value, options);
    }

    private static void AppendScalar(
        List<string> result,
        CliParameterSpec parameter,
        JsonElement value,
        CliTransportOptions options)
    {
        if (parameter.Type == CliParameterType.Boolean && parameter.Flag is not null)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new ArgumentException(
                    $"CLI-Parameter '{parameter.Name}' muss boolean sein.");
            }

            if (value.GetBoolean())
            {
                result.Add(parameter.Flag);
            }
            return;
        }

        var formatted = FormatScalar(parameter, value, options);
        if (parameter.Flag is not null)
        {
            result.Add(parameter.Flag);
        }
        result.Add(formatted);
    }

    private static string FormatScalar(
        CliParameterSpec parameter, JsonElement value, CliTransportOptions options)
    {
        string formatted;
        double? numeric = null;
        switch (parameter.Type)
        {
            case CliParameterType.String:
            case CliParameterType.Enum:
                formatted = RequireString(parameter, value);
                break;
            case CliParameterType.SecretReference:
                formatted = RequireString(parameter, value);
                if (!SecretReferencePattern().IsMatch(formatted))
                {
                    throw new ArgumentException(
                        $"CLI-Parameter '{parameter.Name}' ist keine gültige Secret-Referenz.");
                }
                break;
            case CliParameterType.Path:
                formatted = CliProcessPolicy.ResolveArgumentPath(
                    RequireString(parameter, value), parameter.PathAccess, options);
                break;
            case CliParameterType.Integer:
                if (!value.TryGetInt64(out var integer))
                {
                    throw new ArgumentException(
                        $"CLI-Parameter '{parameter.Name}' muss integer sein.");
                }
                numeric = integer;
                formatted = integer.ToString(CultureInfo.InvariantCulture);
                break;
            case CliParameterType.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    throw new ArgumentException(
                        $"CLI-Parameter '{parameter.Name}' muss eine endliche Zahl sein.");
                }
                numeric = number;
                formatted = number.ToString("R", CultureInfo.InvariantCulture);
                break;
            case CliParameterType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ArgumentException(
                        $"CLI-Parameter '{parameter.Name}' muss boolean sein.");
                }
                formatted = value.GetBoolean() ? "true" : "false";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(parameter));
        }

        if (parameter.MaxLength is { } maxLength && formatted.Length > maxLength)
        {
            throw new ArgumentException(
                $"CLI-Parameter '{parameter.Name}' überschreitet die Maximallänge {maxLength}.");
        }

        if (parameter.Pattern is { } pattern && !Regex.IsMatch(formatted, pattern))
        {
            throw new ArgumentException(
                $"CLI-Parameter '{parameter.Name}' entspricht nicht dem erlaubten Pattern.");
        }

        if (parameter.AllowedValues is { Count: > 0 }
            && !parameter.AllowedValues.Contains(formatted, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"CLI-Parameter '{parameter.Name}' enthält keinen erlaubten Wert.");
        }

        if (numeric is { } numericValue
            && (parameter.Minimum is { } minimum && numericValue < minimum
                || parameter.Maximum is { } maximum && numericValue > maximum))
        {
            throw new ArgumentException(
                $"CLI-Parameter '{parameter.Name}' liegt außerhalb des Wertebereichs.");
        }

        return formatted;
    }

    private static string RequireString(CliParameterSpec parameter, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"CLI-Parameter '{parameter.Name}' muss string sein.");
        }

        return value.GetString()!;
    }

    private static void ValidateRelationships(
        IReadOnlyList<CliParameterSpec> parameters, IEnumerable<string> suppliedNames)
    {
        var supplied = suppliedNames.ToHashSet(StringComparer.Ordinal);
        foreach (var parameter in parameters.Where(parameter => supplied.Contains(parameter.Name)))
        {
            var conflict = (parameter.ConflictsWith ?? [])
                .FirstOrDefault(supplied.Contains);
            if (conflict is not null)
            {
                throw new ArgumentException(
                    $"CLI-Parameter '{parameter.Name}' kollidiert mit '{conflict}'.");
            }

            var missing = (parameter.Requires ?? [])
                .FirstOrDefault(requirement => !supplied.Contains(requirement));
            if (missing is not null)
            {
                throw new ArgumentException(
                    $"CLI-Parameter '{parameter.Name}' erfordert '{missing}'.");
            }
        }
    }

    private static IReadOnlyList<string> ReadLegacyArguments(JsonElement arguments)
    {
        if (arguments.ValueKind is not JsonValueKind.Object
            || !arguments.TryGetProperty("args", out var array)
            || array.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return [.. array.EnumerateArray().Select(element =>
            element.ValueKind is JsonValueKind.String ? element.GetString()! : element.GetRawText())];
    }

    private static void EnsureNoUnknownArguments(
        JsonElement arguments, HashSet<string> knownNames)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined)
        {
            return;
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("CLI-Argumente müssen ein JSON-Objekt sein.");
        }

        var unknown = arguments.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => !knownNames.Contains(name));
        if (unknown is not null)
        {
            throw new ArgumentException($"Unbekannter CLI-Parameter '{unknown}'.");
        }
    }
}
