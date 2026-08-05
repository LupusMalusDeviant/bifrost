using System.Net;

using AwesomeAssertions;

using Bifrost.Integration.Tests.Gateway;

using Xunit;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// <b>BFR-F-0053 — eine Fehlerantwort ohne Rumpf.</b>
/// <para>
/// Im Piloten vom 2026-08-05 antwortete <c>/rbac/keys</c> — ein Pfad aus der eigenen Dokumentation,
/// den es so nie gab — mit <c>404</c> und null Bytes. Der Statuscode war richtig, im Browser stand
/// eine weiße Seite ohne Hinweis und ohne Rückweg. Ein Tippfehler sah aus wie ein Ausfall.
/// </para>
/// </summary>
public class ErrorBodyTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _fixture;

    public ErrorBodyTests(GatewayFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Der Fall aus dem Piloten, wörtlich: derselbe Pfad, derselbe erwartete Statuscode — nur nicht
    /// mehr leer.
    /// </summary>
    [Theory]
    [InlineData("/rbac/keys")]
    [InlineData("/voellig-erfunden")]
    public async Task An_unknown_page_says_what_happened_and_offers_a_way_back(string path)
    {
        var response = await _fixture.CreateUiClient()
            .GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().NotBeEmpty("eine weisse Seite ist von einem Ausfall nicht zu unterscheiden");
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        body.Should().Contain("404");
        body.Should().Contain(
            "B.I.F.R.O.S.T", "ohne Kennzeichen weiss der Besucher nicht einmal, wo er gelandet ist");
        body.Should().Contain(
            "href=\"/\"",
            "eine Fehlerseite ohne Rueckweg laesst den Besucher da stehen, wo er nicht sein wollte");
    }

    /// <summary>
    /// Für Maschinen dieselbe Form wie bei den Betriebsendpunkten. Ein zweites Fehlerformat für
    /// dieselbe Sache ließe jeden Client raten, welches gerade gilt.
    /// <para>
    /// <b>Mit Schlüssel</b>, und das ist kein Umweg um den Test herum: Ohne Schlüssel antwortet der
    /// API-Key-Wächter mit <c>401</c>, bevor das Routing überhaupt feststellt, dass es den Pfad
    /// nicht gibt. Das ist richtig so — einem unangemeldeten Aufrufer zu verraten, welche
    /// API-Pfade existieren, wäre eine Auskunft ohne Anlass. Der leere Rumpf entstand hinter dieser
    /// Schwelle, also wird er auch dort geprüft.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unknown_api_path_answers_in_the_shape_the_api_uses_everywhere()
    {
        var (_, key) = await _fixture.SeedAdminAsync("errorbody-admin");
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var response = await client
            .GetAsync("/api/v1/gibt-es-nicht", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("\"code\"").And.Contain("not-found");
        body.Should().NotContain("<html", "eine Maschine bekommt keine Seite zum Ansehen");
    }

    /// <summary>
    /// <b>Die Gegenprobe zur Gegenprobe.</b> Der Rumpf entsteht nur, wo keiner ist. Eine Absage mit
    /// eigener Begründung — hier die des API-Key-Wächters — darf nicht überschrieben werden; sonst
    /// hätte diese Korrektur die Fehlermeldungen des Produkts durch eine Allerweltsformel ersetzt.
    /// </summary>
    [Fact]
    public async Task An_answer_that_already_has_a_body_keeps_it()
    {
        var response = await _fixture.CreateDefaultClient()
            .GetAsync("/api/v1/servers", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(
            "unauthenticated",
            "das ist der Ersatztext dieser Korrektur — er darf nur erscheinen, wo sonst nichts steht");
    }
}
