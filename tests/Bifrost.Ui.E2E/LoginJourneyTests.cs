using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP5.5: Der Weg, ohne den kein anderer existiert. Er läuft gegen eine echte Instanz mit echter
/// Datenbank, über TLS und mit dem ausgelieferten Anmeldeformular.
/// </summary>
public sealed class LoginJourneyTests
{
    [Fact]
    public async Task A_wrong_password_does_not_let_anyone_in()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        await using var session = await UiSession.StartAsync(
            nameof(A_wrong_password_does_not_let_anyone_in), TestContext.Current.CancellationToken);

        await session.LoginAsync(password: "definitiv-falsch");

        session.Page.Url.Should().Contain("failed=true", "eine fehlgeschlagene Anmeldung bleibt auf der Anmeldeseite");
        (await session.Page.TextContentAsync("body"))
            .Should().Contain("Benutzername oder Passwort falsch");
    }

    [Fact]
    public async Task An_operator_can_sign_in_and_out_again()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        await using var session = await UiSession.StartAsync(
            nameof(An_operator_can_sign_in_and_out_again), TestContext.Current.CancellationToken);

        await session.LoginAsync();

        session.Page.Url.Should().NotContain("login", "nach der Anmeldung steht der Betreiber in der Oberfläche");
        (await session.Page.TextContentAsync("body")).Should().NotContain("Benutzername oder Passwort falsch");

        // Abmelden ist ein Form-POST wie die Anmeldung — ein Klick auf einen Link waere hier der
        // falsche Nachweis.
        // Auch hier bis zum Ende warten: Sonst bricht das nächste GotoAsync die Abmeldung ab, und
        // der Test prüfte eine Sitzung, die noch besteht (in WebKit beobachtet, 2026-11-16).
        await session.Page.GotoAsync("");
        await session.SubmitAndWaitToLeaveAsync("text=Abmelden", session.Page.Url);

        await session.Page.GotoAsync("servers");
        session.Page.Url.Should().Contain("login", "ohne Sitzung fuehrt jede geschuetzte Seite zurueck zur Anmeldung");
    }

    /// <summary>
    /// <b>Was getippt wurde, wird auch abgeschickt</b> — gleich, wie lange die Seite danach noch
    /// steht.
    /// <para>
    /// Befund vom 2026-11-16: Die Anmeldeseite wurde vom Blazor-Circuit übernommen. Rund eine
    /// Zehntelsekunde nach dem Laden baute er sie neu auf und ersetzte die Felder durch leere. Wer
    /// schneller abschickte, kam durch; wer langsamer war — Firefox und WebKit immer, Chromium auf
    /// einem belasteten Rechner manchmal, ein Passwortmanager vermutlich auch —, schickte ein
    /// leeres Formular ab und las „Benutzername oder Passwort falsch".
    /// </para>
    /// <para>
    /// Der Test wartet deshalb absichtlich <em>zwischen</em> Tippen und Abschicken, bis das Netz
    /// ruhig ist: Baut die Seite noch eine Verbindung auf, ist sie dann aufgebaut. Das ist die
    /// Reihenfolge, die den Fehler zeigt — nicht die schnelle, die ihn meistens verdeckt.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Credentials_typed_right_after_loading_are_still_there_when_they_are_sent()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        await using var session = await UiSession.StartAsync(
            nameof(Credentials_typed_right_after_loading_are_still_there_when_they_are_sent),
            TestContext.Current.CancellationToken);

        await session.Page.GotoAsync("login");
        await session.Page.FillAsync("#username", session.Gateway.UiUsername!);
        await session.Page.FillAsync("#password", session.Gateway.UiPassword!);

        await session.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        (await session.Page.InputValueAsync("#username")).Should().Be(
            session.Gateway.UiUsername, "ein getippter Benutzername verschwindet nicht von selbst");
        (await session.Page.InputValueAsync("#password")).Should().NotBeEmpty(
            "ein getipptes Passwort verschwindet nicht von selbst");

        await session.SubmitAndWaitToLeaveAsync("button[type=submit]", session.Page.Url);

        session.Page.Url.Should().NotContain("login", "richtige Zugangsdaten führen in die Oberfläche");
    }

    /// <summary>
    /// Der Fall, den ein Anmeldetest allein nicht abdeckt: Eine geschützte Seite darf ohne Sitzung
    /// nicht einfach ausgeliefert werden — auch nicht serverseitig gerendert.
    /// </summary>
    [Fact]
    public async Task A_protected_page_is_not_served_without_a_session()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        await using var session = await UiSession.StartAsync(
            nameof(A_protected_page_is_not_served_without_a_session), TestContext.Current.CancellationToken);

        await session.Page.GotoAsync("servers");

        session.Page.Url.Should().Contain("login");
        (await session.Page.TextContentAsync("body")).Should().NotContain("Neuen Server anschließen");
    }
}
