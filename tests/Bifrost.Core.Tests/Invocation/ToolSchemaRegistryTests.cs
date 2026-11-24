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
    /// Siebte Runde M1, achte Runde M-A: Nur Ausfälle des Augenblicks gelten als vorübergehend und
    /// werden nicht gemerkt; jeder andere Baufehler — auch eine Art, die die Bibliothek nicht als
    /// <see cref="JsonSchemaException"/> meldet — kehrt wieder und nimmt den R3-Weg. Geprüft an der
    /// Einstufung selbst: Einen echten Stack- oder Speicherausfall löst kein Schematext aus, und eine
    /// Einspeisestelle nur für Tests gehört nicht ins Produkt (achte Runde, M-B).
    /// </summary>
    [Fact]
    public void Only_failures_of_the_moment_count_as_transient()
    {
        ToolInvoker.IsTransientBuildFailure(new InsufficientExecutionStackException()).Should().BeTrue();
        ToolInvoker.IsTransientBuildFailure(new InsufficientMemoryException()).Should().BeTrue("abgeleitet von OutOfMemoryException");
        ToolInvoker.IsTransientBuildFailure(new OperationCanceledException()).Should().BeTrue();
        ToolInvoker.IsTransientBuildFailure(new TaskCanceledException()).Should().BeTrue();
        ToolInvoker.IsTransientBuildFailure(new ThreadInterruptedException()).Should().BeTrue();

        ToolInvoker.IsTransientBuildFailure(new JsonSchemaException("kaputt")).Should().BeFalse();
        ToolInvoker.IsTransientBuildFailure(new UriFormatException()).Should().BeFalse();
        ToolInvoker.IsTransientBuildFailure(new InvalidOperationException()).Should().BeFalse();
        ToolInvoker.IsTransientBuildFailure(new KeyNotFoundException()).Should().BeFalse();
        ToolInvoker.IsTransientBuildFailure(new NotSupportedException()).Should().BeFalse();
    }

    private const string Strict =
        """{"type":"object","required":["message"],"properties":{"message":{"type":"string"}}}""";

    /// <summary>
    /// Zehnte Runde, M-1: Ein einzelner Baufehler, der nicht wiederkehrt (etwa ein Wettlauf in der
    /// Bibliothek), macht das Schema nicht „nicht baubar". Der zweite Bau gelingt, sein Schema zählt,
    /// nichts wird gemerkt.
    /// </summary>
    [Fact]
    public void A_build_failure_that_does_not_repeat_is_not_remembered()
    {
        var entry = Entry(Strict);
        var calls = 0;

        var outcome = ToolInvoker.ObtainSchema(entry, e => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("Wettlauf")
            : ToolInvoker.BuildSchema(e));

        outcome.Schema.Should().NotBeNull();
        outcome.Interrupted.Should().BeNull();
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse();
        outcome.Schema!.Evaluate(JsonDocument.Parse("""{"message":1}""").RootElement).IsValid.Should().BeFalse();
    }

    /// <summary>Zehnte Runde, M-1: Dieselbe Ausnahme zweimal — bestätigt, nicht baubar, R3.</summary>
    [Fact]
    public void A_build_failure_that_repeats_marks_the_schema_unusable()
    {
        var entry = Entry(Strict);
        var calls = 0;

        var outcome = ToolInvoker.ObtainSchema(entry, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new UriFormatException("kaputtes $id");
        });

        outcome.Schema.Should().BeNull();
        outcome.Interrupted.Should().BeNull("R3, kein Ausfall");
        calls.Should().Be(2, "ein Bau zur Bestätigung, nicht mehr");
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out var reason).Should().BeTrue();
        reason!.Reason.Should().Be("kaputtes $id");
    }

    /// <summary>
    /// Zehnte Runde, M-1: Zwei verschiedene Ausnahmen — kein Fehler des Schematexts, sondern einer
    /// des Augenblicks. Abgewiesen, nicht gemerkt.
    /// </summary>
    [Fact]
    public void Two_different_build_failures_are_an_interruption()
    {
        var entry = Entry(Strict);
        var calls = 0;

        var outcome = ToolInvoker.ObtainSchema(entry, _ => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("erst das")
            : throw new KeyNotFoundException("dann das"));

        outcome.Schema.Should().BeNull();
        outcome.Interrupted.Should().BeOfType<KeyNotFoundException>();
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse();
    }

    /// <summary>
    /// Elfte Runde, M-1: Der Wettlauf, den die Bestätigung nicht allein verhindert. Aufruf B baut
    /// noch (und wird es schaffen), während Aufruf A zweimal mit derselben Ausnahme scheitert und das
    /// Schema als nicht baubar markiert. Danach legt B sein Schema ab. Am Ende darf keine Markierung
    /// übrig sein, und ein ungültiger Aufruf über den Invoker muss abgewiesen werden — sonst gingen
    /// alle folgenden Aufrufe ungeprüft durch, obwohl ein gültiges Schema im Zwischenspeicher liegt.
    /// </summary>
    [Fact]
    public async Task A_schema_built_concurrently_wins_over_a_confirmed_failure_of_another_call()
    {
        var world = new InvokerTestWorld();
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;
        var entry = world.Catalog.Find(world.Echo)!;
        using var bStarted = new ManualResetEventSlim(false);
        using var releaseB = new ManualResetEventSlim(false);

        var b = Task.Run(() => ToolInvoker.ObtainSchema(entry, e =>
        {
            bStarted.Set();
            releaseB.Wait(ct);
            return ToolInvoker.BuildSchema(e);
        }), ct);
        bStarted.Wait(ct);

        var a = ToolInvoker.ObtainSchema(entry, _ => throw new InvalidOperationException("Wettlauf"));
        a.Schema.Should().BeNull("B liegt noch nicht im Zwischenspeicher");
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeTrue();

        releaseB.Set();
        var outcomeB = await b;
        outcomeB.Schema.Should().NotBeNull();

        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse(
            "der gelungene Bau nimmt die Markierung des anderen Aufrufs zurück");
        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct))
            .Status.Should().Be(InvocationStatus.ValidationFailed, "das gebaute Schema prüft");
    }

    /// <summary>
    /// Elfte Runde, M-1, andere Reihenfolge: Hat B sein Schema schon abgelegt, während A noch
    /// bestätigt, nimmt A dessen Schema, statt zu markieren. Und selbst eine stehen gebliebene
    /// Markierung schaltet die Prüfung nicht ab, solange ein gebautes Schema da ist.
    /// </summary>
    [Fact]
    public async Task A_built_schema_takes_precedence_over_a_mark()
    {
        var world = new InvokerTestWorld();
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;
        var entry = world.Catalog.Find(world.Echo)!;

        var calls = 0;
        var a = ToolInvoker.ObtainSchema(entry, e =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // Zwischen dem ersten und dem Bestätigungsbau legt ein anderer Aufruf ab.
                ToolInvoker.ObtainSchema(e, ToolInvoker.BuildSchema).Schema.Should().NotBeNull();
                throw new InvalidOperationException("Wettlauf");
            }

            throw new InvalidOperationException("Wettlauf");
        });
        a.Schema.Should().NotBeNull("der Bestätigungsbau bekommt das Schema des anderen Aufrufs aus dem Zwischenspeicher");
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse();

        // Eine Markierung, die trotzdem stehen bliebe, darf die Prüfung nicht abschalten.
        ToolInvoker.UnusableSchemas.TryAdd(entry, new ToolInvoker.UnusableMark("stehen geblieben"));
        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct))
            .Status.Should().Be(InvocationStatus.ValidationFailed);
    }

    /// <summary>
    /// Zwölfte Runde, L-2: Der Weg nach dem Markieren. Während des <i>Bestätigungs</i>baus legt ein
    /// anderer Aufruf das Schema ab, und der Bestätigungsbau wirft trotzdem dieselbe Art. Dann wird
    /// markiert, sofort danach das abgelegte Schema gefunden, die Markierung zurückgenommen und
    /// dessen Schema genommen. Ohne diesen Weg bliebe die Markierung stehen.
    /// </summary>
    [Fact]
    public void A_schema_stored_during_the_confirmation_build_is_found_after_marking()
    {
        var entry = Entry(Strict);
        var calls = 0;

        var outcome = ToolInvoker.ObtainSchema(entry, e =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                // Im Bestätigungsbau: ein anderer Aufruf legt ab — dieser Bau wirft dennoch.
                ToolInvoker.CompiledSchemas.GetValue(e, ToolInvoker.BuildSchema);
            }

            throw new InvalidOperationException("Wettlauf");
        });

        calls.Should().Be(2);
        outcome.Schema.Should().NotBeNull();
        outcome.Schema.Should().BeSameAs(ToolInvoker.CompiledSchemas.TryGetValue(entry, out var stored) ? stored : null);
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse();
    }

    /// <summary>
    /// Zwölfte Runde, M-1: Zweimal dieselbe Art mit verschiedener Meldung ist bestätigt nicht
    /// baubar. Die Bibliothek kann je Bau eine andere Meldung liefern; verglichen mit Meldung
    /// galte ein nicht baubares Schema als Ausfall und wäre dauerhaft gesperrt.
    /// </summary>
    [Fact]
    public void Two_failures_of_the_same_type_with_different_messages_confirm_an_unusable_schema()
    {
        var entry = Entry(Strict);
        var calls = 0;

        var outcome = ToolInvoker.ObtainSchema(entry, _ =>
            throw new JsonSchemaException($"Basis-Adresse https://json-everything.lib/{Interlocked.Increment(ref calls)}"));

        outcome.Schema.Should().BeNull();
        outcome.Interrupted.Should().BeNull("R3, kein Ausfall");
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeTrue();
    }

    /// <summary>
    /// Zwölfte Runde, L-1: Eine Markierung heilt sich selbst. Steht sie auf einem Schema, das sich
    /// sehr wohl bauen lässt (ein zweimal gleich ausgefallener Fehler des Augenblicks), gilt R3 nur
    /// bis zum nächsten erneuten Bauversuch; danach wird wieder geprüft und die Markierung fällt.
    /// </summary>
    [Fact]
    public async Task A_mark_on_a_buildable_schema_heals_after_the_retry_interval()
    {
        var world = new InvokerTestWorld();
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;
        var entry = world.Catalog.Find(world.Echo)!;
        ToolInvoker.UnusableSchemas.TryAdd(entry, new ToolInvoker.UnusableMark("zweimal derselbe Wettlauf"));

        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct))
            .Status.Should().Be(InvocationStatus.Success, "R3, solange die Markierung gilt");

        world.Time.Advance(ToolInvoker.UnusableRetryInterval - TimeSpan.FromSeconds(1));
        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct))
            .Status.Should().Be(InvocationStatus.Success, "vor Ablauf des Intervalls wird nicht neu gebaut");

        world.Time.Advance(TimeSpan.FromSeconds(2));
        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct))
            .Status.Should().Be(InvocationStatus.ValidationFailed, "der erneute Bau gelingt, das Schema prüft");
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse();
    }

    /// <summary>Zwölfte Runde, L-1: Nur ein Aufruf je Intervall übernimmt den erneuten Bauversuch.</summary>
    [Fact]
    public void Only_one_caller_per_interval_claims_the_retry()
    {
        var mark = new ToolInvoker.UnusableMark("x");
        var start = DateTimeOffset.UnixEpoch;
        var interval = TimeSpan.FromSeconds(30);

        mark.TryClaimRetry(start, interval).Should().BeFalse("der erste Blick startet nur die Uhr");
        mark.TryClaimRetry(start + TimeSpan.FromSeconds(29), interval).Should().BeFalse();

        var later = start + TimeSpan.FromSeconds(31);
        var claims = Enumerable.Range(0, 64).AsParallel().Count(_ => mark.TryClaimRetry(later, interval));
        claims.Should().Be(1);
    }

    /// <summary>
    /// Elfte Runde L-3, zwölfte Runde L-3: Ein unterbrochener Bau ist kein Urteil über die
    /// Argumente. Er endet als Fehler des Gateways (<see cref="InvocationStatus.UpstreamError"/>,
    /// <c>fault=gateway</c>), nicht als <see cref="InvocationStatus.ValidationFailed"/> — und
    /// nicht als Durchlass. Eingespeist über den Bau, den <c>ValidateArguments</c> übergeben bekommt.
    /// </summary>
    [Fact]
    public void An_interrupted_build_is_a_gateway_fault_and_not_a_pass()
    {
        var world = new InvokerTestWorld();
        var entry = world.Catalog.Find(world.Echo)!;
        var args = JsonDocument.Parse("""{"message":"hi"}""").RootElement;

        var rejection = world.Invoker.ValidateArguments(entry, args, _ => throw new InsufficientExecutionStackException());

        rejection.Should().NotBeNull("ein unterbrochener Bau darf den Aufruf nicht ungeprüft durchlassen");
        rejection!.Value.Status.Should().Be(InvocationStatus.UpstreamError);
        rejection.Value.GatewayFault.Should().BeTrue();
        rejection.Value.Message.Should().Contain(nameof(InsufficientExecutionStackException));
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_invalid_call_is_still_a_validation_failure()
    {
        var world = new InvokerTestWorld();
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;

        var result = await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct);
        result.Status.Should().Be(InvocationStatus.ValidationFailed);
        result.ErrorMessage.Should().StartWith("Argument-Validierung fehlgeschlagen");
    }

    /// <summary>Ein vorübergehender Fehler wird nicht bestätigt, sondern gleich als Ausfall gewertet.</summary>
    [Fact]
    public void A_transient_build_failure_is_an_interruption_without_a_second_build()
    {
        var entry = Entry(Strict);
        var calls = 0;

        var outcome = ToolInvoker.ObtainSchema(entry, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InsufficientExecutionStackException();
        });

        outcome.Interrupted.Should().BeOfType<InsufficientExecutionStackException>();
        calls.Should().Be(1);
        ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse();
    }

    /// <summary>
    /// Zehnte Runde, Testlücke 1: Gleichzeitige <i>erste</i> Aufrufe über den Invoker. Dort lässt
    /// <c>ConditionalWeakTable.GetValue</c> den Bau mehrfach laufen. Jedes Urteil muss stimmen,
    /// danach liegt genau ein Schema im Zwischenspeicher, und nichts ist als nicht baubar gemerkt.
    /// Mehrere Runden mit frischer Welt, damit der Wettlauf tatsächlich beim ersten Bau stattfindet.
    /// </summary>
    [Fact]
    public async Task Concurrent_first_calls_are_judged_correctly()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 10; round++)
        {
            var world = new InvokerTestWorld();
            var admin = world.RegisterAdmin();
            using var start = new ManualResetEventSlim(false);

            var calls = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
            {
                start.Wait(ct);
                var valid = i % 2 == 0;
                var result = await world.Invoker.InvokeAsync(
                    InvokerTestWorld.Request(admin, world.Echo, valid ? new { message = (object)"hi" } : new { message = (object)1 }), ct);
                return (valid, result.Status);
            }, ct)).ToList();
            start.Set();

            foreach (var (valid, status) in await Task.WhenAll(calls))
            {
                status.Should().Be(valid ? InvocationStatus.Success : InvocationStatus.ValidationFailed);
            }

            var entry = world.Catalog.Find(world.Echo)!;
            ToolInvoker.CompiledSchemas.TryGetValue(entry, out _).Should().BeTrue();
            ToolInvoker.UnusableSchemas.TryGetValue(entry, out _).Should().BeFalse();
        }
    }

    /// <summary>
    /// Zehnte Runde, Testlücke 2: Parallele Auswertung eines geteilten Schemas mit
    /// <c>unevaluatedProperties</c> — einem Schlüsselwort, das an Annotationen anderer Teilschemata
    /// hängt — und <c>pattern</c>. Jedes Urteil muss stimmen.
    /// </summary>
    [Fact]
    public void A_shared_schema_with_unevaluated_properties_judges_correctly_under_concurrent_evaluation()
    {
        var schema = ToolInvoker.BuildSchema(Entry("""
            {
              "type": "object",
              "allOf": [ { "properties": { "code": { "type": "string", "pattern": "^[A-Z]{3}$" } } } ],
              "properties": { "n": { "type": "integer" } },
              "unevaluatedProperties": false
            }
            """));
        var valid = JsonDocument.Parse("""{"code":"ABC","n":1}""").RootElement;
        var extra = JsonDocument.Parse("""{"code":"ABC","n":1,"x":true}""").RootElement;
        var badPattern = JsonDocument.Parse("""{"code":"abc","n":1}""").RootElement;

        var wrong = 0;
        Parallel.For(0, 9_000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            var (instance, expected) = (i % 3) switch
            {
                0 => (valid, true),
                1 => (extra, false),
                _ => (badPattern, false),
            };
            if (schema.Evaluate(instance).IsValid != expected)
            {
                Interlocked.Increment(ref wrong);
            }
        });

        wrong.Should().Be(0);
    }

    /// <summary>
    /// Neunte Runde, L-5: Ein fehlender <i>lokaler</i> Zeiger (<c>#/$defs/missing</c>). JsonSchema.Net
    /// 9.3 baut das Schema und wirft erst beim Auswerten — der Aufruf, der den Zeiger erreicht, wird
    /// abgewiesen, einer ohne ihn geprüft. Verschöbe eine künftige Fassung das Auflösen ins Bauen,
    /// nähme das ganze Werkzeug den R3-Weg; dieser Test fiele dann auf.
    /// </summary>
    [Fact]
    public async Task A_missing_local_pointer_is_resolved_at_evaluation_and_rejects_the_call()
    {
        const string schema =
            """{"type":"object","required":["id"],"properties":{"id":{"type":"integer"},"n":{"$ref":"#/$defs/missing"}}}""";
        var world = new InvokerTestWorld(echoSchema: schema);
        var admin = world.RegisterAdmin();
        var ct = TestContext.Current.CancellationToken;

        var build = () => ToolInvoker.BuildSchema(world.Catalog.Find(world.Echo)!);
        build.Should().NotThrow("der fehlende Zeiger fällt erst beim Auswerten auf");

        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { id = 1, n = 1 }), ct))
            .Status.Should().Be(InvocationStatus.ValidationFailed);
        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { id = "abc" }), ct))
            .Status.Should().Be(InvocationStatus.ValidationFailed);
        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { id = 1 }), ct))
            .Status.Should().Be(InvocationStatus.Success);
        ToolInvoker.UnusableSchemas.TryGetValue(world.Catalog.Find(world.Echo)!, out _).Should().BeFalse();
    }

    /// <summary>
    /// Achte Runde, M-A: Ein kaputtes <c>$id</c> lässt das Schema nicht bauen. Gleich welche
    /// Ausnahme die Bibliothek dafür wirft — das Werkzeug wird nicht dauerhaft gesperrt und nicht
    /// je Aufruf neu gebaut, sondern geht wie jedes nicht baubare Schema über R3.
    /// </summary>
    [Fact]
    public async Task A_schema_with_a_broken_id_takes_the_R3_path()
    {
        var world = new InvokerTestWorld(
            echoSchema: """{"$id":"http://[::1","type":"object","required":["message"],"properties":{"message":{"type":"string"}}}""");
        var admin = world.RegisterAdmin();
        var log = new CapturingLogger();
        var invoker = world.WithLogger(log);
        var ct = TestContext.Current.CancellationToken;

        var thrown = () => ToolInvoker.BuildSchema(world.Catalog.Find(world.Echo)!);
        thrown.Should().Throw<Exception>("sonst bewiese der Test nichts über Baufehler")
            .Which.Should().Match<Exception>(e => !ToolInvoker.IsTransientBuildFailure(e));

        for (var call = 0; call < 5; call++)
        {
            (await invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = 1 }), ct))
                .Status.Should().Be(InvocationStatus.Success, "R3: ein Schema, das nicht baut, lässt durch");
        }

        log.Lines.Should().ContainSingle();
        ToolInvoker.UnusableSchemas.TryGetValue(world.Catalog.Find(world.Echo)!, out _).Should().BeTrue();
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

        (await world.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, world.Echo, new { message = "hi" }), ct))
            .Status.Should().Be(InvocationStatus.Success);
        var oldSchema = WeakSchemaOf(world);

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

    /// <summary>
    /// Synchron und nicht eingebettet (Review-Befund L-3, achte Runde): Weder der Katalogeintrag
    /// noch das Schema dürfen als Feld einer async-Zustandsmaschine überleben, sonst hielte der
    /// Test selbst fest, was er freigegeben sehen will.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference WeakSchemaOf(InvokerTestWorld world)
    {
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
