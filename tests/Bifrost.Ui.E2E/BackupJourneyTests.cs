using System.IO.Compression;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP5.5: Eine Sicherung über die Oberfläche — und die Wiederherstellung, die dort <b>nicht</b>
/// angeboten wird.
/// <para>
/// <b>Beide Hälften gehören zum Weg.</b> Ein Restore tauscht Datenbank und Key-Ring <em>unter</em>
/// dem laufenden Gateway aus; er braucht einen Wartungsmoment (ADR-0024 E5) und läuft deshalb über
/// die Kommandozeile. Eine Oberfläche, die ihn trotzdem anböte, wäre kein Komfort, sondern ein
/// Knopf, der die Instanz unter sich selbst wegzieht. Dass er fehlt, ist eine Zusage — und wird
/// hier geprüft wie jede andere.
/// </para>
/// </summary>
public sealed class BackupJourneyTests
{
    private readonly ITestOutputHelper _output;

    public BackupJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task An_admin_creates_a_backup_and_the_archive_is_readable()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(An_admin_creates_a_backup_and_the_archive_is_readable), ct);

        var target = Path.Combine(Path.GetTempPath(), $"bifrost-ui-backup-{Guid.NewGuid():N}.zip");

        try
        {
            await session.LoginAsync();
            await session.Page.GotoAsync("operations");
            await session.WaitForInteractivityAsync("[data-testid=backup-path]");

            await session.Page.FillAsync("[data-testid=backup-path]", target);

            // Datenbank und Key-Ring sind die beiden Teile, ohne die eine Sicherung keine ist.
            foreach (var box in (string[])["backup-database", "backup-keyring"])
            {
                var checkbox = session.Page.Locator($"[data-testid={box}]");
                if (!await checkbox.IsCheckedAsync())
                {
                    await checkbox.CheckAsync();
                }
            }

            // Zweistufig, wie bei jeder Aktion, die etwas hinterlässt: Eine Sicherung zerstört
            // nichts, legt aber ein Geheimnis auf die Platte.
            var confirm = session.Page.Locator("[data-testid=backup-confirm]");
            await confirm.GetByText("Sicherung anlegen").ClickAsync();
            await confirm.GetByText("Ja").ClickAsync();

            var message = session.Page.Locator("[data-testid=backup-message]");
            await Assertions.Expect(message).ToContainTextAsync(
                "angelegt", new LocatorAssertionsToContainTextOptions { Timeout = 120000 });
            _output.WriteLine($"Meldung der Seite: {await message.TextContentAsync()}");

            // **Der Beweis ist das Archiv.** Eine Seite, die „angelegt" meldet, sagt nichts darüber,
            // ob ein Empfänger damit etwas anfangen kann.
            File.Exists(target).Should().BeTrue();
            using var archive = ZipFile.OpenRead(target);

            var manifest = archive.GetEntry("manifest.json");
            manifest.Should().NotBeNull(
                "das Manifest liegt unverschlüsselt oben im Archiv — sonst kann niemand prüfen, "
                + "was er vor sich hat, bevor er es öffnet");

            using var reader = new StreamReader(manifest!.Open());
            var parsed = JsonDocument.Parse(await reader.ReadToEndAsync(ct)).RootElement;
            _output.WriteLine($"Manifest: {parsed}");
            parsed.GetProperty("sections").EnumerateArray().Select(x => x.GetString())
                .Should().Contain("database", "eine Sicherung ohne die Datenbank ist keine");
        }
        finally
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    /// <summary>
    /// Die andere Hälfte: Der Bildschirm nennt die Wiederherstellung, bietet sie aber nicht an —
    /// und sagt, wo sie stattdessen läuft. Ein Weg, der nur die Sicherung prüft, ließe offen, ob
    /// hier irgendwann ein Knopf auftaucht, der die Instanz unter sich selbst wegzieht.
    /// </summary>
    [Fact]
    public async Task The_screen_names_the_restore_path_instead_of_offering_a_button()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(The_screen_names_the_restore_path_instead_of_offering_a_button), ct);

        await session.LoginAsync();
        await session.Page.GotoAsync("operations");
        await session.WaitForInteractivityAsync("[data-testid=backup-path]");

        var body = await session.Page.TextContentAsync("body");
        body.Should().Contain("Wiederherstellung").And.Contain("bifrost restore");

        // Kein Knopf, der eine Wiederherstellung auslöst — geprüft über alle Schaltflächen der
        // Seite, nicht über eine einzelne Vermutung, wie er heißen würde.
        var buttons = await session.Page.Locator("button").AllTextContentsAsync();
        _output.WriteLine($"Schaltflächen: {string.Join(" | ", buttons)}");
        buttons.Should().NotContain(text => text.Contains("Wiederherstell", StringComparison.OrdinalIgnoreCase));
    }
}
