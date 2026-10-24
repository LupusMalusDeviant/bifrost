using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP5.5: Der geführte Erstaufbau (WP4.4) über den Browser — der allererste Weg, den ein Betreiber
/// geht.
/// <para>
/// <b>Die Instanz startet ohne jeden UI-Zugang</b>, so wie nach einem frischen
/// <c>docker compose up</c>. Der Zugang entsteht im Wizard, mit dem Setup-Token, das der Gateway
/// beim ersten Start in <c>config/bootstrap-token.txt</c> schreibt — ausdrücklich nicht ins Log.
/// </para>
/// <para>
/// <b>Kein Abkürzen.</b> Der Weg holt das Token dort, wo ein Mensch es auch holt (aus der Datei auf
/// dem Rechner des Gateways), und legt den Zugang über das ausgelieferte Formular an. Ein Test, der
/// stattdessen einen UI-Nutzer vorab setzte, prüfte eine Installation, die es beim ersten Start
/// nicht gibt (M5-Vertrag §8).
/// </para>
/// </summary>
public sealed class SetupWizardJourneyTests
{
    private readonly ITestOutputHelper _output;

    public SetupWizardJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_fresh_instance_gets_its_first_access_through_the_wizard()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(A_fresh_instance_gets_its_first_access_through_the_wizard), ct, withUiAdmin: false);

        var tokenFile = Path.Combine(session.Gateway.DataDirectory, "config", "bootstrap-token.txt");
        File.Exists(tokenFile).Should().BeTrue(
            "der Gateway legt das Setup-Token beim ersten Start an — und schreibt es in eine Datei, "
            + "nicht ins Log");

        var token = TokenFrom(await File.ReadAllLinesAsync(tokenFile, ct));
        token.Should().NotBeNullOrWhiteSpace(
            "die Datei ist ein lesbarer Zettel und nicht das nackte Token — genau so, wie ein "
            + "Betreiber sie vorfindet");

        const string username = "erstbetreiber";
        const string password = "Erstaufbau!2026#";

        await session.Page.GotoAsync("first-run");
        await session.WaitForInteractivityAsync("h2");

        await ChooseModeAsync(session);
        await session.Page.ClickAsync("button:text-is('Weiter')");
        await session.Page.WaitForSelectorAsync(
            "#wizard-token", new PageWaitForSelectorOptions { Timeout = 30000 });

        await session.Page.FillAsync("#wizard-token", token);
        await session.Page.FillAsync("#wizard-user", username);
        await session.Page.FillAsync("#wizard-password", password);
        await session.Page.ClickAsync("button:text-is('Zugang anlegen')");

        // **Der Beleg: Der Wizard nennt den angemeldeten Betreiber beim Namen.**
        await Assertions.Expect(session.Page.GetByText(username).First).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 60000 });
        _output.WriteLine($"Erstzugang angelegt: {username}");

        // Und er trägt: Eine geschützte Seite ist danach ohne weitere Anmeldung erreichbar.
        await session.Page.GotoAsync("users");
        await session.WaitForInteractivityAsync("[data-testid=user-name]");
        (await session.Page.TextContentAsync("body")).Should().Contain(username);
    }

    /// <summary>
    /// Holt das Token aus dem Übergabezettel — die Zeile unter „Token:".
    /// <para>
    /// Die Datei ist bewusst für Menschen geschrieben (Frist, Anleitung, Hinweis auf das Löschen).
    /// Wer sie im Ganzen einreicht, bekommt eine Absage; der erste Entwurf dieses Weges tat genau
    /// das und sah aus wie ein abgelehntes Token.
    /// </para>
    /// </summary>
    private static string TokenFrom(string[] lines)
    {
        for (var i = 0; i < lines.Length - 1; i++)
        {
            if (!lines[i].Trim().StartsWith("Token:", StringComparison.Ordinal))
            {
                continue;
            }

            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException(
            "Im Übergabezettel steht keine Token-Zeile: " + string.Join(" / ", lines));
    }

    /// <summary>
    /// Wählt die Betriebsart — und prüft, dass die Wahl <b>angekommen</b> ist.
    /// <para>
    /// <b>Warum nicht einfach klicken:</b> Der Wizard legt seine Sitzung erst nach dem ersten
    /// Rendern an (er liest die Kennung aus einem Cookie, und dafür braucht er einen Browser). Ein
    /// Klick davor läuft ins Leere — das Feld bleibt null, und der Wizard sagt beim Weitergehen
    /// „Bitte eine Betriebsart wählen". Der erste Entwurf dieses Weges lief genau in diese Falle
    /// und scheiterte drei Schritte später an einem Formular, das nie erschienen war.
    /// </para>
    /// </summary>
    private static async Task ChooseModeAsync(UiSession session)
    {
        var radio = session.Page.Locator("label:has-text('Lokale Werkbank') input[type=radio]");

        // Bis die Wahl steht — der Circuit meldet nicht, wann er seine Sitzung hat.
        for (var attempt = 0; attempt < 20 && !await radio.IsCheckedAsync(); attempt++)
        {
            await radio.CheckAsync();
            await Task.Delay(500);
        }

        await Assertions.Expect(radio).ToBeCheckedAsync(
            new LocatorAssertionsToBeCheckedOptions { Timeout = 15000 });
    }

    /// <summary>
    /// Die Kehrseite: <b>Ein falsches Token legt keinen Zugang an.</b> Ohne diesen Fall bliebe
    /// offen, ob das Formular überhaupt etwas prüft — und der erste Zugang zu einer Instanz ist die
    /// Stelle, an der ein Durchwinken am teuersten wäre.
    /// </summary>
    [Fact]
    public async Task A_wrong_setup_token_creates_no_access()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(A_wrong_setup_token_creates_no_access), ct, withUiAdmin: false);

        await session.Page.GotoAsync("first-run");
        await session.WaitForInteractivityAsync("h2");
        await ChooseModeAsync(session);
        await session.Page.ClickAsync("button:text-is('Weiter')");
        await session.Page.WaitForSelectorAsync(
            "#wizard-token", new PageWaitForSelectorOptions { Timeout = 30000 });

        await session.Page.FillAsync("#wizard-token", "falsches-token-0000");
        await session.Page.FillAsync("#wizard-user", "eindringling");
        await session.Page.FillAsync("#wizard-password", "Eindringen!2026#");
        await session.Page.ClickAsync("button:text-is('Zugang anlegen')");

        // Das Formular steht wieder da — und niemand ist angemeldet.
        await session.Page.WaitForSelectorAsync(
            "#wizard-token", new PageWaitForSelectorOptions { Timeout = 60000 });
        var body = await session.Page.TextContentAsync("body");
        body.Should().NotContain("Angemeldet als");
        _output.WriteLine("Falsches Token: kein Zugang, Formular steht wieder da.");
    }
}
