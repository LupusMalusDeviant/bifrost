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
    /// Aufrufweg heißt das seit der fünften Runde: abgewiesen, nicht ungeprüft durchgelassen.
    /// Früher hing das davon ab, ob das andere Werkzeug schon gerufen war.
    /// </summary>
    [Fact]
    public async Task A_reference_to_another_tools_schema_does_not_resolve_and_is_rejected()
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
            result.Status.Should().Be(InvocationStatus.ValidationFailed, "nicht auswertbar heißt abweisen");
        }

        // Review-Befund (dritte Runde): Grund und Werkzeug stehen im Log — einmal, nicht je Aufruf.
        log.Lines.Should().ContainSingle()
            .Which.Should().Contain("echo").And.Contain("https://example.invalid/common");

        // Review-Befund H1 (vierte Runde): Ein Auswertungsfehler wird nicht als beständig gemerkt.
        ToolInvoker.UnusableSchemas.TryGetValue(world.Catalog.Find(world.Echo)!, out _).Should().BeFalse();
    }

    /// <summary>
    /// Review-Befunde H1 (vierte Runde) und M1 (fünfte Runde): Ob ein unauflösbarer Verweis erreicht
    /// wird, wählt der Aufrufer mit seinen Argumenten. Ein Aufruf, der ihn erreicht, wird abgewiesen
    /// — auch dann, wenn ein <i>anderes</i> Feld ungültig ist; liesse er durch, schmuggelte das
    /// passende Feld jeden fehlerhaften Wert zum Upstream. Und er schaltet die Prüfung für die
    /// folgenden Aufrufe nicht ab.
    /// </summary>
    [Fact]
    public async Task A_call_that_reaches_an_unresolvable_reference_is_rejected_and_does_not_switch_off_validation()
    {
        const string schema =
            """{"type":"object","required":["id"],"properties":{"id":{"type":"integer"},"extra":{"$ref":"https://other.invalid/x"}}}""";
        var world = new InvokerTestWorld(echoSchema: schema);
        var admin = world.RegisterAdmin();
        var log = new CapturingLogger();
        var invoker = world.WithLogger(log);
        var ct = TestContext.Current.CancellationToken;

        for (var call = 0; call < 5; call++)
        {
            var reaching = await invoker.InvokeAsync(
                InvokerTestWorld.Request(admin, world.Echo, new { id = 1, extra = 1 }), ct);
            reaching.Status.Should().Be(InvocationStatus.ValidationFailed, "der Verweis lässt sich nicht auswerten");

            var smuggling = await invoker.InvokeAsync(
                InvokerTestWorld.Request(admin, world.Echo, new { id = "abc", extra = 1 }), ct);
            smuggling.Status.Should().Be(InvocationStatus.ValidationFailed,
                "das Feld mit dem Verweis darf das ungültige id nicht an der Prüfung vorbeiführen");

            var invalid = await invoker.InvokeAsync(
                InvokerTestWorld.Request(admin, world.Echo, new { id = "abc" }), ct);
            invalid.Status.Should().Be(InvocationStatus.ValidationFailed,
                "ein Aufruf, der den Verweis nicht erreicht, wird weiter geprüft");

            var valid = await invoker.InvokeAsync(
                InvokerTestWorld.Request(admin, world.Echo, new { id = 1 }), ct);
            valid.Status.Should().Be(InvocationStatus.Success, "ohne den Verweis ist das Werkzeug nutzbar");
        }

        log.Lines.Should().ContainSingle("der Grund steht einmal im Log, nicht je Aufruf");
    }

    /// <summary>
    /// Siebte Runde, M1: Ein Baufehler, der nicht am Schematext liegt (hier ein zu tiefer Stack),
    /// wird nicht gemerkt und lässt den Aufruf nicht durch. Der nächste Aufruf baut neu und prüft.
    /// Gemerkt, hätte ein einmaliger Ausfall die Prüfung bis zum nächsten Katalog-Neubau abgeschaltet.
    /// </summary>
    [Fact]
    public async Task A_transient_build_failure_is_not_remembered_and_does_not_let_the_call_through()
    {
        var world = new InvokerTestWorld();
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;
        var failures = 1;
        var invoker = new ToolInvoker(
            world.Authorization, world.RateLimiter, world.Catalog, world.Supervisor, world.Audit, world.Redaction, world.Time)
        {
            SchemaBuilder = entry => Interlocked.Decrement(ref failures) >= 0
                ? throw new InsufficientExecutionStackException()
                : ToolInvoker.BuildSchema(entry),
        };

        var first = await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = "hi" }), ct);
        first.Status.Should().Be(InvocationStatus.ValidationFailed, "ohne gebautes Schema kein ungeprüfter Durchlass");
        first.ErrorMessage.Should().Contain(nameof(InsufficientExecutionStackException));

        var entry = world.Catalog.Find(world.Echo)!;
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse("ein Ausfall des Augenblicks wird nicht gemerkt");

        var invalid = await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct);
        invalid.Status.Should().Be(InvocationStatus.ValidationFailed, "der zweite Aufruf baut neu und prüft");
        var valid = await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = "hi" }), ct);
        valid.Status.Should().Be(InvocationStatus.Success);
    }

    /// <summary>
    /// Siebte Runde, M2: Dialekte, die JsonSchema.Net nicht kennt — draft-04, den viele Upstreams
    /// senden, und ein erfundenes <c>$schema</c>. Die Bibliothek scheitert daran schon beim
    /// <i>Bauen</i> (<see cref="JsonSchemaException"/>, „Cannot find dialect"). Das ist der
    /// R3-Weg wie auf main: durchgelassen, einmal geloggt, als nicht baubar gemerkt — und nicht etwa
    /// eine Prüfung, die draft-04-Schlüsselwörter falsch läse.
    /// </summary>
    [Theory]
    [InlineData("http://json-schema.org/draft-04/schema#")]
    [InlineData("https://example.invalid/my-dialect")]
    public async Task An_unknown_dialect_takes_the_R3_path_and_is_logged_once(string dialect)
    {
        var schema = "{\"$schema\":\"" + dialect + "\",\"type\":\"object\",\"required\":[\"message\"],"
            + "\"properties\":{\"message\":{\"type\":\"string\"}}}";
        var world = new InvokerTestWorld(echoSchema: schema);
        var admin = world.RegisterAdmin();
        var log = new CapturingLogger();
        var invoker = world.WithLogger(log);
        var ct = TestContext.Current.CancellationToken;

        for (var call = 0; call < 5; call++)
        {
            var result = await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct);
            result.Status.Should().Be(InvocationStatus.Success, "R3: ein Schema, das nicht baut, lässt durch");
        }

        log.Lines.Should().ContainSingle().Which.Should().Contain(dialect);
        ToolInvoker.UnusableSchemas.TryGetValue(world.Catalog.Find(world.Echo)!, out _).Should().BeTrue();
    }

    /// <summary>
    /// Siebte Runde, M3: Nach einem Katalog-Neubau wird das alte gebaute Schema freigegeben — samt
    /// seiner eigenen Registry. Hielte die Bibliothek oder der Invoker es irgendwo statisch fest,
    /// wüchse der Speicher je Neubau; im Soak-Chaos (Upstream-Neustarts) geschieht das ständig.
    /// </summary>
    [Fact]
    public async Task After_a_catalog_rebuild_the_old_schema_is_collected()
    {
        var world = new InvokerTestWorld();
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;

        var oldSchema = await BuildThroughInvokerAsync(world, admin, ct);

        world.Supervisor.SetServer(world.Server, world.Slug, new UpstreamInventory(
            [
                new ToolDescriptor(
                    "echo",
                    "Echoes a message back.",
                    JsonDocument.Parse("""{"type":"object","properties":{"message":{"type":"string"}}}""").RootElement.Clone(),
                    CapabilityRisk.Read,
                    false),
            ],
            [], []));
        world.Supervisor.RaiseChanged(world.Server, UpstreamChangeKind.InventoryChanged);
        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = "hi" }), ct))
            .Status.Should().Be(InvocationStatus.Success);

        for (var round = 0; round < 3 && oldSchema.IsAlive; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        oldSchema.IsAlive.Should().BeFalse("das Schema des alten Katalogeintrags darf nicht festgehalten werden");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> BuildThroughInvokerAsync(InvokerTestWorld world, IdentityId admin, CancellationToken ct)
    {
        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = "hi" }), ct))
            .Status.Should().Be(InvocationStatus.Success);
        ToolInvoker.CompiledSchemas.TryGetValue(world.Catalog.Find(world.Echo)!, out var schema).Should().BeTrue();
        return new WeakReference(schema);
    }

    /// <summary>
    /// Sechste Runde, Befund 3: Ein Verweis auf oberster Ebene wird von jedem Aufruf erreicht. Das
    /// Werkzeug ist dann nicht nutzbar — gewollt, denn sein Schema lässt sich nie auswerten —, und
    /// das Log nennt den Grund einmal statt je Aufruf.
    /// </summary>
    [Fact]
    public async Task A_reference_every_call_reaches_rejects_every_call_and_logs_once()
    {
        var world = new InvokerTestWorld(echoSchema: """{"$ref":"https://other.invalid/root"}""");
        var admin = world.RegisterAdmin();
        var log = new CapturingLogger();
        var invoker = world.WithLogger(log);
        var ct = TestContext.Current.CancellationToken;

        for (var call = 0; call < 10; call++)
        {
            var result = await invoker.InvokeAsync(
                InvokerTestWorld.Request(admin, world.Echo, call % 2 == 0 ? new { message = "hi" } : null), ct);
            result.Status.Should().Be(InvocationStatus.ValidationFailed);
            result.ErrorMessage.Should().Contain("https://other.invalid/root", "der Aufrufer erfährt, welcher Verweis fehlt");
        }

        log.Lines.Should().ContainSingle().Which.Should().Contain("https://other.invalid/root");
    }

    /// <summary>
    /// Sechste Runde, Befunde 3 und 4: Jeder Invoker schreibt seine eigene Zeile — für den
    /// Auswertungsfehler wie für das Schema, das sich nicht bauen lässt. Vorher sah ein zweiter
    /// Invoker den Baufehler nie, weil der erste ihn prozessweit schon geloggt hatte.
    /// </summary>
    [Fact]
    public async Task Each_invoker_logs_schema_failures_with_its_own_logger()
    {
        var world = new InvokerTestWorld(echoSchema: """{"$ref":"https://other.invalid/root"}""");
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;

        foreach (var log in new[] { new CapturingLogger(), new CapturingLogger() })
        {
            var invoker = world.WithLogger(log);
            for (var call = 0; call < 3; call++)
            {
                await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = "hi" }), ct);
                await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Free, new { }), ct);
            }

            log.Lines.Should().HaveCount(2);
            log.Lines.Should().ContainSingle(line => line.Contains("echo"));
            log.Lines.Should().ContainSingle(line => line.Contains("free"));
        }
    }

    /// <summary>
    /// Review-Befund L1 (fünfte Runde): Zwei verschiedene unauflösbare Verweise im selben Schema
    /// stehen beide im Log — je einmal.
    /// </summary>
    [Fact]
    public async Task Two_different_unresolvable_references_are_logged_once_each()
    {
        const string schema =
            """{"type":"object","properties":{"a":{"$ref":"https://first.invalid/a"},"b":{"$ref":"https://second.invalid/b"}}}""";
        var world = new InvokerTestWorld(echoSchema: schema);
        var admin = world.RegisterAdmin();
        var log = new CapturingLogger();
        var invoker = world.WithLogger(log);
        var ct = TestContext.Current.CancellationToken;

        for (var call = 0; call < 5; call++)
        {
            (await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { a = 1 }), ct))
                .Status.Should().Be(InvocationStatus.ValidationFailed);
            (await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { b = 1 }), ct))
                .Status.Should().Be(InvocationStatus.ValidationFailed);
        }

        log.Lines.Should().HaveCount(2);
        log.Lines.Should().ContainSingle(line => line.Contains("https://first.invalid/a"));
        log.Lines.Should().ContainSingle(line => line.Contains("https://second.invalid/b"));
    }

    /// <summary>
    /// Review-Befund M1 (vierte Runde): Nach einem Katalog-Neubau gilt das neue Schema. Ein kaputtes
    /// Schema des alten Eintrags darf am neuen, strengen Eintrag nichts mehr durchlassen.
    /// </summary>
    [Fact]
    public async Task After_a_catalog_rebuild_the_new_schema_applies()
    {
        var world = new InvokerTestWorld(echoSchema: """{"type":12345}""");
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;

        var before = await world.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct);
        before.Status.Should().Be(InvocationStatus.Success, "R3: das alte Schema baut nicht");

        world.Supervisor.SetServer(world.Server, world.Slug, new UpstreamInventory(
            [
                new ToolDescriptor(
                    "echo",
                    "Echoes a message back.",
                    JsonDocument.Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""")
                        .RootElement.Clone(),
                    CapabilityRisk.Read,
                    false),
            ],
            [], []));
        world.Supervisor.RaiseChanged(world.Server, UpstreamChangeKind.InventoryChanged);

        var after = await world.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct);
        after.Status.Should().Be(InvocationStatus.ValidationFailed, "der neue Eintrag hat ein strenges Schema");
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
