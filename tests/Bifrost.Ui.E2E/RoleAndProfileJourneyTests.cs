using System.Net.Http.Headers;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP5.5: Rolle, Profil, Identität und Schlüssel — der Weg, den ein Betreiber geht, wenn ein neuer
/// Agent angebunden wird.
/// <para>
/// <b>Geprüft wird die Wirkung, nicht das Formular.</b> Am Ende steht kein „angelegt", sondern ein
/// echter Schlüssel, mit dem der Gateway abgefragt wird: Ein Profil, das ein Werkzeug pinnt, muss
/// dazu führen, dass der Agent <em>genau dieses</em> sieht. Ein Test, der nur die Tabellenzeile
/// prüft, belegt, dass etwas gespeichert wurde — nicht, dass es wirkt.
/// </para>
/// </summary>
public sealed class RoleAndProfileJourneyTests
{
    private readonly ITestOutputHelper _output;

    public RoleAndProfileJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task An_admin_wires_up_an_agent_and_the_profile_decides_what_it_sees()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(An_admin_wires_up_an_agent_and_the_profile_decides_what_it_sees), ct);

        // Zwei Werkzeuge am selben Upstream — sonst könnte ein Profil, das eines pinnt, nicht von
        // einem unterschieden werden, das gar nichts tut.
        const string slug = "echoprofile";
        await session.Gateway.AddStdioUpstreamAsync(slug, "EchoServer", ct);

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var profileName = "profil-" + suffix;
        var roleName = "rolle-" + suffix;
        var agentName = "agent-" + suffix;

        await session.LoginAsync();

        // ── Profil: genau ein Werkzeug, kein Lazy-Pfad ──────────────────────────────────────────
        await session.Page.GotoAsync("profiles");
        await session.WaitForInteractivityAsync("[data-testid=profile-name]");
        await session.Page.FillAsync("[data-testid=profile-name]", profileName);
        await session.Page.FillAsync("[data-testid=profile-pinned]", $"{slug}__echo");

        var lazy = session.Page.Locator("[data-testid=profile-lazy]");
        if (await lazy.IsCheckedAsync())
        {
            // Mit den Lazy-Meta-Werkzeugen saehe der Agent zusaetzlich search_tools & Co. — dann
            // pruefte der Vergleich unten nicht mehr das Profil, sondern die Vorgabe.
            await lazy.UncheckAsync();
        }

        await session.Page.ClickAsync("[data-testid=profile-add]");
        await Assertions.Expect(session.Page.GetByText(profileName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // ── Rolle: darf Werkzeuge aufrufen ──────────────────────────────────────────────────────
        await session.Page.GotoAsync("rbac");
        await session.WaitForInteractivityAsync("[data-testid=role-name]");
        await session.Page.FillAsync("[data-testid=role-name]", roleName);
        await session.Page.CheckAsync("[data-testid=role-global]");
        await session.Page.ClickAsync("[data-testid=role-add]");
        await Assertions.Expect(session.Page.Locator($"[data-testid=identity-role][data-role='{roleName}']"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });

        // ── Identität mit Rolle und Profil ──────────────────────────────────────────────────────
        await session.Page.FillAsync("[data-testid=identity-name]", agentName);
        await session.Page.CheckAsync($"[data-testid=identity-role][data-role='{roleName}']");
        await session.Page.SelectOptionAsync(
            "[data-testid=identity-profile]", new SelectOptionValue { Label = profileName });
        await session.Page.ClickAsync("[data-testid=identity-add]");

        var keys = session.Page.Locator($"[data-testid=identity-keys][data-identity='{agentName}']");
        await Assertions.Expect(keys).ToHaveCountAsync(
            1, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });

        // ── Schlüssel — und er wird genau einmal angezeigt ──────────────────────────────────────
        await keys.ClickAsync();
        await session.Page.FillAsync("[data-testid=key-label]", "e2e");
        await session.Page.ClickAsync("[data-testid=key-issue]");

        var plaintext = session.Page.Locator("[data-testid=key-plaintext]");
        await Assertions.Expect(plaintext).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        var apiKey = (await plaintext.TextContentAsync())!.Trim();
        apiKey.Should().NotBeNullOrWhiteSpace();

        // ── Die Wirkung: Was sieht dieser Agent? ────────────────────────────────────────────────
        using var agent = session.Gateway.CreateHttpClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var tools = JsonDocument.Parse(await agent.GetStringAsync("api/v1/tools", ct)).RootElement
            .GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();

        _output.WriteLine($"Der Agent sieht: {string.Join(", ", tools)}");
        tools.Should().ContainSingle().Which.Should().Be($"{slug}__echo",
            "das Profil bestimmt, was der Agent überhaupt zu sehen bekommt");
    }
}
