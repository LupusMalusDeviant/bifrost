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
    /// Review-Befund L1: Ein Verweis auf das Schema eines anderen Werkzeugs löst nicht auf. Gebaut
    /// wird das Schema noch; erst die Auswertung wirft <see cref="RefResolutionException"/>. Über den
    /// Aufrufweg heißt das: R3-Ausweichweg, der Aufruf läuft ungeprüft durch, statt abgewiesen zu
    /// werden. Früher hing das davon ab, ob das andere Werkzeug schon gerufen war.
    /// </summary>
    [Fact]
    public async Task A_reference_to_another_tools_schema_does_not_resolve_and_falls_back()
    {
        const string referencing =
            """{"type":"object","properties":{"message":{"$ref":"https://example.invalid/common#/$defs/y"}}}""";

        var evaluate = () => ToolInvoker.BuildSchema(Entry(referencing))
            .Evaluate(JsonDocument.Parse("""{"message":"hi"}""").RootElement);
        evaluate.Should().Throw<RefResolutionException>();

        var world = new InvokerTestWorld(echoSchema: referencing);
        var admin = world.RegisterAdmin();
        var log = new CapturingLogger();
        var invoker = world.WithLogger(log);
        var ct = TestContext.Current.CancellationToken;

        for (var call = 0; call < 20; call++)
        {
            var result = await invoker.InvokeAsync(
                InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct);
            result.Status.Should().Be(InvocationStatus.Success, "R3: nicht auswertbar heißt durchlassen und loggen");
        }

        // Review-Befund (dritte Runde): Grund und Werkzeug stehen im Log — einmal, nicht je Aufruf.
        log.Lines.Should().ContainSingle()
            .Which.Should().Contain("echo").And.Contain("https://example.invalid/common");
        ToolInvoker.UnusableSchemas.TryGetValue(world.Catalog.Find(world.Echo)!, out _).Should().BeTrue();
    }

    /// <summary>
    /// Review-Befund (dritte Runde): Ein Schema, das sich nicht bauen lässt, wird nicht bei jedem
    /// Aufruf neu gebaut. Das bewiese der Speicher nur über Stunden; hier zählt die Logzeile: Sie
    /// entsteht beim Bauen, und es gibt sie genau einmal. Der Aufruf geht weiter über R3 durch.
    /// </summary>
    [Fact]
    public async Task An_unbuildable_schema_is_built_and_logged_once()
    {
        var world = new InvokerTestWorld();
        var admin = world.RegisterAdmin();
        var log = new CapturingLogger();
        var invoker = world.WithLogger(log);
        var ct = TestContext.Current.CancellationToken;

        for (var call = 0; call < 20; call++)
        {
            var result = await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Free, new { }), ct);
            result.Status.Should().Be(InvocationStatus.Success);
        }

        log.Lines.Should().ContainSingle().Which.Should().Contain("free");
        var entry = world.Catalog.Find(world.Free)!;
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeTrue();
        ToolInvoker.CompiledSchemas.TryGetValue(entry, out _).Should().BeFalse();
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<ToolInvoker>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _lines.Enqueue(formatter(state, exception));
    }

    /// <summary>
    /// Review-Befund M2: Schemas mit ausdrücklichem <c>$schema</c>. Die eigene Registry fällt für die
    /// Meta-Schemata auf die globale zurück; bräche das, liefe jeder Aufruf eines solchen Werkzeugs
    /// still über den R3-Ausweichweg — und kein anderer Test fiele auf. Je Draft ein Schlüsselwort,
    /// das es nur dort gibt, über den echten Aufrufweg.
    /// </summary>
    [Theory]
    [InlineData("http://json-schema.org/draft-07/schema#", "items")]
    [InlineData("https://json-schema.org/draft/2019-09/schema", "items")]
    [InlineData("https://json-schema.org/draft/2020-12/schema", "prefixItems")]
    public async Task Schemas_of_each_draft_still_reject_invalid_arguments(string dialect, string tupleKeyword)
    {
        var schema = "{\"$schema\":\"" + dialect + "\",\"type\":\"object\",\"required\":[\"message\",\"pair\"],"
            + "\"properties\":{\"message\":{\"type\":\"string\"},\"pair\":{\"type\":\"array\",\""
            + tupleKeyword + "\":[{\"type\":\"string\"},{\"type\":\"integer\"}]}}}";
        var world = new InvokerTestWorld(echoSchema: schema);
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;

        var valid = await world.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, world.Echo, new { message = "hi", pair = new object[] { "a", 1 } }), ct);
        var invalid = await world.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, world.Echo, new { message = "hi", pair = new object[] { 1, "a" } }), ct);

        valid.Status.Should().Be(InvocationStatus.Success);
        invalid.Status.Should().Be(InvocationStatus.ValidationFailed,
            "das Tupel-Schlüsselwort des Drafts muss greifen; sonst wäre der Aufruf über R3 ungeprüft durchgelaufen");
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
