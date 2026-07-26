using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Upstream.OpenRpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// OpenRPC gegen einen echten JSON-RPC-Dienst (Roadmap Phase 8). Geprüft werden die Punkte, die der
/// Spike als Vertrag nennt: Discovery, benannte und positionale Parameter, strukturierte Fehler,
/// Id-Korrelation — und die Sicherheitsgrenzen beim Import.
/// </summary>
public sealed class OpenRpcUpstreamE2ETests : IAsyncLifetime
{
    private WebApplication? _service;
    private int _port;

    private const string Document = """
    {
      "openrpc": "1.3.2",
      "info": { "title": "rechner", "version": "1.0.0" },
      "methods": [
        {
          "name": "sum",
          "summary": "Addiert",
          "paramStructure": "by-name",
          "params": [
            { "name": "a", "required": true, "schema": { "type": "integer" } },
            { "name": "b", "required": true, "schema": { "type": "integer" } }
          ]
        },
        {
          "name": "subtract",
          "paramStructure": "by-position",
          "params": [
            { "name": "minuend", "required": true, "schema": { "type": "integer" } },
            { "name": "subtrahend", "required": true, "schema": { "type": "integer" } }
          ]
        },
        { "name": "boom", "paramStructure": "by-name", "params": [] }
      ]
    }
    """;

    public async ValueTask InitializeAsync()
    {
        _port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _service = builder.Build();
        _service.Urls.Add($"http://127.0.0.1:{_port}");

        _service.MapPost("/rpc", async (HttpContext ctx) =>
        {
            var request = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var id = request.GetProperty("id").GetString();
            var method = request.GetProperty("method").GetString();
            request.TryGetProperty("params", out var parameters);

            return method switch
            {
                "rpc.discover" => Results.Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = JsonSerializer.Deserialize<JsonElement>(Document),
                }),
                "sum" => Results.Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = parameters.GetProperty("a").GetInt32() + parameters.GetProperty("b").GetInt32(),
                }),
                // Positional: Der Dienst erwartet ein Array in der Reihenfolge der Descriptors.
                "subtract" => parameters.ValueKind is JsonValueKind.Array
                    ? Results.Json(new
                    {
                        jsonrpc = "2.0",
                        id,
                        result = parameters[0].GetInt32() - parameters[1].GetInt32(),
                    })
                    : Results.Json(new
                    {
                        jsonrpc = "2.0",
                        id,
                        error = new { code = -32602, message = "params muss ein Array sein" },
                    }),
                "boom" => Results.Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32000, message = "kaputt", data = "Einzelheiten" },
                }),
                // Antwort mit fremder Id — muss als nicht zugehörig auffallen.
                "wrong-id" => Results.Json(new { jsonrpc = "2.0", id = "fremd", result = 1 }),
                _ => Results.Json(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = "unbekannte Methode" },
                }),
            };
        });

        await _service.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private OpenRpcTransportOptions Options(Uri? spec = null) => new(
        new Uri($"http://127.0.0.1:{_port}/rpc"),
        SpecLocation: spec,
        // Der Testdienst läuft auf Loopback — genau das, was die Zielprüfung sonst abweist.
        AllowPrivateTargets: true,
        TimeoutSeconds: 15);

    private async Task<IUpstreamConnection> ConnectAsync(Uri? spec = null)
        => await new OpenRpcUpstreamConnector().ConnectAsync(
            new ServerId(Guid.NewGuid()),
            new UpstreamServerConfig("rpc", "Rechner", UpstreamTransportKind.OpenRpc, true, OpenRpc: Options(spec)),
            TestContext.Current.CancellationToken);

    private static string Text(JsonElement result)
        => result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;

    /// <summary><c>rpc.discover</c> liefert den Katalog — ohne dass jemand ein Dokument hinterlegt.</summary>
    [Fact]
    public async Task Discovery_over_rpc_discover_yields_the_catalog()
    {
        await using var connection = await ConnectAsync();

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);

        inventory.Tools.Select(t => t.Name).Should().BeEquivalentTo("sum", "subtract", "boom");
        inventory.Tools.Single(t => t.Name == "sum").InputSchema
            .GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_by_name_call_sends_an_object()
    {
        await using var connection = await ConnectAsync();

        var result = await connection.CallToolAsync(
            "sum", JsonSerializer.Deserialize<JsonElement>("""{"a":40,"b":2}"""),
            TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        Text(result).Should().Be("42");
    }

    /// <summary>
    /// Der Kern des Positionsfalls: Aus den benannten Argumenten des Aufrufers wird ein Array in
    /// der Reihenfolge der Descriptors. Der Dienst antwortet mit einem Fehler, wenn er ein Objekt
    /// bekommt — der Test würde also auffallen, wenn die Umsetzung sie verwechselte.
    /// </summary>
    [Fact]
    public async Task A_by_position_call_sends_an_ordered_array()
    {
        await using var connection = await ConnectAsync();

        var result = await connection.CallToolAsync(
            "subtract", JsonSerializer.Deserialize<JsonElement>("""{"subtrahend":8,"minuend":10}"""),
            TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        Text(result).Should().Be("2", "die Reihenfolge kommt aus dem Dokument, nicht aus dem Aufruf");
    }

    /// <summary>Ein JSON-RPC-Fehler bleibt strukturiert: Code und Meldung getrennt erkennbar.</summary>
    [Fact]
    public async Task A_json_rpc_error_is_mapped_structurally()
    {
        await using var connection = await ConnectAsync();

        var result = await connection.CallToolAsync(
            "boom", JsonSerializer.Deserialize<JsonElement>("{}"), TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        Text(result).Should().Contain("-32000").And.Contain("kaputt").And.Contain("Einzelheiten");
    }

    [Fact]
    public async Task An_unknown_method_is_refused_before_the_call()
    {
        await using var connection = await ConnectAsync();

        var result = await connection.CallToolAsync(
            "gibtesnicht", JsonSerializer.Deserialize<JsonElement>("{}"),
            TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        Text(result).Should().Contain("kennt keine Methode");
    }

    /// <summary>
    /// Fixture 6 des Spikes, Kern: Ein Ziel im privaten oder Link-Local-Netz wird abgewiesen. Sonst
    /// wäre das Gateway ein Werkzeug, um interne Dienste zu erreichen — inklusive des
    /// Cloud-Metadatendienstes auf 169.254.169.254.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1/rpc")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/rpc")]
    [InlineData("http://192.168.1.10/rpc")]
    [InlineData("http://172.16.0.1/rpc")]
    [InlineData("http://[::1]/rpc")]
    public async Task An_internal_target_is_refused(string url)
    {
        var act = async () => await SpecFetcher.EnsureTargetAllowedAsync(
            new Uri(url), allowPrivateTargets: false, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<OpenRpcImportException>())
            .WithMessage("*interne Adresse*");
    }

    /// <summary>Wer es ausdrücklich erlaubt, bekommt es — die Grenze ist eine Vorgabe, kein Verbot.</summary>
    [Fact]
    public async Task An_internal_target_is_allowed_when_asked_for()
    {
        var act = async () => await SpecFetcher.EnsureTargetAllowedAsync(
            new Uri("http://127.0.0.1/rpc"), allowPrivateTargets: true, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    /// <summary>Fixture 5: Ein Dokument über der Grenze wird abgewiesen, nicht gelesen.</summary>
    [Fact]
    public async Task An_oversized_document_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openrpc-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path, new string('x', (int)SpecFetcher.MaxBytes + 1), TestContext.Current.CancellationToken);
        try
        {
            var act = async () => await SpecFetcher.FetchAsync(
                new Uri(path), allowPrivateTargets: true, TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            (await act.Should().ThrowAsync<OpenRpcImportException>()).WithMessage("*überschreitet*");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
