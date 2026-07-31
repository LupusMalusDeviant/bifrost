using System.Text.Json;

namespace Bifrost.Upstream;

internal static class JsonArguments
{
    /// <summary>
    /// Konvertiert das JsonElement-Argumentobjekt des Vertrags in das Dictionary-Format des SDK-Clients.
    /// Null/Undefined → null (Tool ohne Argumente); alles andere muss ein JSON-Objekt sein.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? ToDictionary(JsonElement args)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (args.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"Tool-Argumente müssen ein JSON-Objekt sein, war {args.ValueKind}.", nameof(args));
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind is JsonValueKind.Null ? null : property.Value;
        }

        return result;
    }
}
