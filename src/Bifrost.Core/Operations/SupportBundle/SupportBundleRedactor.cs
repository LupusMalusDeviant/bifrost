using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bifrost.Core.Audit;

namespace Bifrost.Core.Operations.SupportBundle;

/// <summary>
/// Der <b>eine</b> Durchgang, durch den jedes Dokument des Bundles geht, bevor es geschrieben wird.
/// <para>
/// <b>Warum zentral und nicht je Quelle:</b> Die Quellen sind für sich schon zurückhaltend — der
/// Konfigurationsexport nimmt keine Secretwerte mit, die Diagnose kennt nur „sichere" Angaben. Genau
/// darauf beruht die Gefahr: Jede neue Quelle erbt diese Annahme, ohne sie zu prüfen. Ein Durchgang
/// am Ausgang gilt auch für die Quelle, die morgen dazukommt.
/// </para>
/// <para>
/// Er ist ein <b>Sicherheitsnetz, keine Erlaubnis.</b> Wer eine Quelle anschließt, die Secrets
/// führt, hat einen Fehler gemacht — der Durchgang macht ihn nur nicht zum Vorfall.
/// </para>
/// </summary>
public sealed class SupportBundleRedactor
{
    public const string PassName = "central-v1";

    public const string Mask = "***";

    /// <summary>
    /// Werte in Verbindungszeichenfolgen und Kopfzeilen, die kein Feldname verrät:
    /// <c>Password=…</c>, <c>Bearer …</c> und Zugangsdaten in einer URL.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] ValueRules =
    [
        (new Regex(@"(?i)\b(password|pwd)\s*=\s*[^;""'\s]+", RegexOptions.Compiled), "$1=" + Mask),
        (new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.Compiled), "Bearer " + Mask),
        (new Regex(@"(?i)\b([a-z][a-z0-9+.\-]*://)[^/\s:@]+:[^/\s@]+@", RegexOptions.Compiled), "$1" + Mask + ":" + Mask + "@"),
    ];

    private int _redacted;

    public int RedactedFields => _redacted;

    /// <summary>Maskiert ein JSON-Dokument nach Feldnamen und Werten.</summary>
    public string RedactJson(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
        {
            return json;
        }

        Walk(node);
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>Maskiert freien Text — für alles, was kein JSON ist (Logzeilen, Hinweise).</summary>
    public string RedactText(string text)
    {
        foreach (var (pattern, replacement) in ValueRules)
        {
            text = pattern.Replace(text, match =>
            {
                _redacted++;
                return match.Result(replacement);
            });
        }

        return text;
    }

    private void Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IsSecretName(name) && obj[name] is not (null or JsonObject or JsonArray))
                    {
                        obj[name] = Mask;
                        _redacted++;
                        continue;
                    }

                    if (obj[name] is { } child)
                    {
                        Walk(child);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array.OfType<JsonNode>())
                {
                    Walk(item);
                }

                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                var scrubbed = RedactText(text);
                if (!string.Equals(scrubbed, text, StringComparison.Ordinal))
                {
                    value.ReplaceWith(scrubbed);
                }

                break;
        }
    }

    // Dieselbe Liste wie die Audit-Redaktion — verlinkt statt kopiert.
    private static bool IsSecretName(string name) =>
        RedactionService.DefaultPatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
}
