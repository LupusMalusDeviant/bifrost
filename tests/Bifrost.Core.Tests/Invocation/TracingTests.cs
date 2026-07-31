using System.Diagnostics;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Invocation;
using Xunit;

namespace Bifrost.Core.Tests.Invocation;

/// <summary>
/// Traces zum Invocation-Pfad (FR-26). Metriken beantworten „wie viele und wie schnell im Mittel",
/// Traces beantworten „wo ist die Zeit dieses einen Aufrufs geblieben" — der Kind-Span um den
/// Upstream-Aufruf trennt Gateway-Anteil vom Fremdanteil.
/// <para>
/// Der wichtigste Test dieser Datei ist der letzte: <b>In Spans stehen keine Argumente und keine
/// Ergebnisse.</b> Das Audit-Log ist redigiert, ein Telemetrie-Backend ist es nicht — ein Payload im
/// Span wäre der bequemste Weg, die Redaction zu umgehen.
/// </para>
/// </summary>
public sealed class TracingTests
{
    /// <summary>
    /// Sammelt die Spans <b>dieses</b> Aufrufs. Ein ActivityListener ist prozessweit — bei
    /// paralleler Testausführung fängt er auch Spans anderer Tests. Deshalb wird am Ende auf den
    /// Trace gefiltert, der zum Slug dieser Welt gehört; sonst wäre der Test flatterig und würde
    /// je nach Auslastung mal grün, mal rot.
    /// </summary>
    private static async Task<(List<Activity> Spans, InvokerTestWorld World)> CollectAsync(
        Func<InvokerTestWorld, Task> act)
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ToolInvoker.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (captured)
                {
                    captured.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var world = new InvokerTestWorld();
        await act(world);

        lock (captured)
        {
            var own = captured.FirstOrDefault(a =>
                a.OperationName == "bifrost.tool_call"
                && a.GetTagItem("bifrost.tool") is string tool
                && tool.StartsWith(world.Slug, StringComparison.Ordinal));
            return own is null
                ? ([], world)
                : ([.. captured.Where(a => a.TraceId == own.TraceId)], world);
        }
    }

    [Fact]
    public async Task A_successful_call_produces_a_span_with_tool_status_and_origin()
    {
        var (activities, world) = await CollectAsync(async w => await w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(w.RegisterAdmin(), w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken));

        var call = activities.Should().ContainSingle(a => a.OperationName == "bifrost.tool_call").Subject;
        call.GetTagItem("bifrost.tool").Should().Be(world.Echo.Value);
        call.GetTagItem("bifrost.server").Should().Be(world.Slug);
        call.GetTagItem("bifrost.status").Should().Be(nameof(InvocationStatus.Success));
        call.GetTagItem("bifrost.origin").Should().NotBeNull();
        call.Status.Should().Be(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Der eigentliche Zweck: Der Fremdanteil ist als eigener Span sichtbar und hängt unter dem
    /// Aufruf-Span. Ohne diese Trennung sieht man in einer langsamen Antwort nicht, wer sie
    /// verursacht hat.
    /// </summary>
    [Fact]
    public async Task The_upstream_call_gets_its_own_child_span()
    {
        var (activities, _) = await CollectAsync(async w => await w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(w.RegisterAdmin(), w.Echo, new { message = "hi" }),
            TestContext.Current.CancellationToken));

        var call = activities.Single(a => a.OperationName == "bifrost.tool_call");
        var upstream = activities.Should().ContainSingle(a => a.OperationName == "bifrost.upstream_call").Subject;

        upstream.ParentSpanId.Should().Be(call.SpanId, "der Fremdanteil gehört unter den Aufruf");
        upstream.Kind.Should().Be(ActivityKind.Client);
        upstream.GetTagItem("bifrost.upstream_tool").Should().Be("echo",
            "im Kind-Span steht der native Name, nicht der namespaced");
    }

    /// <summary>
    /// Ein abgelehnter Aufruf ist kein Serverfehler, aber auch kein gelungener Aufruf. Als Error
    /// markiert taucht er in jeder Fehlersuche auf — und genau dort will man ihn haben.
    /// </summary>
    [Fact]
    public async Task A_denied_call_is_marked_as_an_error_span()
    {
        var (activities, _) = await CollectAsync(async w =>
        {
            // Eine Identität ohne jeden Grant: Default-Deny greift vor dem Upstream.
            var ohneGrant = w.RegisterAgent();
            await w.Invoker.InvokeAsync(
                InvokerTestWorld.Request(ohneGrant, w.Echo, new { message = "hi" }),
                TestContext.Current.CancellationToken);
        });

        var call = activities.Single(a => a.OperationName == "bifrost.tool_call");
        call.GetTagItem("bifrost.status").Should().Be(nameof(InvocationStatus.Denied));
        call.Status.Should().Be(ActivityStatusCode.Error);
        activities.Should().NotContain(a => a.OperationName == "bifrost.upstream_call",
            "was abgelehnt wurde, hat den Upstream nie erreicht");
    }

    /// <summary>
    /// Der wichtigste Fall: Ein Secret in den Argumenten darf in <b>keinem</b> Span-Feld auftauchen —
    /// weder in Tags noch in Events noch in der Statusbeschreibung.
    /// </summary>
    [Fact]
    public async Task No_span_carries_arguments_or_results()
    {
        const string Secret = "sk-streng-geheim-4711";

        var (activities, _) = await CollectAsync(async w => await w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(w.RegisterAdmin(), w.Echo, new { message = Secret }),
            TestContext.Current.CancellationToken));

        activities.Should().NotBeEmpty();
        foreach (var activity in activities)
        {
            var material = string.Join(
                '\n',
                [
                    activity.DisplayName,
                    activity.StatusDescription ?? string.Empty,
                    .. activity.TagObjects.Select(t => $"{t.Key}={t.Value}"),
                    .. activity.Events.SelectMany(e =>
                        e.Tags.Select(t => $"{t.Key}={t.Value}").Prepend(e.Name)),
                ]);

            material.Should().NotContain(Secret,
                "Telemetrie läuft an der Redaction vorbei — Payloads gehören dort nicht hinein");
            material.Should().NotContain("message",
                "auch der Feldname eines Arguments hat im Span nichts verloren");
        }
    }
}
