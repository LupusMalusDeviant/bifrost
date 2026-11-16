using AwesomeAssertions;
using Bifrost.Performance.Tests.Harness;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// Der zweite kritische Weg (WP5.5): einen Server anschließen und sehen, dass er läuft. Er geht
/// durch das ausgelieferte Formular, startet einen echten Upstream-Prozess und liest den Zustand
/// aus der Tabelle — nicht aus der API.
/// </summary>
public sealed class ServerJourneyTests
{
    private readonly ITestOutputHelper _output;

    public ServerJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task An_operator_connects_a_server_through_the_form_and_sees_it_healthy()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(An_operator_connects_a_server_through_the_form_and_sees_it_healthy), ct);

        await session.LoginAsync();
        await session.Page.GotoAsync("servers");
        await session.WaitForInteractivityAsync("[data-testid=server-slug]");

        await FillFormAsync(session, "echoui");

        // Erst prüfen, dann anschließen — das ist die Reihenfolge, die die Oberfläche anbietet.
        await session.Page.ClickAsync("button:text-is('Verbindung testen')");
        var message = session.Page.Locator("[data-testid=server-message]");
        await message.WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        _output.WriteLine($"Verbindungstest: {await message.TextContentAsync()}");

        // Genau der Knopf, nicht die Ueberschrift: "Neuen Server anschließen" enthaelt denselben
        // Text, und ein Text-Treffer haette dort geklickt — der Test waere gelaufen, ohne dass
        // irgendetwas passiert.
        await session.Page.ClickAsync("button:text-is('Anschließen')");

        var row = session.Page.Locator("[data-testid=server-row][data-slug=echoui]");
        await row.WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });

        // Die Seite laedt die Zustaende beim Oeffnen und nach einer Aktion — einen Ticker gibt es
        // nicht. Direkt nach dem Anschliessen steht dort deshalb "Starting", und erst ein Neuladen
        // zeigt den Verlauf. Genau so geht ein Betreiber vor; als Beobachtung steht es im
        // Fortschrittsprotokoll (WP5.5), geaendert wird die Oberflaeche hier nicht.
        var state = await WaitForStateAsync(session, "echoui", "Healthy", TimeSpan.FromSeconds(60));
        state.Should().Be("Healthy", "der Echo-Server kommt hoch, sobald der Prozess verbunden ist");

        // Der Zustand steht an der Zeile, nicht irgendwo auf der Seite: Ein Text-Treffer waere auch
        // dann gruen, wenn ein ANDERER Server healthy ist.
        (await row.TextContentAsync()).Should().Contain("1", "der Echo-Server bringt genau ein Werkzeug mit");
    }

    /// <summary>
    /// Die Kehrseite desselben Weges: Ein Kommando, das es nicht gibt, darf nicht als angeschlossen
    /// durchgehen. Ohne diesen Fall prüfte der Weg oben nur den guten Tag.
    /// </summary>
    [Fact]
    public async Task A_server_that_cannot_start_does_not_show_up_as_healthy()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(A_server_that_cannot_start_does_not_show_up_as_healthy), ct);

        await session.LoginAsync();
        await session.Page.GotoAsync("servers");
        await session.WaitForInteractivityAsync("[data-testid=server-slug]");

        await FillFormAsync(session, "kaputt", command: Path.Combine(
            Path.GetDirectoryName(TestServerPaths.GatewayExecutable)!, "gibt-es-nicht.exe"));

        await session.Page.ClickAsync("button:text-is('Verbindung testen')");
        var message = session.Page.Locator("[data-testid=server-message]");
        await message.WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });

        var text = await message.TextContentAsync();
        _output.WriteLine($"Verbindungstest: {text}");
        (await message.GetAttributeAsync("class")).Should().Contain(
            "msg-err", "ein Kommando, das es nicht gibt, ist ein Fehler und keine Erfolgsmeldung");
    }

    /// <summary>
    /// Vertrag „Selbsttätige Wiederverbindung", Abschnitt 5.4: Ein Upstream, der nicht hochkommt,
    /// zeigt in seiner Zeile, der wievielte Versuch es war und wann der nächste folgt — und
    /// „Jetzt verbinden" wartet diese Frist nicht ab.
    /// <para>
    /// Geprüft werden die <c>data-</c>Attribute der Zeile, nicht der Fließtext. Und die Seite wird
    /// nach dem Anschließen <b>nicht</b> neu geladen: Sie aktualisiert die Zeile jetzt selbst.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_failed_server_shows_its_next_attempt_and_can_be_connected_now()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(A_failed_server_shows_its_next_attempt_and_can_be_connected_now), ct);

        await session.LoginAsync();
        await session.Page.GotoAsync("servers");
        await session.WaitForInteractivityAsync("[data-testid=server-slug]");

        await FillFormAsync(session, "weg", command: Path.Combine(
            Path.GetDirectoryName(TestServerPaths.GatewayExecutable)!, "gibt-es-nicht.exe"));
        await session.Page.ClickAsync("button:text-is('Anschließen')");

        const string rowSelector = "[data-testid=server-row][data-slug=weg]";
        var row = session.Page.Locator(rowSelector);
        await row.WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });

        // Bis zum vierten Fehlversuch warten: Danach liegt der nächste planmäßige Versuch mehr als
        // eine halbe Minute entfernt. Nur so beweist ein Versuch kurz nach dem Klick den Knopf —
        // und nicht bloß, dass ohnehin gerade einer fällig war.
        await session.Page.WaitForFunctionAsync(
            """
            selector => {
                const row = document.querySelector(selector);
                return row !== null
                    && row.getAttribute('data-state') === 'Failed'
                    && Number(row.getAttribute('data-reconnect-attempt')) >= 4
                    && (row.getAttribute('data-next-attempt-at') ?? '') !== '';
            }
            """,
            rowSelector,
            new PageWaitForFunctionOptions { Timeout = 120000 });

        (await row.GetAttributeAsync("data-failure-kind")).Should().Be("Unreachable");
        var attempt = int.Parse(
            (await row.GetAttributeAsync("data-reconnect-attempt"))!, System.Globalization.CultureInfo.InvariantCulture);
        var nextAttemptAt = DateTimeOffset.Parse(
            (await row.GetAttributeAsync("data-next-attempt-at"))!, System.Globalization.CultureInfo.InvariantCulture);
        _output.WriteLine($"Versuch {attempt}, nächster planmäßig um {nextAttemptAt:O}.");
        (nextAttemptAt - DateTimeOffset.UtcNow).Should().BeGreaterThan(
            TimeSpan.FromSeconds(20), "sonst wäre der folgende Versuch kein Beweis für den Knopf");

        var reconnect = row.Locator("[data-testid=server-reconnect]");
        (await reconnect.TextContentAsync()).Should().Contain($"Versuch {attempt}")
            .And.Contain("nächster Versuch in");

        await row.Locator("[data-testid=server-connect-now]").ClickAsync();

        await session.Page.WaitForFunctionAsync(
            """
            ([selector, before]) => {
                const row = document.querySelector(selector);
                return row !== null && Number(row.getAttribute('data-reconnect-attempt')) > before;
            }
            """,
            new object[] { rowSelector, attempt },
            new PageWaitForFunctionOptions { Timeout = 15000 });

        DateTimeOffset.UtcNow.Should().BeBefore(
            nextAttemptAt, "der Versuch kam durch den Klick, nicht durch den Ablauf der Frist");
        (await row.GetAttributeAsync("data-state")).Should().Be(
            "Failed", "das Programm gibt es weiterhin nicht — der Knopf verbindet, er zaubert nicht");
    }

    /// <summary>
    /// Laedt die Seite so lange neu, bis der Server den erwarteten Zustand meldet — und gibt sonst
    /// den zuletzt gesehenen zurueck, damit ein Fehlschlag beantwortbar ist statt nur rot.
    /// </summary>
    private static async Task<string?> WaitForStateAsync(
        UiSession session, string slug, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var row = session.Page.Locator($"[data-testid=server-row][data-slug={slug}]");
            await row.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
            last = await row.GetAttributeAsync("data-state");
            if (last == expected)
            {
                return last;
            }

            await session.Page.WaitForTimeoutAsync(1000);
            await session.Page.ReloadAsync();
            await session.WaitForInteractivityAsync("[data-testid=server-slug]");
        }

        return last;
    }

    private static async Task FillFormAsync(UiSession session, string slug, string? command = null)
    {
        await session.Page.FillAsync("[data-testid=server-slug]", slug);
        await session.Page.FillAsync("[data-testid=server-name]", $"E2E {slug}");
        await session.Page.FillAsync(
            "[data-testid=server-command]", command ?? TestServerPaths.Executable("EchoServer"));
        await session.Page.FillAsync("[data-testid=server-args]", string.Empty);

        // Die Vorgabe ist Container-Isolation (ADR-0018). Für ein lokal gebautes, vertrauenswürdiges
        // Programm nimmt ein Betreiber sie heraus — und genau dieser Klick gehört zum Weg dazu.
        var container = session.Page.Locator(
            "label:has-text('In einem Container ausführen') input[type=checkbox]");
        if (await container.IsCheckedAsync())
        {
            await container.UncheckAsync();
        }
    }
}
