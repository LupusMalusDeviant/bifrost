using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP5.6 über die Oberfläche: Ein Betreiber erzeugt ein Support-Bundle, ohne die Kommandozeile zu
/// öffnen — und sieht <b>vorher</b>, was hineinkäme.
/// <para>
/// Der Weg prüft zugleich die Zusicherung, an der alles hängt: Das erzeugte Archiv wird geöffnet und
/// gegen den Negativkorpus gehalten. Ein Bundle, das man nicht weitergeben kann, ist keins.
/// </para>
/// </summary>
public sealed class SupportBundleJourneyTests
{
    private readonly ITestOutputHelper _output;

    public SupportBundleJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task An_admin_previews_and_creates_a_shareable_support_bundle()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(An_admin_previews_and_creates_a_shareable_support_bundle), ct);

        var target = Path.Combine(Path.GetTempPath(), $"bifrost-ui-bundle-{Guid.NewGuid():N}.zip");

        try
        {
            await session.LoginAsync();
            await session.Page.GotoAsync("operations");
            await session.WaitForInteractivityAsync("[data-testid=bundle-path]");

            await session.Page.FillAsync("[data-testid=bundle-path]", target);

            // Vorschau zuerst — der Knopf zum Erzeugen ist ohne sie ausdrücklich gesperrt.
            var create = session.Page.Locator("[data-testid=bundle-create]");
            (await create.IsDisabledAsync()).Should().BeTrue(
                "ohne Vorschau gibt es kein Archiv: Ein Bundle, dessen Inhalt man erst nach dem "
                + "Schreiben sieht, ist bereits geschrieben");

            await session.Page.ClickAsync("[data-testid=bundle-preview]");
            var message = session.Page.Locator("[data-testid=bundle-message]");
            await message.WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
            _output.WriteLine($"Vorschau: {await message.TextContentAsync()}");

            var body = await session.Page.TextContentAsync("body");
            body.Should().Contain("diagnostics.json").And.Contain("runtime.json");
            body.Should().Contain("Logs", "eine übersprungene Kategorie steht sichtbar in der Vorschau");

            await create.ClickAsync();
            await Assertions.Expect(message).ToContainTextAsync(
                "Bundle erzeugt", new LocatorAssertionsToContainTextOptions { Timeout = 60000 });

            File.Exists(target).Should().BeTrue("die Oberfläche schreibt an den angegebenen Pfad");
            ReadAll(target).Should().NotContain(
                "KORPUS", "auch der Weg über die Oberfläche geht durch den Maskierungslauf");
        }
        finally
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
    }

    private static string ReadAll(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var text = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }
}
