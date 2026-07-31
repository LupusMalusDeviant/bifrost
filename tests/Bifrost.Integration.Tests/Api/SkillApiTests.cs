using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Bifrost.Integration.Tests.Gateway;
using Xunit;

namespace Bifrost.Integration.Tests.Api;

/// <summary>
/// REST-Fläche für Skills (FR-40). Jeder andere Speicher hatte eine — Skills nur die
/// Weboberfläche.
/// <para>
/// Für einen einzelnen Text reicht ein Formular. Für die Skill-Sammlung eines Agenten — Dutzende
/// Dateien mit Verweisen untereinander — ist Abtippen keine Bedienung: Ohne diese Endpunkte lässt
/// sich der Bestand weder aus einem Repository befüllen noch versionieren noch sichern.
/// </para>
/// </summary>
public sealed class SkillApiTests : IClassFixture<GatewayFixture>
{
    private static readonly string[] RequiredTools = ["srv__tool"];
    private static readonly string[] MissingReference = ["gibt-es-nicht"];

    private readonly GatewayFixture _gw;

    public SkillApiTests(GatewayFixture gw) => _gw = gw;

    private IAssetStore Assets => _gw.Services.GetRequiredService<IAssetStore>();

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, key) = await _gw.SeedAdminAsync($"skillapi-{Guid.NewGuid():N}");
        var client = _gw.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");
        return client;
    }

    [Fact]
    public async Task A_skill_can_be_created_and_read_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        var name = $"api-skill-{Guid.NewGuid():N}";

        var created = await client.PostAsJsonAsync("/api/v1/skills", new
        {
            name,
            content = "## Ablauf\nZuerst suchen.",
            description = "Über die API angelegt",
            whenToUse = "Beim Kartieren",
            requiredTools = RequiredTools,
        }, ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = body.GetProperty("id").GetGuid();

        var read = await client.GetFromJsonAsync<JsonElement>($"/api/v1/skills/{id}", ct);
        read.GetProperty("name").GetString().Should().Be(name);
        read.GetProperty("content").GetString().Should().Be("## Ablauf\nZuerst suchen.");
        read.GetProperty("whenToUse").GetString().Should().Be("Beim Kartieren");
    }

    /// <summary>
    /// Befunde sind Warnungen, keine Fehler — aber ein Skript sieht die Oberfläche nicht. Deshalb
    /// kommen sie mit der Antwort zurück, statt nur dort zu erscheinen.
    /// </summary>
    [Fact]
    public async Task Findings_come_back_with_the_response_instead_of_only_appearing_in_the_ui()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/skills", new
        {
            name = $"api-findings-{Guid.NewGuid():N}",
            content = "Text",
            references = MissingReference,
        }, ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created, "ein Befund blockiert nicht");
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("findings").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("message").GetString().Should().Contain("gibt-es-nicht");
    }

    [Fact]
    public async Task Publishing_appends_a_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        var id = await Assets.CreateAsync($"api-version-{Guid.NewGuid():N}", null, "v1", null, ct);

        var published = await client.PostAsJsonAsync(
            $"/api/v1/skills/{id.Value}/versions", new { content = "v2" }, ct);

        published.StatusCode.Should().Be(HttpStatusCode.OK);
        (await published.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("version").GetInt32()
            .Should().Be(2);
        (await Assets.GetAsync(id, null, ct)).Content.Should().Be("v2");
    }

    /// <summary>
    /// Die Beschreibung war nach dem Anlegen <b>unveränderlich</b> — weder über die Oberfläche noch
    /// über die API zu ändern. Das ist ausgerechnet die Angabe, an der ein Agent entscheidet, ob er
    /// den Skill nimmt: Ein Tippfehler darin wäre dauerhaft gewesen.
    /// </summary>
    [Fact]
    public async Task Publishing_can_correct_the_description()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        var name = $"api-desc-{Guid.NewGuid():N}";
        var id = await Assets.CreateAsync(name, "abgeschnitten…", "Text", null, ct);

        await client.PostAsJsonAsync(
            $"/api/v1/skills/{id.Value}/versions",
            new { content = "Text", description = "vollständig und richtig" }, ct);

        var listed = (await Assets.ListAsync(ct)).Single(a => a.Name == name);
        listed.Description.Should().Be("vollständig und richtig");
    }

    /// <summary>
    /// Ohne Angabe bleibt sie stehen — sonst löschte jede Textänderung die Beschreibung mit.
    /// </summary>
    [Fact]
    public async Task Publishing_without_a_description_keeps_the_existing_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        var name = $"api-keep-{Guid.NewGuid():N}";
        var id = await Assets.CreateAsync(name, "bleibt", "v1", null, ct);

        await client.PostAsJsonAsync($"/api/v1/skills/{id.Value}/versions", new { content = "v2" }, ct);

        (await Assets.ListAsync(ct)).Single(a => a.Name == name).Description.Should().Be("bleibt");
    }

    /// <summary>
    /// Ein doppelter Name ist ein Bedienfehler, kein Serverfehler — und die Meldung muss sagen,
    /// was los ist, sonst sucht ein Skript im Nebel.
    /// </summary>
    [Fact]
    public async Task A_duplicate_name_is_a_bad_request_with_a_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        var name = $"api-dup-{Guid.NewGuid():N}";
        await Assets.CreateAsync(name, null, "erster", null, ct);

        var second = await client.PostAsJsonAsync(
            "/api/v1/skills", new { name, content = "zweiter" }, ct);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadAsStringAsync(ct)).Should().Contain("bereits einen Skill");
    }

    [Fact]
    public async Task The_endpoints_are_admin_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, key) = await _gw.SeedIdentityAsync($"skillapi-ohne-{Guid.NewGuid():N}", grants: []);
        var client = _gw.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");

        var listed = await client.GetAsync("/api/v1/skills", ct);

        listed.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}
