using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP5.5: Der Freigabeweg über die Oberfläche — <b>die Kernzusage von FR-32</b>.
/// <para>
/// Ein Agent ruft ein scharfgestelltes Werkzeug auf und wird abgewiesen. Ein Betreiber sieht die
/// Anfrage in der Warteschlange, gibt sie frei, und erst danach geht derselbe Aufruf durch. Der Weg
/// ist deshalb der wichtigste der offenen fünf: Wenn er nicht hält, führt der Gateway Werkzeuge
/// aus, über die niemand entschieden hat.
/// </para>
/// <para>
/// <b>Kein Abkürzen über die API.</b> Die Entscheidung fällt im Browser, im ausgelieferten
/// Formular. Ein Test, der stattdessen den Freigabe-Endpunkt riefe, prüfte die Warteschlange — nicht
/// den Weg, den ein Mensch geht.
/// </para>
/// </summary>
public sealed class ApprovalJourneyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _output;

    public ApprovalJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_sharp_tool_waits_for_a_human_and_runs_only_after_the_click()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(A_sharp_tool_waits_for_a_human_and_runs_only_after_the_click), ct);

        const string slug = "echoapproval";
        const string tool = slug + "__echo";
        await session.Gateway.AddStdioUpstreamAsync(slug, "EchoServer", ct);

        // Scharfstellen: ab hier braucht dieses Werkzeug eine menschliche Entscheidung.
        var sharpened = await session.Gateway.Client.PostAsJsonAsync(
            "api/v1/approvals/tools",
            new { tool, required = true, mode = "Queue" },
            Json,
            ct);
        sharpened.IsSuccessStatusCode.Should().BeTrue(
            await sharpened.Content.ReadAsStringAsync(ct));

        // **Erster Aufruf — und er darf NICHT durchgehen.**
        var arguments = new { message = "warten bitte" };
        var abgewiesen = await InvokeAsync(session, tool, arguments, ct);
        abgewiesen.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "ein scharfgestelltes Werkzeug laeuft nicht, bevor jemand entschieden hat — Antwort: "
            + await abgewiesen.Content.ReadAsStringAsync(ct));
        _output.WriteLine($"Erster Aufruf: {abgewiesen.StatusCode}");

        // Der Betreiber sieht die Anfrage — im Browser, in der Warteschlange.
        await session.LoginAsync();
        await session.Page.GotoAsync("approvals");
        await session.WaitForInteractivityAsync("h1");

        var row = session.Page.Locator($"[data-testid=approval-row][data-tool='{tool}']");
        await Assertions.Expect(row).ToHaveCountAsync(
            1, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });

        // Die maskierten Argumente stehen daneben — ohne sie entscheidet man ueber eine Zeile
        // Text, nicht ueber einen Vorgang.
        (await row.TextContentAsync()).Should().Contain("warten bitte");

        await row.Locator("[data-testid=approval-grant]").ClickAsync();
        await Assertions.Expect(row).ToHaveCountAsync(
            0, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });

        // **Zweiter Aufruf, gleiche Identitaet, gleiche Argumente — jetzt geht er durch.**
        var durch = await InvokeAsync(session, tool, arguments, ct);
        durch.StatusCode.Should().Be(HttpStatusCode.OK,
            "nach der Freigabe laeuft genau dieser Aufruf");
        (await durch.Content.ReadAsStringAsync(ct)).Should().Contain("warten bitte");
    }

    /// <summary>
    /// Die Kehrseite, und ohne sie prüfte der Weg oben nur den guten Tag: <b>Eine Ablehnung lässt
    /// den Aufruf abgewiesen.</b> Ein Freigabeweg, bei dem „Ablehnen" dieselbe Wirkung hat wie
    /// „Freigeben", wäre schlimmer als gar keiner.
    /// </summary>
    [Fact]
    public async Task A_denied_request_does_not_let_the_call_through()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(A_denied_request_does_not_let_the_call_through), ct);

        const string slug = "echodeny";
        const string tool = slug + "__echo";
        await session.Gateway.AddStdioUpstreamAsync(slug, "EchoServer", ct);

        var sharpened = await session.Gateway.Client.PostAsJsonAsync(
            "api/v1/approvals/tools", new { tool, required = true, mode = "Queue" }, Json, ct);
        sharpened.IsSuccessStatusCode.Should().BeTrue();

        var arguments = new { message = "besser nicht" };
        (await InvokeAsync(session, tool, arguments, ct)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        await session.LoginAsync();
        await session.Page.GotoAsync("approvals");
        await session.WaitForInteractivityAsync("h1");

        var row = session.Page.Locator($"[data-testid=approval-row][data-tool='{tool}']");
        await Assertions.Expect(row).ToHaveCountAsync(
            1, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });
        await row.Locator("[data-testid=approval-deny]").ClickAsync();
        await Assertions.Expect(row).ToHaveCountAsync(
            0, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });

        var erneut = await InvokeAsync(session, tool, arguments, ct);
        erneut.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "eine Ablehnung ist eine Entscheidung, kein Durchwinken");
        _output.WriteLine($"Nach der Ablehnung: {erneut.StatusCode}");
    }

    private static Task<HttpResponseMessage> InvokeAsync(
        UiSession session, string tool, object arguments, CancellationToken ct)
        => session.Gateway.Client.PostAsJsonAsync($"api/v1/tools/{tool}/invoke", arguments, Json, ct);
}
