using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Persistence;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Was der Gateway aus der <b>Antwort</b> eines Clients auf seine Freigabe-Rückfrage macht.
/// <para>
/// <b>Diese Tests gab es zwei Anläufe lang nicht</b> — der Testclient meldete keine
/// Elicitation-Fähigkeit, also nahm der Server immer den Warteschlangen-Pfad, und die Auswertung
/// der Antwort lief in keinem einzigen Test. Drei Fehler sind so bis in den Betrieb gekommen:
/// ein leeres Formular, das der Client selbst ablehnte; ein <c>cancel</c>, das als menschliches
/// Nein verbucht wurde; und zuletzt ein <c>decline</c>, das derselbe Client schickte, ohne dass
/// je ein Formular zu sehen war.
/// </para>
/// <para>
/// <b>Die Regel, die daraus folgt:</b> Nur eine ausdrückliche Zustimmung — <c>accept</c> mit
/// gesetztem Häkchen — ist eine Entscheidung. Alles andere führt zurück in die Warteschlange.
/// Man kann einer Antwort nicht ansehen, ob ein Mensch dahinterstand; nur das eigens gesetzte
/// Häkchen erzeugt kein Automatismus nebenbei.
/// </para>
/// </summary>
public sealed class ElicitationAnswerTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public ElicitationAnswerTests(GatewayFixture gw) => _gw = gw;

    private ApprovalPolicyStore Policy => _gw.Services.GetRequiredService<ApprovalPolicyStore>();

    private IApprovalStore Approvals => _gw.Services.GetRequiredService<IApprovalStore>();

    private async Task<NamespacedToolName> EchoToolAsync()
    {
        var slug = $"ea{Guid.NewGuid():N}"[..10];
        await _gw.AddEchoUpstreamAsync(slug);
        return new NamespacedToolName($"{slug}__echo");
    }

    private static ValueTask<ElicitResult> Answer(string action, bool? approve)
    {
        var result = new ElicitResult { Action = action };
        if (approve is { } tick)
        {
            result.Content = new Dictionary<string, JsonElement>
            {
                ["approve"] = JsonSerializer.SerializeToElement(tick),
            };
        }

        return ValueTask.FromResult(result);
    }

    private async Task<(CallToolResult Result, ApprovalRequest? Request)> CallWithAnswerAsync(
        NamespacedToolName tool, string action, bool? approve)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, apiKey) = await _gw.SeedAdminAsync($"elicit-{Guid.NewGuid():N}");
        await using var client = await _gw.ConnectClientAsync(
            apiKey, (_, _) => Answer(action, approve));

        var result = await client.CallToolAsync(
            tool.Value, new Dictionary<string, object?> { ["message"] = "hallo" },
            cancellationToken: ct);

        var request = (await Approvals.ListAsync(null, ct)).FirstOrDefault(r => r.Tool == tool);
        return (result, request);
    }

    /// <summary>
    /// Ein ausdrückliches Ja lässt genau diesen einen Aufruf durch — und die Anfrage endet als
    /// <b>eingelöst</b>.
    /// <para>
    /// Der Zustand ist hier kein Beiwerk: Dieser Test war der erste, der den Weg bis zur
    /// Ausführung ging, und hat dabei aufgedeckt, dass ein fertiger Vorgang in der
    /// Freigabe-Ansicht als <c>Denied</c> gemeldet wurde — ein freigegebener Aufruf sah im Audit
    /// aus wie ein abgelehnter.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_explicit_yes_lets_the_call_through()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync();
        await Policy.SetAsync(tool, required: true, ct);
        try
        {
            var (result, request) = await CallWithAnswerAsync(tool, "accept", approve: true);

            result.IsError.Should().NotBe(true);
            request!.State.Should().Be(ApprovalState.Consumed,
                "freigegeben und ausgefuehrt — nicht abgelehnt");
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// <c>decline</c> ist <b>keine</b> Entscheidung. Der Client kann das ohne Zutun eines Menschen
    /// schicken — nachgewiesen am 2026-07-30 im Betrieb, wo der Mensch ausschließlich die
    /// Berechtigungsfrage seines Clients bestätigt und nie ein Formular gesehen hat. Die Anfrage
    /// muss wartend bleiben, damit ein Mensch sie noch entscheiden kann.
    /// </summary>
    [Fact]
    public async Task A_decline_does_not_count_as_a_human_no()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync();
        await Policy.SetAsync(tool, required: true, ct);
        try
        {
            var (result, request) = await CallWithAnswerAsync(tool, "decline", approve: null);

            result.IsError.Should().BeTrue();
            request!.State.Should().Be(ApprovalState.Pending,
                "eine Ablehnung, die niemand ausgesprochen hat, darf nicht im Audit stehen");
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// <c>cancel</c> ebenso — das war der Fund davor, mit derselben Ursache.
    /// </summary>
    [Fact]
    public async Task A_cancel_leaves_the_request_waiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync();
        await Policy.SetAsync(tool, required: true, ct);
        try
        {
            var (result, request) = await CallWithAnswerAsync(tool, "cancel", approve: null);

            result.IsError.Should().BeTrue();
            request!.State.Should().Be(ApprovalState.Pending);
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// Ein <c>accept</c> <b>ohne</b> gesetztes Häkchen ist kein Ja. Das ist der gefährlichste der
    /// vier Fälle: Hier sagt der Client „angenommen", und nur das leere Feld verrät, dass niemand
    /// zugestimmt hat. Würde allein die Aktion ausgewertet, wäre die Freigabepflicht erledigt.
    /// </summary>
    [Fact]
    public async Task An_accept_without_the_tick_is_not_a_yes()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync();
        await Policy.SetAsync(tool, required: true, ct);
        try
        {
            var (result, request) = await CallWithAnswerAsync(tool, "accept", approve: false);

            result.IsError.Should().BeTrue();
            request!.State.Should().Be(ApprovalState.Pending);
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }

    /// <summary>
    /// Ein leeres <c>accept</c> ohne jeden Inhalt — die Form, in der ein Client antwortet, der das
    /// Formular gar nicht dargestellt hat.
    /// </summary>
    [Fact]
    public async Task An_accept_without_any_content_is_not_a_yes()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = await EchoToolAsync();
        await Policy.SetAsync(tool, required: true, ct);
        try
        {
            var (result, request) = await CallWithAnswerAsync(tool, "accept", approve: null);

            result.IsError.Should().BeTrue();
            request!.State.Should().Be(ApprovalState.Pending);
        }
        finally
        {
            await Policy.SetAsync(tool, null, ct);
        }
    }
}
