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
    /// falls eine künftige Version der Bibliothek gar nicht mehr global registriert. Das eine Schema
    /// bleibt für die Dauer des Testprozesses in der globalen Registry — das ist der Preis der Probe.
    /// </summary>
    [Fact]
    public void With_the_library_defaults_the_schema_would_stay_in_the_global_registry()
    {
        var schema = JsonSchema.FromText("""{"type":"object"}""");

        SchemaRegistry.Global.Get(schema.BaseUri).Should().NotBeNull();
    }

    /// <summary>
    /// Review-Befund M1: über den echten Aufrufweg. Viele Aufrufe desselben Werkzeugs bauen das
    /// Schema genau einmal — derselbe Gegenstand im Zwischenspeicher, nicht in der globalen Registry.
    /// Fiele der Zwischenspeicher oder die eigene Registry wieder weg, schlüge das hier fehl.
    /// </summary>
    [Fact]
    public async Task Repeated_calls_through_the_invoker_build_the_schema_once()
    {
        var world = new InvokerTestWorld();
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;

        JsonSchema? first = null;
        for (var call = 0; call < 50; call++)
        {
            var result = await world.Invoker.InvokeAsync(
                InvokerTestWorld.Request(admin, world.Echo, new { message = "hi" }), ct);
            result.Status.Should().Be(InvocationStatus.Success);

            var entry = world.Catalog.Find(world.Echo)!;
            ToolInvoker.CompiledSchemas.TryGetValue(entry, out var cached).Should().BeTrue(
                "der Aufruf legt das gebaute Schema für seinen Katalogeintrag ab");
            first ??= cached;
            cached.Should().BeSameAs(first, "jeder weitere Aufruf nimmt das schon gebaute Schema");
        }

        SchemaRegistry.Global.Get(first!.BaseUri).Should().BeNull();

        var invalid = await world.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct);
        invalid.Status.Should().Be(InvocationStatus.ValidationFailed, "das zwischengespeicherte Schema prüft weiter");
    }

    /// <summary>
    /// Review-Befund M2: Ein Schema wird von gleichzeitigen Aufrufen geteilt. Mit rekursivem
    /// <c>$ref</c> und <c>$defs</c> — der Teil, an dem eine faul aufgebaute Auflösung am ehesten
    /// Zustand am Schema hielte. Jedes Urteil muss stimmen; ein falsches „gültig" wäre ein
    /// ungeprüfter Aufruf.
    /// </summary>
    [Fact]
    public void A_shared_schema_judges_correctly_under_concurrent_evaluation()
    {
        var schema = ToolInvoker.BuildSchema(Entry("""
            {
              "$id": "https://example.invalid/tree",
              "type": "object",
              "required": ["node"],
              "properties": { "node": { "$ref": "#/$defs/node" } },
              "$defs": {
                "node": {
                  "type": "object",
                  "required": ["name"],
                  "properties": {
                    "name": { "type": "string" },
                    "children": { "type": "array", "items": { "$ref": "#/$defs/node" } }
                  }
                }
              }
            }
            """));
        var valid = JsonDocument.Parse(
            """{"node":{"name":"a","children":[{"name":"b","children":[{"name":"c"}]}]}}""").RootElement;
        var invalid = JsonDocument.Parse(
            """{"node":{"name":"a","children":[{"name":"b","children":[{"name":3}]}]}}""").RootElement;

        var wrong = 0;
        Parallel.For(0, 10_000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            var expectValid = i % 2 == 0;
            var verdict = schema.Evaluate(expectValid ? valid : invalid).IsValid;
            if (verdict != expectValid)
            {
                Interlocked.Increment(ref wrong);
            }
        });

        wrong.Should().Be(0);
    }

    /// <summary>
    /// Review-Befund L1: Zwei Werkzeuge mit demselben <c>$id</c> überschreiben sich nicht mehr — jedes
    /// prüft nach seinem eigenen Schema.
    /// </summary>
    [Fact]
    public void Two_tools_with_the_same_id_keep_their_own_schemas()
    {
        var strict = ToolInvoker.BuildSchema(Entry(
            """{"$id":"https://example.invalid/shared","type":"object","required":["a"]}"""));
        var loose = ToolInvoker.BuildSchema(Entry(
            """{"$id":"https://example.invalid/shared","type":"object"}"""));

        var empty = JsonDocument.Parse("{}").RootElement;
        strict.Evaluate(empty).IsValid.Should().BeFalse();
        loose.Evaluate(empty).IsValid.Should().BeTrue();
    }

    /// <summary>
    /// Review-Befund L1: Ein Verweis auf das Schema eines anderen Werkzeugs löst nicht auf. Der Bau
    /// scheitert, und der Aufruf geht über den R3-Ausweichweg — ungeprüft durch, mit Logzeile —
    /// statt abgewiesen zu werden. Früher hing das davon ab, ob das andere Werkzeug schon gerufen war.
    /// </summary>
    [Fact]
    public void A_reference_to_another_tools_schema_does_not_resolve()
    {
        ToolInvoker.BuildSchema(Entry(
            """{"$id":"https://example.invalid/common","$defs":{"y":{"type":"string"}}}"""));

        var referencing = Entry(
            """{"type":"object","properties":{"x":{"$ref":"https://example.invalid/common#/$defs/y"}}}""");
        var build = () => ToolInvoker.BuildSchema(referencing).Evaluate(
            JsonDocument.Parse("""{"x":1}""").RootElement);

        build.Should().Throw<Exception>("ein fremder Verweis bleibt unaufgelöst; ValidateArguments fängt das ab");
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
