using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// FR-20 / ADR-0013 durch den echten Host: Ein signierter Webhook löst den Tool-Call aus, ein
/// unsignierter oder falsch signierter wird abgewiesen — und der ausgelöste Call landet mit
/// Herkunft Webhook im Audit.
/// </summary>
public sealed class WebhookE2ETests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public WebhookE2ETests(GatewayFixture gw) => _gw = gw;

    private IWebhookStore Store => _gw.Services.GetRequiredService<IWebhookStore>();

    [Fact]
    public async Task Signed_webhook_triggers_the_tool_call_and_is_audited_as_webhook_origin()
    {
        await _gw.AddEchoUpstreamAsync("hook1");
        var (identity, _) = await _gw.SeedAdminAsync("hook-caller");
        var tool = new NamespacedToolName("hook1__echo");

        var (def, secret) = await Store.CreateAsync("test-hook", identity, tool, TestContext.Current.CancellationToken);

        using var client = _gw.CreateDefaultClient();
        var body = """{"message":"vom webhook"}""";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{def.Id}/trigger")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(WebhookSignature.TimestampHeader, ts.ToString());
        req.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Compute(secret, ts, body));

        var response = await client.SendAsync(req, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, "gültige Signatur → Trigger angenommen");

        // Der ausgelöste Call muss im Audit stehen, mit Herkunft Webhook und der gebundenen Identität.
        // Der Batch-Writer schreibt asynchron — kurz nachfassen.
        PagedResult<AuditEvent>? audited = null;
        for (var i = 0; i < 50; i++)
        {
            audited = await _gw.AuditQuery.QueryAsync(
                new AuditFilter(Origin: CallOrigin.Webhook, ToolPrefix: "hook1__echo"),
                TestContext.Current.CancellationToken);
            if (audited.TotalCount >= 1) { break; }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        audited!.TotalCount.Should().BeGreaterThanOrEqualTo(1, "der Webhook-Call gehört ins Audit");
        audited.Items.Should().Contain(e => e.Caller == identity && e.Tool == tool.Value);
    }

    [Fact]
    public async Task Unsigned_webhook_is_rejected_without_running_the_tool()
    {
        await _gw.AddEchoUpstreamAsync("hook2");
        var (identity, _) = await _gw.SeedAdminAsync("hook-caller-2");
        var (def, _) = await Store.CreateAsync(
            "test-hook-2", identity, new NamespacedToolName("hook2__echo"), TestContext.Current.CancellationToken);

        using var client = _gw.CreateDefaultClient();

        // Ohne Signatur-Header.
        var response = await client.PostAsync(
            $"/webhooks/{def.Id}/trigger",
            new StringContent("""{"message":"ungebeten"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tampered_body_is_rejected()
    {
        var (identity, _) = await _gw.SeedAdminAsync("hook-caller-3");
        var (def, secret) = await Store.CreateAsync(
            "test-hook-3", identity, new NamespacedToolName("hook3__echo"), TestContext.Current.CancellationToken);

        using var client = _gw.CreateDefaultClient();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSignature.Compute(secret, ts, """{"message":"original"}""");

        // Signatur über das Original, aber ein anderer Body geht raus.
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/{def.Id}/trigger")
        {
            Content = new StringContent("""{"message":"manipuliert"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(WebhookSignature.TimestampHeader, ts.ToString());
        req.Headers.Add(WebhookSignature.SignatureHeader, signature);

        var response = await client.SendAsync(req, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "der Body ist Teil der Signatur");
    }

    [Fact]
    public async Task Unknown_webhook_id_is_unauthorized_not_notfound()
    {
        using var client = _gw.CreateDefaultClient();

        var response = await client.PostAsync(
            $"/webhooks/{Guid.NewGuid()}/trigger",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // Kein Unterschied zwischen "existiert nicht" und "falsch signiert" — kein Enumerations-Leak.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
