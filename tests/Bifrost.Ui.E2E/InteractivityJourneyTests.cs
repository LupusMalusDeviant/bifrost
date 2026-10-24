using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// Der Weg, der den folgenreichsten Ausfall der Oberfläche abdeckt: Sie <b>sieht</b> benutzbar aus,
/// aber kein Knopf tut etwas, weil der Blazor-Einstiegspunkt fehlt (Badwolf, 2026-07-29).
/// <para>
/// <c>BlazorScriptDeliveryTests</c> prüft die Lieferbedingung im Bauergebnis. Hier wird geprüft, was
/// im Browser ankommt — und das ist ein anderer Satz: Beim ersten Lauf dieses Harness kam die Datei
/// mit Status 200 und <b>null Bytes</b>, weil er gegen das Entwicklungslayout fuhr statt gegen das
/// Veröffentlichungsergebnis. Der Manifest-Test war dabei grün.
/// </para>
/// </summary>
public sealed class InteractivityJourneyTests
{
    [Fact]
    public async Task The_shipped_ui_actually_becomes_interactive_in_a_browser()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(The_shipped_ui_actually_becomes_interactive_in_a_browser), ct);

        await session.LoginAsync();
        await session.Page.GotoAsync("servers");
        await session.WaitForInteractivityAsync("[data-testid=server-slug]");

        // Nicht "die Datei ist im Manifest", sondern "der Browser hat sie bekommen und ausgeführt".
        var script = await session.Gateway.Client.GetAsync("_framework/blazor.web.js", ct);
        script.IsSuccessStatusCode.Should().BeTrue();
        script.Content.Headers.ContentLength.Should().BeGreaterThan(
            0, "eine Auslieferung mit 200 und null Bytes ist genau der stille Ausfall");

        (await session.Page.EvaluateAsync<bool>("() => window.Blazor !== undefined"))
            .Should().BeTrue("ohne den Circuit ist jedes @onclick in der Oberfläche tot");
    }
}
