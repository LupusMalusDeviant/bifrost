using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// WP5.3-DoD: eine echte REST-API (Mini-Petstore, in-test gehostet) wird per OpenAPI-Spec
/// als virtueller Upstream importiert — hot-swappable, RBAC-gefiltert, auditiert wie jeder MCP-Server.
/// </summary>
public sealed class OpenApiUpstreamE2ETests : IClassFixture<GatewayFixture>, IAsyncLifetime
{
    private const string ApiKeyHeader = "X-Pet-Key";
    private const string ApiKeySecret = "petstore-geheim";

    private readonly GatewayFixture _gw;
    private WebApplication? _petApi;
    private int _port;

    public OpenApiUpstreamE2ETests(GatewayFixture gw) => _gw = gw;

    public async ValueTask InitializeAsync()
    {
        _port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _petApi = builder.Build();
        _petApi.Urls.Add($"http://127.0.0.1:{_port}");

        // Auth-Prüfung: beweist, dass der Konnektor das konfigurierte Credential injiziert.
        _petApi.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/pets")
                && ctx.Request.Headers[ApiKeyHeader] != ApiKeySecret)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(ctx);
        });

        _petApi.MapGet("/pets", (int? limit) => Results.Json(
            Enumerable.Range(1, limit ?? 3).Select(i => new { id = i, name = $"Pet{i}" })));
        _petApi.MapGet("/pets/{petId:int}", (int petId) => Results.Json(new { id = petId, name = $"Pet{petId}" }));
        _petApi.MapPost("/pets", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            return Results.Json(new { created = body.GetProperty("name").GetString() });
        });
        // Lockvogel: Die API antwortet mit einer Weiterleitung auf den Cloud-Metadatendienst. Würde
        // der Konnektor ihr folgen, ginge der Aufruf an eine nie geprüfte Adresse.
        _petApi.MapGet("/lure", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
        _petApi.MapGet("/openapi.json", () => Results.Text(SpecJson(), "application/json"));

        await _petApi.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_petApi is not null)
        {
            await _petApi.DisposeAsync();
        }
    }

    [Fact]
    public async Task Imported_api_behaves_like_a_normal_upstream()
    {
        var serverId = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                "petapi", "Petstore via OpenAPI", UpstreamTransportKind.OpenApi, Enabled: true,
                OpenApi: new OpenApiTransportOptions(
                    new Uri($"http://127.0.0.1:{_port}/openapi.json"),
                    BaseAddress: new Uri($"http://127.0.0.1:{_port}"),
                    AuthKind: OpenApiAuthKind.ApiKeyHeader,
                    Credential: ApiKeySecret,
                    ApiKeyHeaderName: ApiKeyHeader,
                    // Der Testdienst läuft auf Loopback — genau das, was die Zielprüfung sonst
                    // abweist. So sieht auch die Umstellung einer bestehenden lokalen API aus.
                    AllowPrivateTargets: true)),
            TestContext.Current.CancellationToken);
        await IntegrationSupport.WaitUntilAsync(
            () => _gw.Supervisor.GetStatus(serverId)?.State == UpstreamState.Healthy,
            because: "OpenAPI-Import muss Healthy werden");

        var (_, apiKey) = await _gw.SeedAdminAsync("pet-admin");
        using var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        // Discovery: Operationen erscheinen als namespaced Tools im Katalog (FR-19)
        var tools = await client.GetFromJsonAsync<JsonElement>("/api/v1/tools");
        var names = tools.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        names.Should().Contain(["petapi__listPets", "petapi__getPet", "petapi__createPet"]);

        // Query-Parameter
        var list = await InvokeAsync(client, "petapi__listPets", """{"limit":2}""");
        list.GetArrayLength().Should().Be(2);

        // Path-Parameter
        var single = await InvokeAsync(client, "petapi__getPet", """{"petId":7}""");
        single.GetProperty("name").GetString().Should().Be("Pet7");

        // JSON-Body (referenziertes Schema)
        var created = await InvokeAsync(client, "petapi__createPet", """{"body":{"name":"Bello"}}""");
        created.GetProperty("created").GetString().Should().Be("Bello");

        // Hot-Swap: entfernen wie jeder andere Server (WP5-DoD)
        await _gw.Supervisor.RemoveAsync(serverId, DrainPolicy.Immediate, TestContext.Current.CancellationToken);
        var afterRemove = await client.PostAsync(
            "/api/v1/tools/petapi__getPet/invoke",
            new StringContent("""{"petId":1}""", Encoding.UTF8, "application/json"));
        afterRemove.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Eine Weiterleitung der Ziel-API wird nicht verfolgt. Sonst führte ein 302 an der beim
    /// Verbinden geprüften Adresse vorbei — hier auf den Cloud-Metadatendienst — und die Zielprüfung
    /// wäre nur so lange wirksam, wie die API mitspielt.
    /// </summary>
    [Fact]
    public async Task A_redirect_from_the_api_is_not_followed()
    {
        var serverId = await _gw.Supervisor.AddAsync(
            new UpstreamServerConfig(
                "lureapi", "Weiterleitende API", UpstreamTransportKind.OpenApi, Enabled: true,
                OpenApi: new OpenApiTransportOptions(
                    new Uri($"http://127.0.0.1:{_port}/openapi.json"),
                    BaseAddress: new Uri($"http://127.0.0.1:{_port}"),
                    AuthKind: OpenApiAuthKind.ApiKeyHeader,
                    Credential: ApiKeySecret,
                    ApiKeyHeaderName: ApiKeyHeader,
                    AllowPrivateTargets: true)),
            TestContext.Current.CancellationToken);
        try
        {
            await IntegrationSupport.WaitUntilAsync(
                () => _gw.Supervisor.GetStatus(serverId)?.State == UpstreamState.Healthy,
                because: "der Import selbst muss gelingen");

            var (_, apiKey) = await _gw.SeedAdminAsync("lure-admin");
            using var client = _gw.CreateDefaultClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

            var response = await client.PostAsync(
                "/api/v1/tools/lureapi__lure/invoke",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var content = payload.GetProperty("content");

            content.GetProperty("isError").GetBoolean().Should().BeTrue();
            content.GetProperty("content")[0].GetProperty("text").GetString()
                .Should().Contain("Weiterleitung").And.Contain("169.254.169.254",
                    "die Meldung nennt das Ziel, dem nicht gefolgt wurde");
        }
        finally
        {
            await _gw.Supervisor.RemoveAsync(
                serverId, DrainPolicy.Immediate, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Unsupported_spec_fails_completely_with_precise_error_and_no_partial_tools()
    {
        var badSpecPath = Path.Combine(Path.GetTempPath(), $"bad-spec-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(badSpecPath, """
            { "openapi": "3.1.0",
              "servers": [ { "url": "http://127.0.0.1:9" } ],
              "paths": {
                "/ok": { "get": { "operationId": "fine" } },
                "/upload": { "post": {
                  "operationId": "upload",
                  "requestBody": { "content": { "multipart/form-data": { "schema": { "type": "object" } } } } } } } }
            """);
        try
        {
            var serverId = await _gw.Supervisor.AddAsync(
                new UpstreamServerConfig(
                    "badapi", "Kaputte Spec", UpstreamTransportKind.OpenApi, Enabled: true,
                    OpenApi: new OpenApiTransportOptions(new Uri(badSpecPath)),
                    Restart: new RestartPolicy(0, TimeSpan.FromMilliseconds(100), 2.0, TimeSpan.FromSeconds(1))),
                TestContext.Current.CancellationToken);

            await IntegrationSupport.WaitUntilAsync(
                () => _gw.Supervisor.GetStatus(serverId)?.State == UpstreamState.Failed,
                because: "nicht unterstützte Spec muss den Import komplett scheitern lassen");

            _gw.Supervisor.GetStatus(serverId)!.LastError.Should().Contain("application/json",
                "die Fehlermeldung benennt das nicht unterstützte Feature (WP5-DoD)");
            _gw.Supervisor.GetInventory(serverId).Should().BeNull("nie ein Halbimport — auch 'fine' erscheint nicht");
        }
        finally
        {
            File.Delete(badSpecPath);
        }
    }

    private static async Task<JsonElement> InvokeAsync(HttpClient client, string tool, string argsJson)
    {
        var response = await client.PostAsync(
            $"/api/v1/tools/{tool}/invoke", new StringContent(argsJson, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var text = payload.GetProperty("content").GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private string SpecJson() => $$"""
        {
          "openapi": "3.1.0",
          "info": { "title": "Petstore Mini", "version": "1.0.0" },
          "servers": [ { "url": "http://127.0.0.1:{{_port}}" } ],
          "paths": {
            "/pets": {
              "get": {
                "operationId": "listPets",
                "summary": "Listet Haustiere",
                "parameters": [ { "name": "limit", "in": "query", "schema": { "type": "integer" } } ]
              },
              "post": {
                "operationId": "createPet",
                "summary": "Legt ein Haustier an",
                "requestBody": {
                  "required": true,
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } }
                }
              }
            },
            "/lure": { "get": { "operationId": "lure", "summary": "Leitet weiter" } },
            "/pets/{petId}": {
              "get": {
                "operationId": "getPet",
                "summary": "Ein Haustier per Id",
                "parameters": [ { "name": "petId", "in": "path", "required": true, "schema": { "type": "integer" } } ]
              }
            }
          },
          "components": { "schemas": { "Pet": {
            "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name"] } } }
        }
        """;

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
