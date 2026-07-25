using System.Globalization;
using System.Text;
using System.Text.Json;

namespace McpMcp.Upstream.Wasi;

/// <summary>
/// Ein Tool des WASI-Hosts in Katalogform (Plan 0003, WP6.1). <see cref="Name"/> ist der
/// normalisierte Katalogname, <see cref="Export"/> der rohe Export-Name, den der Host beim
/// <c>invoke</c> erwartet — die Rückabbildung darf nicht verloren gehen.
/// </summary>
internal sealed record WasiTool(
    string Name,
    string Export,
    string Description,
    JsonElement InputSchema,
    IReadOnlyList<string> ParameterNames)
{
    /// <summary>
    /// Bindet die benannten Argumente des Aufrufers an die Positionsliste, die der IPC-Vertrag
    /// führt. Der Vertrag überträgt <c>args</c> als Reihenfolge — die Namen existieren nur im
    /// Schema, also muss die Übersetzung hier stattfinden und nicht beim Aufrufer.
    /// </summary>
    public bool TryBindArguments(JsonElement arguments, out JsonElement[] positional, out string error)
    {
        var bound = new JsonElement[ParameterNames.Count];
        for (var index = 0; index < ParameterNames.Count; index++)
        {
            var name = ParameterNames[index];
            // Geprüft wird hier nur Anwesenheit und Reihenfolge. Ob der Wert zum Typ passt,
            // entscheidet das Schema im Invoker und danach der Host — zwei Stellen mit derselben
            // Prüfung wären zwei Wahrheiten, die auseinanderlaufen können.
            if (arguments.ValueKind is not JsonValueKind.Object
                || !arguments.TryGetProperty(name, out var value))
            {
                positional = [];
                error = $"Argument '{name}' fehlt.";
                return false;
            }

            bound[index] = value;
        }

        positional = bound;
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Übersetzt die typisierte Discovery-Antwort des Hosts in Katalog-Tools (Plan 0003, WP6.1).
/// <para>
/// Zwei Dinge passieren hier, und beide sind Gateway-Politik statt Host-Wahrheit: Der Host meldet
/// Export-Namen so, wie das Component sie trägt (<c>wasi:cli/run@0.2.6</c>) — als Katalogname
/// taugen sie nicht, weil sie in REST-Pfaden und Tool-Namen Sonderzeichen einschleppen. Und er
/// meldet Parametertypen, aus denen hier ein echtes JSON-Schema pro Tool entsteht statt eines
/// Platzhalters für alle.
/// </para>
/// <para>
/// Exports, die der Host als nicht aufrufbar meldet, erscheinen nicht im Katalog — ein Tool, das
/// bei jedem Aufruf scheitert, ist schlimmer als ein fehlendes.
/// </para>
/// </summary>
internal static class WasiToolNormalizer
{
    public static IReadOnlyList<WasiTool> Normalize(JsonElement tools)
    {
        var normalized = new List<WasiTool>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in tools.EnumerateArray())
        {
            if (descriptor.ValueKind is not JsonValueKind.Object
                || descriptor.GetProperty("name").GetString() is not { Length: > 0 } export
                || !IsSupported(descriptor))
            {
                continue;
            }

            var parameters = ReadParameters(descriptor);
            normalized.Add(new WasiTool(
                Unique(CatalogName(export), taken),
                export,
                Describe(descriptor, export, parameters),
                Schema(parameters),
                [.. parameters.Select(parameter => parameter.Name)]));
        }

        return normalized;
    }

    /// <summary>
    /// Macht aus einem WIT-Export-Namen einen katalog- und URL-tauglichen Namen:
    /// <c>wasi:cli/run@0.2.6</c> → <c>wasi_cli_run</c>. Die Versionsangabe fällt weg — sie gehört
    /// zum Component, nicht zum Tool, und würde den Namen bei jedem Guest-Update ändern.
    /// </summary>
    internal static string CatalogName(string export)
    {
        var withoutVersion = StripVersionSuffix(export);
        var builder = new StringBuilder(withoutVersion.Length);
        foreach (var character in withoutVersion)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var name = builder.ToString().Trim('_');
        return name.Length > 0 ? name : "tool";
    }

    /// <summary>
    /// Entfernt WIT-Versionsanhänge (<c>@0.2.6</c>) — auch mitten im Namen. Ein Punkt zählt nur
    /// dann zur Version, wenn eine Ziffer folgt: sonst verschluckte <c>@0.2.6.run</c> den
    /// Funktionsteil.
    /// </summary>
    private static string StripVersionSuffix(string export)
    {
        var builder = new StringBuilder(export.Length);
        var index = 0;
        while (index < export.Length)
        {
            if (export[index] != '@')
            {
                builder.Append(export[index]);
                index++;
                continue;
            }

            index++;
            while (index < export.Length
                   && (char.IsAsciiDigit(export[index])
                       || (export[index] == '.' && index + 1 < export.Length && char.IsAsciiDigit(export[index + 1]))))
            {
                index++;
            }
        }

        return builder.ToString();
    }

    private static string Unique(string candidate, HashSet<string> taken)
    {
        if (taken.Add(candidate))
        {
            return candidate;
        }

        // Deterministisch statt zufällig: gleiche Discovery-Reihenfolge → gleiche Namen.
        for (var suffix = 2; ; suffix++)
        {
            var next = string.Create(CultureInfo.InvariantCulture, $"{candidate}_{suffix}");
            if (taken.Add(next))
            {
                return next;
            }
        }
    }

    private static bool IsSupported(JsonElement descriptor)
        => !descriptor.TryGetProperty("supported", out var supported)
            || supported.ValueKind is not JsonValueKind.False;

    private static List<WasiParameter> ReadParameters(JsonElement descriptor)
    {
        var parameters = new List<WasiParameter>();
        if (!descriptor.TryGetProperty("params", out var declared)
            || declared.ValueKind is not JsonValueKind.Array)
        {
            return parameters;
        }

        foreach (var parameter in declared.EnumerateArray())
        {
            if (parameter.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } named)
            {
                parameters.Add(new WasiParameter(
                    named,
                    parameter.TryGetProperty("type", out var declaredType) ? declaredType.Clone() : default));
            }
        }

        return parameters;
    }

    /// <summary>
    /// Das Schema pro Tool statt eines gemeinsamen Platzhalters. <c>additionalProperties: false</c>
    /// wie beim CLI-Connector: Der Lazy-Pfad validiert serverseitig gegen genau dieses Schema
    /// (ADR-0003), unbekannte Felder sollen dort auffallen und nicht beim Guest.
    /// </summary>
    private static JsonElement Schema(IReadOnlyList<WasiParameter> parameters)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            properties[parameter.Name] = SchemaForType(parameter.Type);
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = parameters.Select(parameter => parameter.Name).ToArray(),
            ["additionalProperties"] = false,
        });
    }

    /// <summary>
    /// Baut das Schema eines Component-Model-Typs aus dem Typbaum des Hosts. Der Baum ist der
    /// Grund, warum das überhaupt geht: Aus dem blossen Namen „list" liesse sich kein
    /// <c>items</c> ableiten, und <c>list&lt;u8&gt;</c> wäre nicht von <c>list&lt;string&gt;</c>
    /// zu unterscheiden.
    /// </summary>
    private static object SchemaForType(JsonElement type)
    {
        var kind = type.ValueKind is JsonValueKind.Object && type.TryGetProperty("kind", out var declared)
            ? declared.GetString()
            : null;

        return kind switch
        {
            "bool" => Described("boolean", "bool"),
            "s8" => Bounded(sbyte.MinValue, sbyte.MaxValue, "s8"),
            "u8" => Bounded(byte.MinValue, byte.MaxValue, "u8"),
            "s16" => Bounded(short.MinValue, short.MaxValue, "s16"),
            "u16" => Bounded(ushort.MinValue, ushort.MaxValue, "u16"),
            "s32" => Bounded(int.MinValue, int.MaxValue, "s32"),
            "u32" => Bounded(uint.MinValue, uint.MaxValue, "u32"),
            // 64 Bit als Dezimalstring: JSON-Zahlen sind Doubles und verlieren ab 2^53 Stellen.
            "s64" => Pattern("^-?[0-9]+$", "s64"),
            "u64" => Pattern("^[0-9]+$", "u64"),
            "f32" => Described("number", "f32"),
            "f64" => Described("number", "f64"),
            "char" => new { type = "string", minLength = 1, maxLength = 1, description = ComponentType("char") },
            "string" => Described("string", "string"),
            // list<u8> ist ein Blob, kein Zahlen-Array (ADR-0017).
            "binary" => new { type = "string", contentEncoding = "base64", description = ComponentType("list<u8>") },
            "list" => new
            {
                type = "array",
                items = SchemaForType(type.GetProperty("element")),
                description = ComponentType("list"),
            },
            "option" => new
            {
                oneOf = new[] { SchemaForType(type.GetProperty("value")), new { type = "null" } },
                description = "Component-Model-Typ 'option' — null bedeutet none.",
            },
            "result" => ResultSchema(type),
            // Sollte nie vorkommen: nicht abbildbare Exports stehen gar nicht erst im Katalog.
            _ => new { description = "Typ ohne Schema." },
        };
    }

    /// <summary><c>result&lt;T,E&gt;</c> als Objekt mit genau einem der Zweige.</summary>
    private static object ResultSchema(JsonElement type)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        if (type.TryGetProperty("ok", out var ok))
        {
            properties["ok"] = SchemaForType(ok);
        }

        if (type.TryGetProperty("err", out var err))
        {
            properties["err"] = SchemaForType(err);
        }

        return new
        {
            type = "object",
            properties,
            additionalProperties = false,
            minProperties = 1,
            maxProperties = 1,
            description = "Component-Model-Typ 'result' — genau eines von 'ok' oder 'err'.",
        };
    }

    private static string ComponentType(string name) => $"Component-Model-Typ '{name}'.";

    private static object Described(string jsonType, string componentType)
        => new { type = jsonType, description = ComponentType(componentType) };

    private static object Bounded(long minimum, long maximum, string componentType)
        => new { type = "integer", minimum, maximum, description = ComponentType(componentType) };

    private static object Pattern(string pattern, string componentType)
        => new { type = "string", pattern, description = $"Component-Model-Typ '{componentType}' als Dezimalstring." };

    private static string Describe(JsonElement descriptor, string export, IReadOnlyList<WasiParameter> parameters)
    {
        var kind = descriptor.TryGetProperty("kind", out var declared) ? declared.GetString() : null;
        // Der rohe Export-Name bleibt in der Beschreibung: Wer im Katalog 'wasi_cli_run' sieht,
        // muss ihn im Component wiederfinden können.
        return kind == "command"
            ? $"WASI-Kommando-Export '{export}' — läuft ohne Argumente."
            : $"WASI-Funktions-Export '{export}'({string.Join(", ", parameters.Select(p => p.Name))}).";
    }

    /// <summary>Ein Parameter mit dem Typbaum, den der Host gemeldet hat.</summary>
    private sealed record WasiParameter(string Name, JsonElement Type);
}
