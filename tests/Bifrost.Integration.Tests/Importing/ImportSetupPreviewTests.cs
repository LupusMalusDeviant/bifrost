using System.Net;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

using Bifrost.Security.Tests.Infrastructure;
using Bifrost.Server.Importing;

using Xunit;

namespace Bifrost.Integration.Tests.Importing;

/// <summary>
/// Der lokale Setup-Weg <b>während</b> der Erstzugang aussteht.
///
/// <para>
/// Eine eigene Testklasse und damit eine eigene Instanz des Hosts: Diese Prüfungen brauchen den
/// Zustand „Erstzugang steht aus", <see cref="ImportEndpointAccessTests"/> braucht den Zustand
/// danach. Beides an derselben Instanz hieße, dass die Reihenfolge der Testmethoden über das
/// Ergebnis entscheidet.
/// </para>
/// </summary>
public class ImportSetupPreviewTests : IClassFixture<ImportGatewayFixture>
{
    private readonly ImportGatewayFixture _gateway;

    public ImportSetupPreviewTests(ImportGatewayFixture gateway) => _gateway = gateway;

    private const string Document =
        """{"mcpServers":{"setup-fall":{"command":"/usr/bin/echo","args":["hallo"]}}}""";

    /// <summary>
    /// Der Weg tut, wofür es ihn gibt: Wer lokal steht und das Erstzugangs-Token hat, bekommt die
    /// Vorschau — ohne Konto, weil es noch keines gibt.
    /// </summary>
    [Fact]
    public async Task Locally_and_with_the_setup_token_the_preview_answers()
    {
        var token = await _gateway.PendingSetupTokenAsync();
        token.Should().NotBeNull("dieser Test setzt einen ausstehenden Erstzugang voraus");

        using var response = await SendAsync(token, IPAddress.Loopback);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("candidates").GetArrayLength().Should().Be(1);

        // Kein Handle: Vorgemerkt wird nur fuer einen Eigentuemer, und den gibt es vor dem
        // Einloesen nicht. Ein Vorgang ohne Eigentuemer waere ein Vorgang, den der Naechste
        // uebernimmt.
        parsed.RootElement.GetProperty("token").ValueKind.Should().Be(JsonValueKind.Null,
            "der Setup-Weg zeigt an und merkt nichts vor");
    }

    /// <summary>
    /// Ohne Token: <c>401</c>. Damit ist dieser Weg kein anonymer — wer das Token hat, hat die
    /// Übergabedatei gelesen, und wer die gelesen hat, ist bereits der Betreiber.
    /// </summary>
    [Fact]
    public async Task Without_the_setup_token_there_is_no_preview()
    {
        using var response = await SendAsync(token: null, IPAddress.Loopback);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_setup_token_is_refused_and_never_echoed()
    {
        using var response = await SendAsync(SecretCorpus.BootstrapToken, IPAddress.Loopback);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        SecretCorpus.FirstLeakIn(body).Should().BeNull(
            "ein abgelehnter Versuch ist genau die Stelle, an der ein vorgelegtes Geheimnis in "
            + "einer Fehlerzeile mitreisen wuerde");
    }

    /// <summary>
    /// Von einer fremden Adresse gibt es diesen Endpunkt nicht — und zwar als <c>404</c>, nicht als
    /// <c>403</c>: Ein <c>403</c> bestätigte, dass es ihn gibt.
    /// </summary>
    [Fact]
    public async Task From_another_machine_the_endpoint_does_not_exist()
    {
        var token = await _gateway.PendingSetupTokenAsync();

        using var response = await SendAsync(token, IPAddress.Parse("203.0.113.7"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Ohne erkennbare Gegenstelle gilt die Anfrage als <b>nicht</b> lokal. Fail-closed ist hier
    /// billig: Die einzige Lage, in der eine Adresse fehlt, ist ein Transport ohne Netz.
    /// </summary>
    [Fact]
    public async Task Without_a_remote_address_the_request_does_not_count_as_local()
    {
        var token = await _gateway.PendingSetupTokenAsync();

        using var response = await SendAsync(token, address: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Die Eingangsgrenzen gelten hier genauso — sie sind keine Eigenschaft der Anmeldung.</summary>
    [Fact]
    public async Task The_size_limit_applies_on_the_setup_path_too()
    {
        var token = await _gateway.PendingSetupTokenAsync();
        var zuGross = "{\"mcpServers\":{},\"fuellung\":\""
            + new string('x', ImportRequestLimits.MaxDocumentBytes) + "\"}";

        using var response = await SendAsync(token, IPAddress.Loopback, zuGross);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string? token, IPAddress? address, string document = Document)
    {
        using var client = _gateway.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        var request = new HttpRequestMessage(HttpMethod.Post, ImportEndpoints.SetupPreviewPath)
        {
            Content = new StringContent(document, Encoding.UTF8, "application/json"),
        };
        if (token is not null)
        {
            request.Headers.Add(ImportEndpoints.SetupTokenHeader, token);
        }

        if (address is not null)
        {
            request.Headers.Add(ImportGatewayFixture.RemoteAddressHeader, address.ToString());
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
