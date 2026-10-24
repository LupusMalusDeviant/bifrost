using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP6.2 über die Oberfläche: Ein Administrator exportiert das Auditlog und lässt die
/// Integritätskette nachrechnen — ohne die Kommandozeile zu öffnen.
/// <para>
/// <b>Geprüft wird die Datei, nicht die Anzeige.</b> Eine Seite, die „geschrieben" meldet, ist kein
/// Beleg dafür, dass etwas Brauchbares in der Datei steht; der Weg liest sie deshalb zurück und
/// zerlegt sie.
/// </para>
/// <para>
/// Und die zweite Zusage steht als eigene Erwartung da: Der Kettenbericht zeigt seine
/// <b>Grenzen</b> neben dem Ergebnis. Ein „kein Bruch gefunden", das jemand für „nichts fehlt"
/// hält, ist gefährlicher als gar keine Kette.
/// </para>
/// </summary>
public sealed class AuditExportJourneyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _output;

    public AuditExportJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task An_admin_exports_the_audit_log_and_checks_the_chain()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(An_admin_exports_the_audit_log_and_checks_the_chain), ct);

        // Etwas Auditierbares, damit das Log nicht leer ist — über die API, weil hier der
        // Exportweg geprüft wird und nicht die Rollenverwaltung.
        var seeded = await session.Gateway.Client.PostAsJsonAsync(
            "api/v1/rbac/roles",
            new
            {
                id = new { value = Guid.NewGuid() },
                name = $"audit-ui-{Guid.NewGuid():N}"[..16],
                grants = Array.Empty<object>(),
            },
            Json,
            ct);
        seeded.IsSuccessStatusCode.Should().BeTrue();

        var target = Path.Combine(Path.GetTempPath(), $"bifrost-ui-audit-{Guid.NewGuid():N}.jsonl");

        try
        {
            await session.LoginAsync();
            await session.Page.GotoAsync("logs");
            await session.WaitForInteractivityAsync("[data-testid=audit-export-path]");

            await session.Page.FillAsync("[data-testid=audit-export-path]", target);
            await session.Page.SelectOptionAsync("[data-testid=audit-export-format]", "Jsonl");
            await session.Page.ClickAsync("[data-testid=audit-export-run]");

            var message = session.Page.Locator("[data-testid=audit-message]");
            await Assertions.Expect(message).ToContainTextAsync(
                "geschrieben", new LocatorAssertionsToContainTextOptions { Timeout = 60000 });
            _output.WriteLine($"Meldung der Seite: {await message.TextContentAsync()}");

            // **Der Beweis ist die Datei.** Jede Zeile für sich vollständig — daran hängt, dass ein
            // Empfänger mitlesen kann, bevor der Export fertig ist.
            File.Exists(target).Should().BeTrue("die Seite hat auf den Gateway-Rechner geschrieben");
            var lines = await File.ReadAllLinesAsync(target, ct);
            lines.Should().NotBeEmpty();
            foreach (var line in lines)
            {
                JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64()
                    .Should().BeGreaterThan(0);
            }

            // Und die Kette — über den vollen Datenstrom, nicht über den Filter der Ansicht.
            await session.Page.ClickAsync("[data-testid=audit-verify-run]");
            var result = session.Page.Locator("[data-testid=audit-chain-result]");
            await Assertions.Expect(result).ToContainTextAsync(
                "Kein Bruch gefunden", new LocatorAssertionsToContainTextOptions { Timeout = 60000 });

            // Die Grenzen stehen NEBEN dem Ergebnis. Ohne sie wäre die Zeile darüber eine
            // Vollständigkeitsaussage, die sie nicht ist.
            var limits = await session.Page.TextContentAsync("[data-testid=audit-chain-limits]");
            limits.Should().Contain("übernommenen Wirt").And.Contain("Vollständigkeit");
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
    /// Das ganze Log ausleiten ist Verwaltung. Die Ansicht steht jedem Angemeldeten offen — der
    /// Export nicht: Wer sein eigenes Handeln nachlesen darf, darf deshalb noch lange nicht alles
    /// mitnehmen.
    /// <para>
    /// Der Auditor wird über die <b>Oberfläche</b> angelegt, weil es für UI-Nutzer keine REST-API
    /// gibt — sie sind bewusst nicht über einen Agenten-Token erreichbar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_auditor_sees_the_log_but_not_the_export()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(An_auditor_sees_the_log_but_not_the_export), ct);

        var username = $"pruefer{Guid.NewGuid():N}"[..14];
        const string password = "Pruefer!2026#lang";

        await session.LoginAsync();
        await session.Page.GotoAsync("users");
        await session.WaitForInteractivityAsync("[data-testid=user-name]");

        await session.Page.FillAsync("[data-testid=user-name]", username);
        await session.Page.FillAsync("[data-testid=user-password]", password);
        await session.Page.SelectOptionAsync("[data-testid=user-role]", "Auditor");
        await session.Page.ClickAsync("[data-testid=user-add]");
        await Assertions.Expect(session.Page.GetByText(username).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // Neue Sitzung, damit die Admin-Anmeldung nicht mitreist.
        await session.Page.GotoAsync("logout");
        await session.LoginAsync(username, password);
        await session.Page.GotoAsync("logs");
        await session.WaitForInteractivityAsync("table");

        _output.WriteLine($"Auditor '{username}' sieht das Log — und den Export nicht.");
        await Assertions.Expect(session.Page.Locator("[data-testid=audit-export-run]"))
            .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 15000 });
        await Assertions.Expect(session.Page.Locator("[data-testid=audit-verify-run]"))
            .ToHaveCountAsync(0);
    }
}
