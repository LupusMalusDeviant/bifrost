using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Integration.Tests.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Operations;

/// <summary>
/// WP6.2 am laufenden Gateway: Der Auditexport und die Kettenprüfung über HTTP.
/// <para>
/// Der Kern ist <see cref="The_export_streams_instead_of_answering_with_one_document"/>: Die
/// Antwort ist eine Folge von Zeilen, kein JSON-Dokument. Das ist nicht Geschmackssache — ein
/// Dokument muss vollständig entstehen, bevor die erste Zeile hinausgeht.
/// </para>
/// </summary>
public sealed class AuditExportApiTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _fixture;

    public AuditExportApiTests(GatewayFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_export_streams_instead_of_answering_with_one_document()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        await ProduceAuditAsync(client, ct);

        using var response = await client.GetAsync(
            "/api/v1/audit/export?format=jsonl", HttpCompletionOption.ResponseHeadersRead, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/x-ndjson");

        var body = await response.Content.ReadAsStringAsync(ct);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().NotBeEmpty();

        foreach (var line in lines)
        {
            // Jede Zeile ist für sich vollständig — daran hängt, dass ein Empfänger mitlesen kann,
            // bevor der Export fertig ist.
            JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64()
                .Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Csv_carries_the_pinned_header_and_no_arguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        await ProduceAuditAsync(client, ct);

        var body = await client.GetStringAsync("/api/v1/audit/export?format=csv", ct);

        body.Split('\n')[0].Should().StartWith("sequence,timestamp,kind,origin,caller");
        body.Should().NotContain("redactedArguments");
    }

    /// <summary>
    /// Der Cursor ist das, was einen Export fortsetzbar macht: <c>after</c> nimmt die zuletzt
    /// gesehene Zeilennummer und liefert ausschließlich, was danach kam.
    /// </summary>
    [Fact]
    public async Task The_cursor_continues_where_the_last_export_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        await ProduceAuditAsync(client, ct);

        var erste = await LinesAsync(client, "/api/v1/audit/export?limit=1", ct);
        erste.Should().ContainSingle();
        var cursor = JsonDocument.Parse(erste[0]).RootElement.GetProperty("sequence").GetInt64();

        var weitere = await LinesAsync(client, $"/api/v1/audit/export?after={cursor}", ct);

        weitere.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("sequence").GetInt64())
            .Should().OnlyContain(sequence => sequence > cursor);
    }

    /// <summary>
    /// Die Prüfung läuft über das echte Log dieser Instanz — geschrieben vom echten Schreibpfad.
    /// Und sie nennt ihre Grenzen in der Antwort, nicht in der Dokumentation.
    /// </summary>
    [Fact]
    public async Task The_chain_of_the_running_instance_verifies_and_states_its_limits()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminClientAsync();
        await ProduceAuditAsync(client, ct);

        var report = await client.GetFromJsonAsync<JsonElement>("/api/v1/audit/verify", ct);

        report.GetProperty("isIntact").GetBoolean().Should().BeTrue();
        report.GetProperty("checked").GetInt64().Should().BeGreaterThan(0);
        report.GetProperty("limits").EnumerateArray().Should().NotBeEmpty();
        string.Join(" ", report.GetProperty("limits").EnumerateArray().Select(l => l.GetString()))
            .Should().Contain("übernommenen Wirt");
    }

    /// <summary>Das ganze Log ausleiten darf nur, wer verwalten darf — nicht jeder Angemeldete.</summary>
    [Fact]
    public async Task A_caller_without_management_rights_gets_no_export()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, ohneRechte) = await _fixture.SeedIdentityAsync($"audit-fremd-{Guid.NewGuid():N}", grants: []);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ohneRechte);

        var response = await client.GetAsync("/api/v1/audit/export", ct);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    private static async Task<List<string>> LinesAsync(HttpClient client, string path, CancellationToken ct)
        => [.. (await client.GetStringAsync(path, ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Erzeugt einen auditierten Vorgang und wartet, bis er in der Ablage steht. Der Schreiber
    /// arbeitet in Batches — ohne das Warten prüfte der Test ein noch leeres Log.
    /// </summary>
    private static async Task ProduceAuditAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/rbac/roles",
            new { id = new { value = Guid.NewGuid() }, name = $"audit-{Guid.NewGuid():N}"[..14], grants = Array.Empty<object>() },
            ct);
        response.IsSuccessStatusCode.Should().BeTrue();

        for (var i = 0; i < 100; i++)
        {
            if ((await client.GetStringAsync("/api/v1/audit/export?limit=1", ct)).Length > 0)
            {
                return;
            }

            await Task.Delay(100, ct);
        }

        Assert.Fail("Nach 10 s stand noch keine Auditzeile in der Ablage.");
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var (_, key) = await _fixture.SeedAdminAsync("audit-admin-" + Guid.NewGuid().ToString("N")[..8]);
        var client = _fixture.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }
}
