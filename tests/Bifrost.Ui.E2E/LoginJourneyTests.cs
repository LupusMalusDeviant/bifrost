using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
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
        await session.Page.GotoAsync("");
        await session.Page.ClickAsync("text=Abmelden");

        await session.Page.GotoAsync("servers");
        session.Page.Url.Should().Contain("login", "ohne Sitzung fuehrt jede geschuetzte Seite zurueck zur Anmeldung");
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
