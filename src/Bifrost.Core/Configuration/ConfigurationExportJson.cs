using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Core.Configuration;

/// <summary>
/// Die eine Serialisierungseinstellung des Exports.
/// <para>
/// <b>Eingerückt und mit Klartext-Aufzählungen</b>, weil das Ziel dieses Formats ein
/// Versionskontrollsystem ist: Ein Diff, in dem <c>"mode": 1</c> zu <c>"mode": 0</c> wird, sagt dem
/// Prüfer eines Pull Requests nichts; <c>"Block"</c> zu <c>"Observe"</c> sagt ihm alles.
/// </para>
/// </summary>
public static class ConfigurationExportJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    /// <summary>
    /// Dieselbe Abbildung ohne Einrückung — für den Vergleich „ist das schon da und identisch?".
    /// <para>
    /// Verglichen wird über JSON und nicht über <c>record</c>-Gleichheit: Die Konfigurationstypen
    /// tragen <c>IReadOnlyList</c>- und <c>IReadOnlyDictionary</c>-Felder, und die vergleicht ein
    /// Record über die <em>Referenz</em>. Zwei inhaltsgleiche Konfigurationen wären damit ungleich,
    /// und der Import meldete Konflikte, wo keine sind.
    /// </para>
    /// </summary>
    public static JsonSerializerOptions ComparisonOptions { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Umlaute und Apostrophe bleiben stehen, statt als ä und ' zu erscheinen. Der
            // Name der Einstellung meint HTML-Kontexte; ein Exportdokument wird von einem
            // JSON-Parser gelesen, und der löst die Ersatzdarstellung ohnehin auf, bevor der Text
            // irgendetwas erreicht. Die Escapes schützen hier also nichts und kosten genau das, wofür
            // dieses Format da ist: einen Diff, den ein Mensch beurteilen kann.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
