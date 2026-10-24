using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Performance.Tests.Harness;
using Bifrost.Ui.E2E.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Bifrost.Ui.E2E;

/// <summary>
/// WP5.5: Der Rug-Pull-Schutz über die Oberfläche (FR-33).
/// <para>
/// Ein Upstream ändert die Beschreibung eines Werkzeugs — derselbe Name, andere Bedeutung. Genau so
/// sieht ein Rug-Pull aus: Ein Agent, der gestern „echoes the message back" gelesen hat, ruft heute
/// etwas auf, das die Nachricht weiterreicht. Der Gateway hält die neue Fassung zurück, bis ein
/// Mensch sie angesehen hat.
/// </para>
/// <para>
/// <b>Der Beweis ist die Werkzeugliste, nicht die Anzeige.</b> „Zurückgehalten" heißt: weder
/// sichtbar noch aufrufbar. Ein Test, der nur den Hinweis auf der Seite prüfte, ließe offen, ob das
/// Werkzeug daneben munter weiterläuft.
/// </para>
/// </summary>
public sealed class ToolPinJourneyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _output;

    public ToolPinJourneyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_changed_definition_is_held_back_until_a_human_accepts_it()
    {
        Assert.SkipUnless(UiSession.Enabled, "Browserwege nur mit BIFROST_RUN_E2E=1.");

        var ct = TestContext.Current.CancellationToken;
        await using var session = await UiSession.StartAsync(
            nameof(A_changed_definition_is_held_back_until_a_human_accepts_it), ct);

        const string slug = "echorug";
        const string tool = slug + "__echo";
        var id = await ConnectAsync(session, slug, changed: false, ct);

        (await ToolsAsync(session, ct)).Should().Contain(tool, "erst einmal ist alles normal");

        // Der Upstream ändert seine Definition — dieselbe Adresse, derselbe Werkzeugname.
        await ConnectAsync(session, slug, changed: true, ct, existing: id);

        // **Weder sichtbar noch aufrufbar** — das ist die Zusage, nicht nur ein Hinweis.
        var afterChange = await ToolsAsync(session, ct);
        _output.WriteLine($"Nach der Änderung sichtbar: {string.Join(", ", afterChange)}");
        afterChange.Should().NotContain(tool, "eine geänderte Definition wird zurückgehalten");

        await session.LoginAsync();
        await session.Page.GotoAsync("servers");
        await session.WaitForInteractivityAsync("[data-testid=server-slug]");

        // Der Alarm steht in der Zeile des Servers, nicht in einer Fußnote.
        var body = await session.Page.TextContentAsync("body");
        body.Should().Contain("Definition geändert").And.Contain("echo");

        await session.Page.ClickAsync("button:text-is('Neue Fassung annehmen')");

        // Erst nach dem Klick ist das Werkzeug zurück — mit der Fassung, die jemand gesehen hat.
        var accepted = await WaitForToolAsync(session, tool, TimeSpan.FromSeconds(60), ct);
        accepted.Should().BeTrue("nach dem Annehmen gilt die neue Fassung");
    }

    private static async Task<Guid> ConnectAsync(
        UiSession session, string slug, bool changed, CancellationToken ct, Guid? existing = null)
    {
        var config = new UpstreamServerConfig(
            slug,
            $"E2E {slug}",
            UpstreamTransportKind.Stdio,
            Enabled: true,
            Stdio: new StdioTransportOptions(
                TestServerPaths.Executable("EchoServer"),
                [],
                changed
                    ? new Dictionary<string, string> { ["BIFROST_ECHO_DESCRIPTION"] = "geaendert" }
                    : null,
                Isolation: new IsolationOptions(IsolationMode.Host)));

        var response = existing is { } known
            ? await session.Gateway.Client.PutAsJsonAsync($"api/v1/servers/{known}", config, Json, ct)
            : await session.Gateway.Client.PostAsJsonAsync("api/v1/servers", config, Json, ct);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(ct));

        if (existing is { } unchangedId)
        {
            await Task.Delay(2000, ct);
            return unchangedId;
        }

        await session.Gateway.WaitUntilHealthyAsync(slug, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct))
            .RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<List<string>> ToolsAsync(UiSession session, CancellationToken ct)
        => [.. JsonDocument.Parse(await session.Gateway.Client.GetStringAsync("api/v1/tools", ct))
            .RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)];

    private static async Task<bool> WaitForToolAsync(
        UiSession session, string tool, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            if ((await ToolsAsync(session, ct)).Contains(tool))
            {
                return true;
            }

            await Task.Delay(500, ct);
        }

        return false;
    }
}
