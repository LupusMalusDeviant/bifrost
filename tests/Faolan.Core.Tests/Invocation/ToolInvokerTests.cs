using System.Text.Json;
using AwesomeAssertions;
using Faolan.Abstractions;
using Faolan.Core.Tests.Upstreams;
using Faolan.Core.Upstreams;
using Xunit;

namespace Faolan.Core.Tests.Invocation;

/// <summary>WP4.1: Statusmatrix der Pipeline — jeder Pfad endet in genau einem Result und genau einem Audit-Event.</summary>
public class ToolInvokerTests
{
    private readonly InvokerTestWorld _w = new();

    [Fact]
    public async Task Success_passes_original_tool_name_and_audits_once()
    {
        var admin = _w.RegisterAdmin();

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Success);
        result.Content!.Value.GetProperty("tool").GetString().Should().Be("echo");
        _w.Connection.LastToolName.Should().Be("echo", "der Upstream sieht den Original-Namen ohne Namespace");

        var evt = _w.Audit.Events.Should().ContainSingle().Subject;
        evt.Status.Should().Be(InvocationStatus.Success);
        evt.Caller.Should().Be(admin);
        evt.Server.Should().Be(_w.Server);
        evt.Tool.Should().Be(_w.Echo.Value);
        evt.CallerRoles.Should().Contain("rolle").And.Contain("Profil",
            "FR-21 verlangt Profil/Rolle des Aufrufers, nicht nur die Id");
    }

    [Fact]
    public async Task Rate_limited_caller_is_denied_and_audited()
    {
        var admin = _w.RegisterAdmin();
        _w.RateLimiter.Allow = false;

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Denied);
        result.ErrorMessage.Should().Contain("Rate-Limit");
        _w.Connection.LastToolName.Should().BeNull("der Upstream darf nie erreicht werden");
        _w.Audit.Events.Should().ContainSingle().Which.Status.Should().Be(InvocationStatus.Denied);
    }

    [Fact]
    public async Task Unknown_tool_returns_not_found_and_audits()
    {
        var admin = _w.RegisterAdmin();

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, NamespacedToolName.Create(_w.Slug, "gibtsnicht")), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.ToolNotFound);
        _w.Audit.Events.Should().ContainSingle().Which.Server.Should().BeNull();
    }

    [Fact]
    public async Task Rbac_denial_blocks_call_with_reason_and_audit()
    {
        var restricted = _w.RegisterAgent(); // keine Grants

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(restricted, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Denied);
        result.ErrorMessage.Should().Contain("Default-Deny");
        _w.Connection.LastToolName.Should().BeNull();
        _w.Audit.Events.Should().ContainSingle().Which.Status.Should().Be(InvocationStatus.Denied);
    }

    [Fact]
    public async Task Invalid_arguments_fail_schema_validation_before_upstream()
    {
        var admin = _w.RegisterAdmin();

        var missingRequired = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { falsch = 1 }), TestContext.Current.CancellationToken);
        var wrongType = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = 42 }), TestContext.Current.CancellationToken);
        var undefinedArgs = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo), TestContext.Current.CancellationToken);

        missingRequired.Status.Should().Be(InvocationStatus.ValidationFailed);
        wrongType.Status.Should().Be(InvocationStatus.ValidationFailed);
        undefinedArgs.Status.Should().Be(InvocationStatus.ValidationFailed, "fehlende Argumente gegen required-Schema");
        _w.Connection.LastToolName.Should().BeNull();
        _w.Audit.Events.Should().HaveCount(3);
    }

    [Fact]
    public async Task Unparseable_schema_falls_back_to_passthrough()
    {
        var admin = _w.RegisterAdmin();

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Free, new { irgendwas = true }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Success,
            "R3-Fallback: nicht validierbare Schemas dürfen den Call nicht fälschlich ablehnen");
    }

    [Fact]
    public async Task Missing_connection_yields_upstream_error()
    {
        var admin = _w.RegisterAdmin();
        _w.Supervisor.SetConnection(_w.Server, null);

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.UpstreamError);
        result.ErrorMessage.Should().Contain("nicht verbunden");
        _w.Audit.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task Upstream_exception_maps_to_upstream_error()
    {
        var admin = _w.RegisterAdmin();
        _w.Connection.CallException = new IOException("Rohr geplatzt");

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.UpstreamError);
        result.ErrorMessage.Should().Contain("Rohr geplatzt");
    }

    /// <summary>
    /// Codex H7: Der Antwortkörper des Upstreams geht nicht an den Agenten — er kann den vorgelegten
    /// Schlüssel zitieren. Weder in der Meldung noch im Trace-Status.
    /// </summary>
    [Fact]
    public async Task An_upstream_failure_does_not_pass_the_response_body_on()
    {
        var secret = "sk-" + Guid.NewGuid().ToString("N");
        var admin = _w.RegisterAdmin();
        _w.Connection.CallException = new HttpRequestException(
            "Response status code does not indicate success: 500 (Internal Server Error). Response body: "
            + $"{{\"error\":\"kaputt mit Bearer {secret}\"}}",
            null, System.Net.HttpStatusCode.InternalServerError);

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.UpstreamError);
        result.ErrorMessage.Should().NotContain(secret).And.Contain("HTTP 500");
    }

    /// <summary>Codex H7: Auch ein Fremdtext ohne Statuscode wird auf eine Zeile ohne Antwortkörper gebracht.</summary>
    [Fact]
    public async Task An_upstream_error_text_is_cut_at_the_response_body()
    {
        var secret = "sk-" + Guid.NewGuid().ToString("N");
        var admin = _w.RegisterAdmin();
        _w.Connection.CallException = new IOException($"Rohr geplatzt.\nResponse body: {secret}");

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.ErrorMessage.Should().Contain("Rohr geplatzt").And.NotContain(secret).And.NotContain("\n");
    }

    /// <summary>
    /// Codex H7: Hinter einer echten Verbindung des Supervisors ist die Konfiguration bekannt — dann
    /// werden auch ihre Geheimwerte maskiert, wo der Upstream sie ohne Kennzeichnung zitiert.
    /// </summary>
    [Fact]
    public async Task A_guarded_connection_masks_the_configured_secret()
    {
        var secret = "sk-" + Guid.NewGuid().ToString("N");
        var config = new UpstreamServerConfig(
            "geheim", "Geheim", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                new Uri("https://upstream.invalid/mcp"),
                Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret }));
        var guarded = new GuardedUpstreamConnection(
            new FakeUpstreamConnection { Id = _w.Server, CallException = new IOException($"abgelehnt: {secret}") },
            TimeSpan.FromSeconds(30), config: config);
        _w.Supervisor.SetConnection(_w.Server, guarded);
        var admin = _w.RegisterAdmin();

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.ErrorMessage.Should().Contain("abgelehnt").And.NotContain(secret);
    }

    /// <summary>
    /// Plan 0005 D1: Das vorgelegte OAuth-Token steht in keiner Konfiguration — maskiert wird es
    /// trotzdem, auch im Zweig der Zeitüberschreitung.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_guarded_connection_masks_the_presented_token(bool timeout)
    {
        var token = "at-" + Guid.NewGuid().ToString("N");
        var config = new UpstreamServerConfig(
            "oauth", "OAuth", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(new Uri("https://upstream.invalid/mcp")));
        Exception failure = timeout
            ? new TimeoutException($"zu langsam mit {token}")
            : new IOException($"{{\"jsonrpc\":\"2.0\",\"error\":{{\"message\":\"Bearer {token} abgelehnt\"}}}}");
        var guarded = new GuardedUpstreamConnection(
            new FakeUpstreamConnection { Id = _w.Server, CallException = failure, PresentedCredentials = [token] },
            TimeSpan.FromSeconds(30), config: config);
        _w.Supervisor.SetConnection(_w.Server, guarded);
        var admin = _w.RegisterAdmin();

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(timeout ? InvocationStatus.Timeout : InvocationStatus.UpstreamError);
        result.ErrorMessage.Should().NotBeNullOrEmpty().And.NotContain(token);
        _w.Audit.Events.Should().ContainSingle().Which.Detail.Should().NotContain(token);
    }

    [Fact]
    public async Task Upstream_timeout_exception_maps_to_timeout()
    {
        var admin = _w.RegisterAdmin();
        _w.Connection.CallException = new TimeoutException("zu langsam");

        var result = await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        result.Status.Should().Be(InvocationStatus.Timeout);
        _w.Audit.Events.Should().ContainSingle().Which.Status.Should().Be(InvocationStatus.Timeout);
    }

    [Fact]
    public async Task Timeout_override_cancels_hanging_call()
    {
        var admin = _w.RegisterAdmin();
        _w.Connection.CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        var call = _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }, timeoutOverride: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);
        await TestData.WaitUntilAdvancingAsync(_w.Time, () => call.IsCompleted, because: "Override-Timeout muss greifen");

        (await call).Status.Should().Be(InvocationStatus.Timeout);
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_as_timeout_result()
    {
        var admin = _w.RegisterAdmin();
        _w.Connection.CallGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var call = _w.Invoker.InvokeAsync(InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), cts.Token);
        await TestData.WaitUntilAsync(() => _w.Connection.LastToolName == "echo");
        await cts.CancelAsync();

        var result = await call;
        result.Status.Should().Be(InvocationStatus.Timeout);
        result.ErrorMessage.Should().Contain("abgebrochen");
        _w.Audit.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task Response_payloads_are_only_audited_in_debug_mode_and_stay_redacted()
    {
        var admin = _w.RegisterAdmin();

        // Default: kein Ergebnis-Payload im Log (FR-24/NFR-04).
        await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);
        _w.Audit.Events.Should().ContainSingle().Which.RedactedResponse
            .Should().BeNull("Ergebnis-Payloads sind ohne ausdrücklichen Debug-Modus tabu");

        var debug = _w.WithAuditOptions(new AuditOptions(CaptureResponsePayloads: true));
        await debug.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Echo, new { message = "hi" }), TestContext.Current.CancellationToken);

        var evt = _w.Audit.Events[^1];
        evt.RedactedResponse.Should().NotBeNull("im Debug-Modus wird der Payload mitgeschrieben");
        evt.RedactedResponse!.Value.GetRawText()
            .Should().Contain("echo").And.NotContain("geheim-antwort",
                "auch im Debug-Modus läuft der Payload durch die Redaction");
    }

    [Fact]
    public async Task Audit_arguments_are_redacted()
    {
        var admin = _w.RegisterAdmin();

        await _w.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, _w.Free, new { message = "offen", password = "geheim123" }),
            TestContext.Current.CancellationToken);

        var evt = _w.Audit.Events.Should().ContainSingle().Subject;
        var json = evt.RedactedArguments!.Value.GetRawText();
        json.Should().Contain("offen").And.NotContain("geheim123");
    }

    [Fact]
    public async Task Write_only_manifest_parameters_are_redacted_even_with_neutral_names()
    {
        var world = new InvokerTestWorld(echoSensitive: true);
        var admin = world.RegisterAdmin();

        await world.Invoker.InvokeAsync(
            InvokerTestWorld.Request(admin, world.Echo, new { message = "raw-sensitive-value" }),
            TestContext.Current.CancellationToken);

        var json = world.Audit.Events.Should().ContainSingle()
            .Subject.RedactedArguments!.Value.GetRawText();
        json.Should().Contain("***").And.NotContain("raw-sensitive-value");
    }
}
