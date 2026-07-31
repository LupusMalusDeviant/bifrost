using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Core.Webhooks;

namespace Bifrost.Server;

/// <summary>
/// Der eingehende Webhook-Trigger (FR-20, ADR-0013) — der EINZIGE unauthentifizierte Eingang des
/// Gateways. Deshalb bewusst eng: nur POST, nur mit gültiger HMAC-Signatur, nur an eine
/// registrierte Id. Der ausgelöste Tool-Call läuft durch dieselbe Pipeline wie jeder andere
/// (RBAC, Guardrail, Rate-Limit, Audit) unter der fest gebundenen Identität.
/// </summary>
public static class WebhookEndpoint
{
    private static readonly TimeSpan ReplayTolerance = TimeSpan.FromMinutes(5);

    public static void MapWebhookEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/{id:guid}/trigger", async (
            Guid id, HttpContext ctx, IWebhookStore store, IToolInvoker invoker, TimeProvider time,
            CancellationToken ct) =>
        {
            // Body als Rohtext lesen — die Signatur geht über exakt diese Bytes.
            ctx.Request.EnableBuffering();
            using var reader = new StreamReader(ctx.Request.Body, leaveOpen: false);
            var body = await reader.ReadToEndAsync(ct);

            var lookup = await store.GetForVerificationAsync(id, ct);
            if (lookup is null)
            {
                // Kein Hinweis, ob die Id existiert — kein Enumerations-Leak.
                return Results.Unauthorized();
            }

            var (definition, secret) = lookup.Value;
            var verification = WebhookSignature.Verify(
                secret,
                ctx.Request.Headers[WebhookSignature.SignatureHeader],
                ctx.Request.Headers[WebhookSignature.TimestampHeader],
                body,
                time.GetUtcNow(),
                ReplayTolerance);

            if (verification is not WebhookVerification.Valid)
            {
                return Results.Unauthorized();
            }

            // Payload als Argumente durchreichen. Kein JSON: leeres Objekt — das Schema des Tools
            // entscheidet dann über Gültigkeit, wie bei jedem anderen Call.
            JsonElement args;
            try
            {
                args = string.IsNullOrWhiteSpace(body)
                    ? JsonSerializer.Deserialize<JsonElement>("{}")
                    : JsonSerializer.Deserialize<JsonElement>(body);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Webhook-Payload ist kein gültiges JSON." });
            }

            var result = await invoker.InvokeAsync(
                new ToolInvocationRequest(definition.Caller, CallOrigin.Webhook, definition.Tool, args, null),
                ct);

            // Nach außen bewusst knapp: ob und wie der Call lief, ist eine interne Information;
            // der Auslöser bekommt nur, ob der Trigger angenommen wurde.
            return result.Status switch
            {
                InvocationStatus.Success => Results.Accepted(),
                InvocationStatus.ApprovalRequired => Results.Accepted(),
                InvocationStatus.Denied => Results.StatusCode(StatusCodes.Status403Forbidden),
                InvocationStatus.ToolNotFound => Results.NotFound(),
                _ => Results.StatusCode(StatusCodes.Status502BadGateway),
            };
        });
    }
}
