using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.Wasi;
using Xunit;

namespace Bifrost.Upstream.Tests;

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
            ? "Bifrost.TestServers.WasiHostStub.exe"
            : "Bifrost.TestServers.WasiHostStub");

    private static readonly JsonElement NoArgs = JsonSerializer.Deserialize<JsonElement>("{}");

    private static readonly JsonElement TypedArguments =
        JsonSerializer.Deserialize<JsonElement>("""{"value":21}""");

    private string _componentPath = string.Empty;
    private string _signaturePath = string.Empty;

    public async ValueTask InitializeAsync()
    {
        // Der Stub prüft die Signatur nicht — Inhalt und Länge sind hier egal, der Pfad zählt.
        _componentPath = Path.Combine(Path.GetTempPath(), $"bifrost-{Guid.NewGuid():N}.wasm");
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

    /// <summary>Der Stub prüft die Signatur nicht — entscheidend ist, dass überhaupt ein Schlüssel
    /// aus dem Trust-Store kommt (WP4); das Config-Feld ist nur noch Migrationsballast.</summary>
    private static readonly IPublisherTrustStore Trust =
        new FakePublisherTrustStore(Convert.ToBase64String(new byte[32]));

    private UpstreamServerConfig Config(params string[] hostArguments) => new(
        "wasi", "WASI", UpstreamTransportKind.Wasi, Enabled: true,
        Wasi: new WasiTransportOptions(
            StubHost,
            _componentPath,
            _signaturePath,
            [],
            Grants: new WasiCapabilityGrants(Environment: ["MCPMCP_SPIKE"]),
            HostArguments: hostArguments));

    private Task<IUpstreamConnection> ConnectAsync(params string[] hostArguments)
        => new WasiRuntimeConnector(Trust).ConnectAsync(
            new ServerId(Guid.NewGuid()), Config(hostArguments), TestContext.Current.CancellationToken);

    [Fact]
    public void Connector_declares_the_wasi_transport()
        => new WasiRuntimeConnector(Trust).Kind.Should().Be(UpstreamTransportKind.Wasi);

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
        var value = typed.InputSchema.GetProperty("properties").GetProperty("value");
        value.GetProperty("type").GetString().Should().Be("integer");
        value.GetProperty("minimum").GetInt64().Should().Be(int.MinValue, "s32 ist begrenzt");
        typed.InputSchema.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).Should().Equal("value");
        typed.InputSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        // Was der Host nicht aufrufen kann, gehört nicht in den Katalog.
        inventory.Tools.Select(tool => tool.Name).Should().NotContain("grow");
    }

    [Fact]
    public async Task Binary_and_result_types_become_real_schemas()
    {
        await using var connection = await ConnectAsync();

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);

        // list<u8> ist ein Base64-String, kein Zahlen-Array (ADR-0017).
        var blob = inventory.Tools.Single(tool => tool.Name == "blob");
        var data = blob.InputSchema.GetProperty("properties").GetProperty("data");
        data.GetProperty("type").GetString().Should().Be("string");
        data.GetProperty("contentEncoding").GetString().Should().Be("base64");

        // Der Typbaum ist der Grund, warum das Schema mehr als "object" sagen kann.
        var classify = inventory.Tools.Single(tool => tool.Name == "classify");
        classify.InputSchema.GetProperty("properties").GetProperty("value")
            .GetProperty("maximum").GetUInt32().Should().Be(uint.MaxValue, "u32 ist begrenzt");
    }

    [Fact]
    public async Task An_interface_function_gets_a_catalog_name_and_a_nested_schema()
    {
        await using var connection = await ConnectAsync();

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);

        // Der Interface-Name wird katalogtauglich, der rohe Pfad bleibt in der Beschreibung.
        var place = inventory.Tools.Single(tool => tool.Name == "demo_shapes_api_place");
        place.Description.Should().Contain("demo:shapes/api@1.0.0.place");

        // Der Record wird zu einem verschachtelten Schema, nicht zu "object".
        var point = place.InputSchema.GetProperty("properties").GetProperty("point");
        point.GetProperty("type").GetString().Should().Be("object");
        point.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("x", "colour");
        point.GetProperty("properties").GetProperty("colour").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).Should().Equal("rot", "gruen");
        point.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task An_interface_function_is_callable_under_its_catalog_name()
    {
        await using var connection = await ConnectAsync();
        var args = JsonSerializer.Deserialize<JsonElement>("""{"point":{"x":7,"colour":"rot"}}""");

        var result = await connection.CallToolAsync(
            "demo_shapes_api_place", args, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("7");
    }

    [Fact]
    public async Task Binary_arguments_and_results_travel_as_base64()
    {
        await using var connection = await ConnectAsync();
        var payload = Convert.ToBase64String(new byte[] { 0, 127, 255 });
        var args = JsonSerializer.Deserialize<JsonElement>($$"""{"data":"{{payload}}"}""");

        var result = await connection.CallToolAsync("blob", args, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString().Should().Be(payload,
            "ein String-Ergebnis geht roh durch, nicht mit Anführungszeichen");
    }

    [Fact]
    public async Task A_result_type_reaches_the_caller_as_json()
    {
        await using var connection = await ConnectAsync();
        var even = JsonSerializer.Deserialize<JsonElement>("""{"value":4}""");
        var odd = JsonSerializer.Deserialize<JsonElement>("""{"value":7}""");

        var ok = await connection.CallToolAsync("classify", even, TestContext.Current.CancellationToken);
        var err = await connection.CallToolAsync("classify", odd, TestContext.Current.CancellationToken);

        // Zusammengesetzte Rückgaben behalten ihre Struktur, statt zu einer Zahl zu verkümmern.
        ok.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("""{"ok":"gerade"}""");
        err.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("""{"err":7}""");
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

    /// <summary>
    /// Ein Host ohne Pflichtfeature kommt nicht hoch (ADR-0016). Der Fehler fällt beim Handshake,
    /// nicht beim ersten Aufruf, der das Feature gebraucht hätte.
    /// </summary>
    [Fact]
    public async Task A_host_missing_a_required_feature_fails_the_connection()
    {
        var act = () => ConnectAsync("--no-drain");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*drain*");
    }

    /// <summary>Health prüft Bereitschaft, nicht nur Leben — und der geladene Host ist bereit.</summary>
    [Fact]
    public async Task Ping_accepts_a_ready_host()
    {
        await using var connection = await ConnectAsync();

        var act = () => connection.PingAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
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
            new Uri("bifrost://x"), TestContext.Current.CancellationToken);
        var getPrompt = () => connection.GetPromptAsync("p", null, TestContext.Current.CancellationToken);

        await readResource.Should().ThrowAsync<NotSupportedException>();
        await getPrompt.Should().ThrowAsync<NotSupportedException>();
    }
}
