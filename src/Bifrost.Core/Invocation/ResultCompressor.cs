using System.Text.Json;
using System.Text.Json.Nodes;
using Bifrost.Abstractions;

namespace Bifrost.Core.Invocation;

/// <summary>
/// Kürzt übergroße Tool-Ergebnisse (FR-16), damit ein einzelnes fettes Ergebnis nicht die
/// Token-Ersparnis der Profile auffrisst.
///
/// Zwei Regeln, die den Nutzen erst brauchbar machen:
/// <list type="number">
/// <item>Das Ergebnis bleibt <b>valides JSON</b>. Ein roh abgeschnittener Text wäre für den Agenten
/// unparsbar — er hätte dann gar nichts statt zu wenig.</item>
/// <item>Die Kürzung ist im Ergebnis <b>erkennbar</b> (<see cref="ResultTruncation.MarkerProperty"/>).
/// Verschwiegen wäre sie schlimmer als die Größe: der Agent hielte das Bruchstück für vollständig.</item>
/// </list>
/// </summary>
public static class ResultCompressor
{
    /// <summary>
    /// Gibt das Ergebnis unverändert zurück, wenn es unter der Grenze liegt oder die Kompression
    /// abgeschaltet ist. Sonst das gekürzte Ergebnis plus <see cref="ResultTruncation"/>.
    /// </summary>
    public static (JsonElement Content, ResultTruncation? Truncation) Compress(
        JsonElement content, ResultCompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxResultChars <= 0 || content.ValueKind is JsonValueKind.Undefined)
        {
            return (content, null);
        }

        var raw = content.GetRawText();
        if (raw.Length <= options.MaxResultChars)
        {
            return (content, null);
        }

        // Unter der Mindestgrenze wäre der Hinweis größer als das, was übrig bleibt.
        var limit = Math.Max(options.MaxResultChars, ResultCompressionOptions.MinimumUsefulLimit);

        var truncated = Shorten(content, limit);
        var result = JsonSerializer.SerializeToElement(truncated);
        return (result, new ResultTruncation(raw.Length, result.GetRawText().Length));
    }

    private static JsonObject Shorten(JsonElement content, int limit)
    {
        // Arrays sind der häufigste Fall (Listen von Treffern) und lassen sich verlustarm kürzen:
        // vordere Elemente behalten, Rest zählen. Der Agent sieht Struktur UND was fehlt.
        if (content.ValueKind is JsonValueKind.Array)
        {
            var items = content.EnumerateArray().ToList();
            var kept = new JsonArray();
            var used = 0;
            var keptCount = 0;

            foreach (var item in items)
            {
                var text = item.GetRawText();
                if (used + text.Length > limit && keptCount > 0)
                {
                    break;
                }

                kept.Add(JsonNode.Parse(text));
                used += text.Length;
                keptCount++;
            }

            return new JsonObject
            {
                [ResultTruncation.MarkerProperty] = true,
                ["items"] = kept,
                ["returnedItems"] = keptCount,
                ["totalItems"] = items.Count,
                ["note"] = $"Ergebnis gekürzt: {keptCount} von {items.Count} Einträgen. "
                    + "Grenze über die Gateway-Konfiguration anpassbar.",
            };
        }

        // Strings: hinten abschneiden, Rest als Zahl ausweisen.
        if (content.ValueKind is JsonValueKind.String)
        {
            var text = content.GetString() ?? string.Empty;
            var keep = Math.Min(text.Length, limit);
            return new JsonObject
            {
                [ResultTruncation.MarkerProperty] = true,
                ["text"] = text[..keep],
                ["omittedChars"] = text.Length - keep,
                ["note"] = "Ergebnis gekürzt.",
            };
        }

        // Objekte und alles Übrige: als Rohtext kürzen und ausdrücklich als unvollständig markieren.
        // Einzelne Felder wegzulassen wäre riskant — welches Feld wichtig ist, weiß nur der Aufrufer.
        var rawText = content.GetRawText();
        return new JsonObject
        {
            [ResultTruncation.MarkerProperty] = true,
            ["partialJson"] = rawText[..Math.Min(rawText.Length, limit)],
            ["omittedChars"] = Math.Max(0, rawText.Length - limit),
            ["note"] = "Ergebnis gekürzt; 'partialJson' ist ein Ausschnitt und für sich nicht parsbar.",
        };
    }
}
