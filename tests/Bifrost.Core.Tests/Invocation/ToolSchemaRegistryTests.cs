using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Json.Schema;
using Xunit;

namespace Bifrost.Core.Tests.Invocation;

/// <summary>
/// Das Speicherwachstum des Soak-Nachtlaufs (269 → 3768 MB in vier Stunden): Die
/// Argumentprüfung baute bei jedem Werkzeugaufruf das Eingabeschema neu, und JsonSchema.Net legte
/// jedes gebaute Schema in <c>SchemaRegistry.Global</c> ab — für immer. Zwei Heap-Abzüge eines
/// Soak-Laufs zeigten genau das: <c>JsonSchema</c>-Objekte 15 805 → 64 252 in neun Minuten.
/// </summary>
public sealed class ToolSchemaRegistryTests
{
    private static CatalogEntry Entry(string schema) => new(
        new NamespacedToolName("echo__echo"),
        ServerId.New(),
        "Echo",
        JsonDocument.Parse(schema).RootElement.Clone(),
        CatalogEntryKind.Tool,
        EstimatedSchemaTokens: 10);

    [Fact]
    public void A_tool_schema_is_not_left_in_the_global_registry()
    {
        var schema = ToolInvoker.BuildSchema(
            Entry("""{"type":"object","properties":{"message":{"type":"string"}}}"""));

        SchemaRegistry.Global.Get(schema.BaseUri).Should().BeNull(
            "ein Schema je Aufruf in der globalen Registry wächst ohne Ende");
    }

    /// <summary>
    /// Gegenprobe: Mit den Vorgaben landet es dort. Ohne diese Prüfung bewiese der Test oben nichts,
    /// falls eine künftige Version der Bibliothek gar nicht mehr global registriert.
    /// </summary>
    [Fact]
    public void With_the_library_defaults_the_schema_would_stay_in_the_global_registry()
    {
        var schema = JsonSchema.FromText("""{"type":"object"}""");

        SchemaRegistry.Global.Get(schema.BaseUri).Should().NotBeNull();
    }

    [Fact]
    public void A_schema_built_with_its_own_registry_still_validates()
    {
        var schema = ToolInvoker.BuildSchema(
            Entry("""{"type":"object","required":["message"],"properties":{"message":{"type":"string"}}}"""));

        schema.Evaluate(JsonDocument.Parse("""{"message":"hallo"}""").RootElement).IsValid.Should().BeTrue();
        schema.Evaluate(JsonDocument.Parse("""{"message":1}""").RootElement).IsValid.Should().BeFalse();
        schema.Evaluate(JsonDocument.Parse("{}").RootElement).IsValid.Should().BeFalse();
    }
}
