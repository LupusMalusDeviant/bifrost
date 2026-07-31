using System.Text.Json;
using Bifrost.Abstractions;

namespace Bifrost.Core.Capabilities;

/// <summary>
/// Bildet die bestehende Deskriptorwelt auf <see cref="CapabilityDescriptorV1"/> ab (ADR-0015,
/// Kompatibilität).
/// <para>
/// <b>Verlustfrei</b> heißt hier: Jedes Feld von <see cref="ToolDescriptor"/> und
/// <see cref="CatalogEntry"/> findet sich wieder, und nichts wird erfunden. Wo die alte Welt eine
/// Eigenschaft nicht kennt — Ausgabeschema, Fortschritt, Pagination — steht die vorsichtige Annahme,
/// nicht eine geratene. Ein Katalog, der Fähigkeiten behauptet, die der Upstream nicht hat, wäre
/// schlimmer als einer, der zu wenig verspricht.
/// </para>
/// <para>
/// Der Adapter ist bewusst eine <b>Projektion</b> und kein Ersatz: Die Endpunkte für Tools,
/// Resources und Prompts bleiben, bis eine eigene Migration sie ablöst.
/// </para>
/// </summary>
public static class LegacyCapabilityAdapter
{
    /// <summary>
    /// Projiziert einen Katalogeintrag. Der Katalogname liegt hier schon namespaced vor; der native
    /// Name wird daraus zurückgewonnen, weil die stabile Id an ihm hängt und nicht am Präfix.
    /// <paramref name="connector"/> ist optional — der Katalog kennt die Transportart nicht, und
    /// eine geratene wäre schlechter als keine.
    /// </summary>
    public static CapabilityDescriptorV1 FromCatalogEntry(
        CatalogEntry entry, UpstreamTransportKind? connector = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var nativeName = entry.Name.TrySplit(out _, out var tool) ? tool : entry.Name.Value;
        return Build(
            nativeName,
            entry.Name,
            entry.Description,
            entry.Server,
            connector,
            entry.InputSchema,
            KindOf(entry.Kind, entry.Risk),
            entry.Risk,
            entry.RequiresApproval);
    }

    /// <summary>Projiziert einen Tool-Deskriptor eines Upstreams, vor der Katalogaufnahme.</summary>
    public static CapabilityDescriptorV1 FromToolDescriptor(
        ToolDescriptor tool, ServerId upstream, string upstreamSlug,
        UpstreamTransportKind? connector = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Build(
            tool.Name,
            NamespacedToolName.Create(upstreamSlug, tool.Name),
            tool.Description,
            upstream,
            connector,
            tool.InputSchema,
            KindOf(CatalogEntryKind.Tool, tool.Risk),
            tool.Risk,
            tool.RequiresApproval);
    }

    private static CapabilityDescriptorV1 Build(
        string nativeName,
        NamespacedToolName catalogName,
        string? description,
        ServerId upstream,
        UpstreamTransportKind? connector,
        JsonElement inputSchema,
        CapabilityKind kind,
        CapabilityRisk risk,
        bool requiresApproval)
        => new(
            CapabilityId.Derive(upstream, nativeName),
            nativeName,
            catalogName,
            // Anzeigename ist heute der Katalogname; er darf sich später ändern, ohne die Id zu
            // berühren — genau dafür sind die beiden Felder getrennt.
            DisplayName: catalogName.Value,
            description,
            kind,
            // Alle bestehenden Connectoren antworten im Aufruf. Asynchron wird eine Fähigkeit erst,
            // wenn ein Connector es sagt — nicht, weil das Modell es könnte.
            CapabilityExecution.Synchronous,
            upstream,
            connector,
            Input: SchemaFor(inputSchema),
            // Kein bestehender Connector liefert ein Ausgabeschema. `null` ist hier die Wahrheit;
            // ein erfundenes „object" wäre eine Zusage ohne Deckung.
            Output: null,
            SideEffect: risk,
            RequiresApproval: requiresApproval,
            // Idempotenz nur da, wo sie aus dem Seiteneffekt folgt: Lesen ist wiederholbar, alles
            // andere weiß der Upstream — und keiner sagt es heute.
            Idempotent: risk is CapabilityRisk.Read,
            // Der Aufrufer kann jeden Call abbrechen (FR-09 und der Per-Call-Timeout); ob der
            // Upstream das bestätigt, ist eine andere Frage und steht in ADR-0019.
            SupportsCancellation: true,
            SupportsProgress: false,
            SupportsBinary: false,
            SupportsPagination: false);

    /// <summary>
    /// Ordnet die Katalog-Art einer Capability-Art zu. Lesende Tools sind
    /// <see cref="CapabilityKind.Query"/>, schreibende <see cref="CapabilityKind.Mutation"/> — die
    /// Unterscheidung steckt schon im Risk und wird hier nur sichtbar gemacht, statt sie neu zu
    /// erfinden.
    /// </summary>
    private static CapabilityKind KindOf(CatalogEntryKind kind, CapabilityRisk risk) => kind switch
    {
        CatalogEntryKind.Resource => CapabilityKind.Resource,
        CatalogEntryKind.Prompt => CapabilityKind.Prompt,
        CatalogEntryKind.Tool or CatalogEntryKind.MetaTool => risk is CapabilityRisk.Read
            ? CapabilityKind.Query
            : CapabilityKind.Mutation,
        _ => CapabilityKind.Tool,
    };

    /// <summary>Ein leeres Schema heißt „keine Argumente", nicht „unbekanntes Schema".</summary>
    private static SchemaRef? SchemaFor(JsonElement schema)
    {
        if (schema.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind is JsonValueKind.Object
            && properties.EnumerateObject().Any();
        return hasProperties
            ? SchemaRef.ForSchema(schema, SchemaProvenance.Native)
            : SchemaRef.None;
    }
}
