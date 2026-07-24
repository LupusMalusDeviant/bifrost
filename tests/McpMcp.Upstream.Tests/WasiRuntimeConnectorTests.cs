using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Upstream.Wasi;
using Xunit;

namespace McpMcp.Upstream.Tests;

/// <summary>
/// Testet den WASI-Connector (ADR-0020, Plan 0003/WP2) gegen einen Stub-Host, der denselben
/// IPC-Vertrag spricht. Das prüft die .NET-Seite — Framing, Handshake, Load, Discovery,
/// Argumentübergabe und Fehlerabbildung — deterministisch und ohne Rust-Toolchain. Die
/// Runtime-Semantik selbst (Signaturprüfung, Grants, Limits) ist auf der Rust-Seite getestet;
/// die Wire-Kompatibilität beider Seiten belegt <see cref="WasiRealHostCompatibilityTests"/>
/// gegen das echte Binary (Plan 0003, WP6.2).
/// </summary>
public class WasiRuntimeConnectorTests : IAsyncLifetime
{
    private static readonly string StubHost = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows()
            ? "McpMcp.TestServers.WasiHostStub.exe"
            : "McpMcp.TestServers.WasiHostStub");

    private static readonly JsonElement NoArgs = JsonSerializer.Deserialize<JsonElement>("{}");

    private static readonly JsonElement TypedArguments =
        JsonSerializer.Deserialize<JsonElement>("""{"value":21}""");

    private string _componentPath = string.Empty;
    private string _signaturePath = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Der Stub prüft die Signatur nicht — Inhalt und Länge sind hier egal, der Pfad zählt.
        _componentPath = Path.Combine(Path.GetTempPath(), $"mcpmcp-{Guid.NewGuid():N}.wasm");
        _signaturePath = Path.ChangeExtension(_componentPath, ".sig");
        await File.WriteAllBytesAsync(_componentPath, [0x00, 0x61, 0x73, 0x6D], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(_signaturePath, new byte[64], TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(_componentPath);
        File.Delete(_signaturePath);
        return ValueTask.CompletedTask;
    }

    private UpstreamServerConfig Config(params string[] hostArguments) => new(
        "wasi", "WASI", UpstreamTransportKind.Wasi, Enabled: true,
        Wasi: new WasiTransportOptions(
            StubHost,
            _componentPath,
            _signaturePath,
            [Convert.ToBase64String(new byte[32])],
            Grants: new WasiCapabilityGrants(Environment: ["MCPMCP_SPIKE"]),
            HostArguments: hostArguments));

    private Task<IUpstreamConnection> ConnectAsync(params string[] hostArguments)
        => new WasiRuntimeConnector().ConnectAsync(
            new ServerId(Guid.NewGuid()), Config(hostArguments), TestContext.Current.CancellationToken);

    [Fact]
    public void Connector_declares_the_wasi_transport()
        => new WasiRuntimeConnector().Kind.Should().Be(UpstreamTransportKind.Wasi);

    [Fact]
    public async Task Connect_performs_the_handshake_and_lists_the_components_tools()
    {
        await using var connection = await ConnectAsync();

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);

        // Katalognamen sind normalisiert (WP6.1) — der rohe Export steht in der Beschreibung.
        inventory.Tools.Select(tool => tool.Name).Should().Contain(["wasi_cli_run", "double"]);
        inventory.Tools.Single(tool => tool.Name == "wasi_cli_run").Description
            .Should().Contain("wasi:cli/run@0.2.6");
        inventory.Resources.Should().BeEmpty();
        inventory.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_tool_carries_its_own_schema_and_unsupported_exports_stay_out()
    {
        await using var connection = await ConnectAsync();

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);

        // Kommando-Export: keine Argumente.
        var command = inventory.Tools.Single(tool => tool.Name == "wasi_cli_run");
        command.InputSchema.GetProperty("properties").EnumerateObject().Should().BeEmpty();

        // Typisierter Export: der echte Parametername mit passendem JSON-Typ, strikt geschlossen.
        var typed = inventory.Tools.Single(tool => tool.Name == "double");
        typed.InputSchema.GetProperty("properties").GetProperty("value").GetProperty("type")
            .GetString().Should().Be("integer");
        typed.InputSchema.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).Should().Equal("value");
        typed.InputSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        // Was der Host nicht aufrufen kann, gehört nicht in den Katalog.
        inventory.Tools.Select(tool => tool.Name).Should().NotContain("grow");
    }

    [Fact]
    public async Task Export_names_become_catalog_safe_names_without_losing_the_mapping()
    {
        await using var connection = await ConnectAsync("--odd-names");

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);

        // Versionsanhang weg, Sonderzeichen zu '_', Kollision deterministisch entschärft,
        // ein rein zerfallender Name bekommt einen Ersatz.
        inventory.Tools.Select(tool => tool.Name).Should().Equal(
            "ns_pkg_iface", "ns_pkg_iface_2", "weird_name", "tool");
        inventory.Tools.Select(tool => tool.Name).Should().OnlyContain(
            name => name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'),
            "Katalognamen landen in MCP-Tool-Namen und REST-Pfaden");

        // Aufrufbar bleibt es trotzdem: der Connector kennt den rohen Export dahinter.
        var result = await connection.CallToolAsync(
            "ns_pkg_iface_2", NoArgs, TestContext.Current.CancellationToken);
        result.GetProperty("isError").GetBoolean().Should().BeTrue(
            "der Stub kennt diesen Export beim Aufruf nicht — entscheidend ist, dass der rohe Name gesendet wurde");
        result.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("ns:pkg/iface@2.0.0");
    }

    [Fact]
    public async Task Invoking_a_command_export_returns_its_output()
    {
        await using var connection = await ConnectAsync();

        var result = await connection.CallToolAsync(
            "wasi_cli_run", NoArgs, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("stub-guest-ok");
    }

    [Fact]
    public async Task A_missing_argument_is_an_error_result_instead_of_a_silent_default()
    {
        await using var connection = await ConnectAsync();

        var result = await connection.CallToolAsync("double", NoArgs, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Contain("value");
    }

    [Fact]
    public async Task Typed_arguments_reach_the_host()
    {
        await using var connection = await ConnectAsync();
        var args = TypedArguments;

        var result = await connection.CallToolAsync("double", args, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("42");
    }

    [Fact]
    public async Task An_unknown_tool_is_surfaced_as_an_error_result_not_an_exception()
    {
        await using var connection = await ConnectAsync();

        var result = await connection.CallToolAsync("nope", NoArgs, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_protocol_mismatch_fails_the_connection()
    {
        var act = () => ConnectAsync("--bad-protocol");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Protokoll*");
    }

    [Fact]
    public async Task A_rejected_load_fails_the_connection()
    {
        // Der Host weist eine ungültige Signatur ab — der Upstream darf NICHT hochkommen.
        var act = () => ConnectAsync("--reject-load");

        await act.Should().ThrowAsync<WasiHostException>()
            .WithMessage("*load-rejected*");
    }

    [Fact]
    public async Task Health_probe_succeeds_while_the_host_lives()
    {
        await using var connection = await ConnectAsync();

        var act = () => connection.PingAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Resources_and_prompts_are_not_supported()
    {
        await using var connection = await ConnectAsync();

        var readResource = () => connection.ReadResourceAsync(
            new Uri("mcpmcp://x"), TestContext.Current.CancellationToken);
        var getPrompt = () => connection.GetPromptAsync("p", null, TestContext.Current.CancellationToken);

        await readResource.Should().ThrowAsync<NotSupportedException>();
        await getPrompt.Should().ThrowAsync<NotSupportedException>();
    }
}
