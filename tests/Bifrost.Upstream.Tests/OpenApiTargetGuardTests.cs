using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.OpenApi;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// Zielprüfung des OpenAPI-Konnektors — dieselbe Grenze, die OpenRPC schon hatte
/// (Security-Audit WP7.2, nachgezogen mit Phase 8).
/// <para>
/// Ohne sie ist ein Gateway, das eine konfigurierte URL abruft, ein Werkzeug, um interne Dienste zu
/// erreichen. Die Fälle hier prüfen beide Wege hinein: die Spec-Quelle <b>und</b> die Ziel-API, die
/// aus der Spec kommt.
/// </para>
/// </summary>
public sealed class OpenApiTargetGuardTests
{
    private static Task<IUpstreamConnection> ConnectAsync(OpenApiTransportOptions options)
        => new OpenApiUpstreamConnector().ConnectAsync(
            new ServerId(Guid.NewGuid()),
            new UpstreamServerConfig("api", "API", UpstreamTransportKind.OpenApi, true, OpenApi: options),
            TestContext.Current.CancellationToken);

    /// <summary>Eine Spec-Quelle im internen Netz wird abgewiesen, bevor die Verbindung steht.</summary>
    [Theory]
    [InlineData("http://127.0.0.1/openapi.json")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/openapi.json")]
    [InlineData("http://192.168.1.10/openapi.json")]
    [InlineData("http://172.16.0.1/openapi.json")]
    [InlineData("http://[::1]/openapi.json")]
    public async Task An_internal_spec_source_is_refused(string url)
    {
        var act = async () => await ConnectAsync(new OpenApiTransportOptions(new Uri(url)));

        (await act.Should().ThrowAsync<OpenApiImportException>())
            .WithMessage("*interne Adresse*");
    }

    /// <summary>
    /// Der eigentliche Umweg: Die Spec liegt harmlos als Datei, aber ihr <c>servers</c>-Eintrag zeigt
    /// nach innen. Wird nur die Quelle geprüft, ist die Prüfung eine Formalie — die Aufrufe gingen
    /// trotzdem an den internen Dienst.
    /// </summary>
    [Fact]
    public async Task A_local_spec_pointing_at_an_internal_api_is_refused()
    {
        var path = await WriteSpecAsync("http://169.254.169.254");
        try
        {
            var act = async () => await ConnectAsync(new OpenApiTransportOptions(new Uri(path)));

            (await act.Should().ThrowAsync<OpenApiImportException>())
                .WithMessage("*interne Adresse*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Und eine konfigurierte BaseAddress nach innen ebenso — sie überschreibt die Spec.</summary>
    [Fact]
    public async Task An_internal_base_address_is_refused()
    {
        var path = await WriteSpecAsync("https://api.example.com");
        try
        {
            var act = async () => await ConnectAsync(new OpenApiTransportOptions(
                new Uri(path), BaseAddress: new Uri("http://127.0.0.1:8080")));

            (await act.Should().ThrowAsync<OpenApiImportException>())
                .WithMessage("*interne Adresse*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Wer es ausdrücklich erlaubt, bekommt es: Die Grenze ist eine Vorgabe, kein Verbot — eine API
    /// im eigenen Netz einzubinden bleibt möglich, nur nicht mehr versehentlich.
    /// </summary>
    [Fact]
    public async Task An_internal_target_is_allowed_when_asked_for()
    {
        var path = await WriteSpecAsync("http://127.0.0.1:8080");
        try
        {
            await using var connection = await ConnectAsync(
                new OpenApiTransportOptions(new Uri(path), AllowPrivateTargets: true));

            var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);
            inventory.Tools.Select(t => t.Name).Should().Equal("fine");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Die Absage nennt den Schalter — sonst wäre die Umstellung ein Rätsel.</summary>
    [Fact]
    public async Task The_refusal_names_the_switch()
    {
        var act = async () => await ConnectAsync(
            new OpenApiTransportOptions(new Uri("http://127.0.0.1/openapi.json")));

        (await act.Should().ThrowAsync<OpenApiImportException>())
            .WithMessage("*AllowPrivateTargets*");
    }

    private static async Task<string> WriteSpecAsync(string serverUrl)
    {
        var path = Path.Combine(Path.GetTempPath(), $"openapi-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, $$"""
        {
          "openapi": "3.1.0",
          "info": { "title": "demo", "version": "1.0.0" },
          "servers": [ { "url": "{{serverUrl}}" } ],
          "paths": { "/ok": { "get": { "operationId": "fine" } } }
        }
        """, TestContext.Current.CancellationToken);
        return path;
    }
}
