using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Abstractions;

namespace Bifrost.Core.Audit;

/// <summary>
/// Maskiert Secret-Felder in Tool-Argumenten vor der Persistierung (FR-24, DON'T Nr. 2).
/// Globale Default-Muster (Substring-Match auf Property-Namen, case-insensitiv) plus
/// zusätzliche Regeln pro Tool. Arbeitet rekursiv über Objekte und Arrays; das Original
/// bleibt unberührt (JsonElement ist immutabel, Ausgabe ist ein neuer Baum).
/// </summary>
public sealed class RedactionService : IRedactionService
{
    public const string Mask = "***";

    private static readonly string[] DefaultPatterns =
    [
        "password", "passwort", "token", "secret", "key", "authorization", "credential", "apikey",
    ];

    private readonly ConcurrentDictionary<NamespacedToolName, IReadOnlyList<string>> _toolRules = new();
    private readonly IRedactionRules? _rules;

    public RedactionService(IRedactionRules? rules = null) => _rules = rules;

    /// <summary>Ergänzt tool-spezifische Property-Muster (additiv zu den Defaults).</summary>
    public void SetToolRules(NamespacedToolName tool, IReadOnlyList<string> propertyPatterns)
    {
        ArgumentNullException.ThrowIfNull(propertyPatterns);
        _toolRules[tool] = propertyPatterns;
    }

    public JsonElement RedactArguments(NamespacedToolName tool, JsonElement args)
    {
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return args; // Skalare/Undefined enthalten keine benannten Secret-Felder
        }

        // Konfigurierte Regeln aus dem Store haben Vorrang; die In-Memory-Variante bleibt für
        // Setups ohne Persistenz (und für Tests) bestehen.
        var extraRules = _rules?.GetPatterns(tool) ?? _toolRules.GetValueOrDefault(tool);
        var node = JsonNode.Parse(args.GetRawText())!;
        RedactNode(node, extraRules);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void RedactNode(JsonNode node, IReadOnlyList<string>? extraRules)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var propertyName in obj.Select(p => p.Key).ToList())
                {
                    if (IsSecretProperty(propertyName, extraRules))
                    {
                        obj[propertyName] = Mask;
                    }
                    else if (obj[propertyName] is { } child)
                    {
                        RedactNode(child, extraRules);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        RedactNode(item, extraRules);
                    }
                }

                break;
            default:
                break; // Werte ohne Property-Namen werden nicht maskiert
        }
    }

    private static bool IsSecretProperty(string propertyName, IReadOnlyList<string>? extraRules)
    {
        foreach (var pattern in DefaultPatterns)
        {
            if (propertyName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (extraRules is not null)
        {
            foreach (var pattern in extraRules)
            {
                if (propertyName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
