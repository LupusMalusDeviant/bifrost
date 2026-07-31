using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.TestServers;
using Bifrost.Upstream.Cli;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>Smoke-Tests für den CLI-Upstream (ADR-0014). Nutzt das ohnehin vorhandene
/// <c>dotnet</c>-Binary als harmloses, portables Ziel-Programm.</summary>
public class CliUpstreamConnectorTests
{
    private static readonly JsonElement NoArgs = JsonSerializer.Deserialize<JsonElement>("{}");
    private static readonly string DotnetHost = Path.GetFullPath(Path.Combine(
        RuntimeEnvironment.GetRuntimeDirectory(),
        "..",
        "..",
        "..",
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
    private static readonly string ProbeAssembly = typeof(CliProbeMarker).Assembly.Location;

    private static async Task<IUpstreamConnection> ConnectAsync(params CliToolSpec[] tools)
    {
        var config = new UpstreamServerConfig(
            "clitest", "CLI Test", UpstreamTransportKind.Cli, Enabled: true,
            Cli: new CliTransportOptions("dotnet", tools, AllowPathLookup: true));
        return await new CliUpstreamConnector()
            .ConnectAsync(new ServerId(Guid.NewGuid()), config, TestContext.Current.CancellationToken);
    }

    private static async Task<IUpstreamConnection> ConnectProbeAsync(
        IReadOnlyList<CliToolSpec> tools,
        int maxOutputBytes = 64 * 1024,
        int? timeoutSeconds = null,
        int maxConcurrency = 4,
        IReadOnlyDictionary<string, string>? environment = null,
        IReadOnlyList<string>? allowedReadRoots = null,
        IReadOnlyList<string>? allowedWriteRoots = null)
    {
        var config = new UpstreamServerConfig(
            "cliprobe", "CLI Probe", UpstreamTransportKind.Cli, Enabled: true,
            Cli: new CliTransportOptions(
                DotnetHost,
                tools,
                EnvironmentVariables: environment,
                TimeoutSeconds: timeoutSeconds,
                MaxOutputBytes: maxOutputBytes,
                AllowedExecutableRoots: [Path.GetDirectoryName(DotnetHost)!],
                AllowedReadRoots: allowedReadRoots,
                AllowedWriteRoots: allowedWriteRoots,
                MaxConcurrency: maxConcurrency));
        return await new CliUpstreamConnector()
            .ConnectAsync(new ServerId(Guid.NewGuid()), config, TestContext.Current.CancellationToken);
    }

    private static CliToolSpec Probe(
        string name,
        string command,
        IReadOnlyList<CliParameterSpec>? parameters = null,
        int? maxConcurrency = null)
        => new(
            name,
            FixedArguments: [ProbeAssembly, command],
            Parameters: parameters,
            MaxConcurrency: maxConcurrency);

    [Fact]
    public async Task Discover_lists_the_configured_tools_with_an_args_schema()
    {
        await using var connection = await ConnectAsync(
            new CliToolSpec("version", "dotnet-Version", ["--version"], AllowCallerArguments: true));

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);

        inventory.Tools.Should().ContainSingle();
        var tool = inventory.Tools[0];
        tool.Name.Should().Be("version");
        tool.Description.Should().Be("dotnet-Version");
        tool.InputSchema.GetProperty("properties").TryGetProperty("args", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Free_form_arguments_are_disabled_by_default()
    {
        await using var connection = await ConnectAsync(
            new CliToolSpec("version", FixedArguments: ["--version"]));

        var tool = (await connection.DiscoverAsync(TestContext.Current.CancellationToken)).Tools.Single();

        tool.InputSchema.GetProperty("properties").TryGetProperty("args", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Typed_manifest_produces_schema_and_risk_metadata()
    {
        await using var connection = await ConnectAsync(
            new CliToolSpec(
                "delete",
                Parameters:
                [
                    new CliParameterSpec(
                        "target",
                        "Target file",
                        CliParameterType.Path,
                        Flag: "--target",
                        Required: true,
                        PathAccess: CliPathAccess.Write,
                        MaxLength: 200),
                    new CliParameterSpec(
                        "force",
                        Type: CliParameterType.Boolean,
                        Flag: "--force"),
                ],
                Risk: CapabilityRisk.Destructive));

        var tool = (await connection.DiscoverAsync(TestContext.Current.CancellationToken)).Tools.Single();

        tool.Risk.Should().Be(CapabilityRisk.Destructive);
        tool.RequiresApproval.Should().BeTrue();
        var schema = tool.InputSchema;
        schema.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).Should().Contain("target");
        schema.GetProperty("properties").GetProperty("target")
            .GetProperty("type").GetString().Should().Be("string");
        schema.GetProperty("properties").GetProperty("force")
            .GetProperty("type").GetString().Should().Be("boolean");
    }

    [Fact]
    public async Task Successful_command_returns_output_without_error_flag()
    {
        await using var connection = await ConnectAsync(
            new CliToolSpec("version", FixedArguments: ["--version"], AllowCallerArguments: false));

        var result = await connection.CallToolAsync("version", NoArgs, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().MatchRegex(@"\d+\.\d+"); // eine Versionsnummer wie 10.0.xxx
    }

    [Fact]
    public async Task Nonzero_exit_is_surfaced_as_isError()
    {
        await using var connection = await ConnectAsync(
            new CliToolSpec("bad", FixedArguments: ["--this-flag-does-not-exist"], AllowCallerArguments: false));

        var result = await connection.CallToolAsync("bad", NoArgs, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_tool_is_rejected()
    {
        await using var connection = await ConnectAsync(new CliToolSpec("version", FixedArguments: ["--version"]));

        var act = () => connection.CallToolAsync("does-not-exist", NoArgs, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Typed_arguments_keep_order_and_shell_metacharacters_literal()
    {
        var tool = Probe(
            "args",
            "args",
            [
                new CliParameterSpec("value", Position: 0, Required: true),
                new CliParameterSpec(
                    "count", Type: CliParameterType.Integer, Flag: "--count", Required: true),
                new CliParameterSpec(
                    "force", Type: CliParameterType.Boolean, Flag: "--force"),
            ]);
        await using var connection = await ConnectProbeAsync([tool]);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            value = """; $(echo injected) & | "quoted" """,
            count = 3,
            force = true,
        });

        var result = await connection.CallToolAsync(
            "args", arguments, TestContext.Current.CancellationToken);

        var emitted = JsonSerializer.Deserialize<string[]>(
            result.GetProperty("cli").GetProperty("stdout").GetProperty("text").GetString()!);
        emitted.Should().Equal(
            """; $(echo injected) & | "quoted" """,
            "--count",
            "3",
            "--force");
    }

    [Fact]
    public async Task Host_environment_is_not_inherited_but_configured_values_are_available()
    {
        var variable = $"BIFROST_HOST_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "host-secret");
        try
        {
            await using var isolated = await ConnectProbeAsync(
                [Probe(
                    "env",
                    "env",
                    [new CliParameterSpec("name", Position: 0, Required: true)])]);
            var args = JsonSerializer.SerializeToElement(new { name = variable });

            var missing = await isolated.CallToolAsync(
                "env", args, TestContext.Current.CancellationToken);

            missing.GetProperty("cli").GetProperty("stdout").GetProperty("text")
                .GetString().Should().Be("<missing>");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        await using var configured = await ConnectProbeAsync(
            [Probe(
                "env",
                "env",
                [new CliParameterSpec("name", Position: 0, Required: true)])],
            environment: new Dictionary<string, string> { ["CONTROLLED_VALUE"] = "yes" });
        var present = await configured.CallToolAsync(
            "env",
            JsonSerializer.SerializeToElement(new { name = "CONTROLLED_VALUE" }),
            TestContext.Current.CancellationToken);
        present.GetProperty("cli").GetProperty("stdout").GetProperty("text")
            .GetString().Should().Be("yes");

        await using var secretConnection = await ConnectProbeAsync(
            [Probe(
                "env",
                "env",
                [new CliParameterSpec("name", Position: 0, Required: true)])],
            environment: new Dictionary<string, string>
            {
                ["CONTROLLED_SECRET"] = "configured-secret-123",
            });
        var redacted = await secretConnection.CallToolAsync(
            "env",
            JsonSerializer.SerializeToElement(new { name = "CONTROLLED_SECRET" }),
            TestContext.Current.CancellationToken);
        redacted.GetProperty("cli").GetProperty("stdout").GetProperty("text")
            .GetString().Should().Be("***");
    }

    [Fact]
    public async Task Stdout_and_stderr_are_byte_capped_while_streaming()
    {
        await using var connection = await ConnectProbeAsync(
            [Probe(
                "output",
                "output",
                [
                    new CliParameterSpec(
                        "stdout", Type: CliParameterType.Integer, Position: 0, Required: true),
                    new CliParameterSpec(
                        "stderr", Type: CliParameterType.Integer, Position: 1, Required: true),
                ])],
            maxOutputBytes: 1024);

        var result = await connection.CallToolAsync(
            "output",
            JsonSerializer.SerializeToElement(new { stdout = 200_000, stderr = 180_000 }),
            TestContext.Current.CancellationToken);

        foreach (var streamName in new[] { "stdout", "stderr" })
        {
            var stream = result.GetProperty("cli").GetProperty(streamName);
            stream.GetProperty("capturedBytes").GetInt32().Should().Be(1024);
            stream.GetProperty("truncated").GetBoolean().Should().BeTrue();
        }
        result.GetProperty("cli").GetProperty("stdout").GetProperty("totalBytes")
            .GetInt64().Should().Be(200_000);
        result.GetProperty("cli").GetProperty("stderr").GetProperty("totalBytes")
            .GetInt64().Should().Be(180_000);
    }

    [Fact]
    public async Task Nonzero_exit_preserves_stdout_and_stderr_separately()
    {
        await using var connection = await ConnectProbeAsync(
            [Probe(
                "dual",
                "dual",
                [
                    new CliParameterSpec("stdout", Position: 0, Required: true),
                    new CliParameterSpec("stderr", Position: 1, Required: true),
                    new CliParameterSpec(
                        "exitCode", Type: CliParameterType.Integer, Position: 2, Required: true),
                ])]);

        var result = await connection.CallToolAsync(
            "dual",
            JsonSerializer.SerializeToElement(new
            {
                stdout = "normal-output",
                stderr = "relevant-error",
                exitCode = 17,
            }),
            TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("cli").GetProperty("exitCode").GetInt32().Should().Be(17);
        result.GetProperty("cli").GetProperty("stdout").GetProperty("text")
            .GetString().Should().Be("normal-output");
        result.GetProperty("cli").GetProperty("stderr").GetProperty("text")
            .GetString().Should().Be("relevant-error");
        result.GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Contain("normal-output").And.Contain("relevant-error");
    }

    [Fact]
    public async Task Unicode_output_is_decoded_as_utf8()
    {
        await using var connection = await ConnectProbeAsync([Probe("unicode", "unicode")]);

        var result = await connection.CallToolAsync(
            "unicode", NoArgs, TestContext.Current.CancellationToken);

        result.GetProperty("cli").GetProperty("stdout").GetProperty("text")
            .GetString().Should().Be("Grüße 🌍 — 日本語");
    }

    [Fact]
    public async Task Timeout_is_distinct_from_caller_cancellation()
    {
        var sleep = Probe(
            "sleep",
            "sleep",
            [new CliParameterSpec(
                "milliseconds", Type: CliParameterType.Integer, Position: 0, Required: true)]);
        await using var timeoutConnection = await ConnectProbeAsync([sleep], timeoutSeconds: 1);

        var timedOut = await timeoutConnection.CallToolAsync(
            "sleep",
            JsonSerializer.SerializeToElement(new { milliseconds = 10_000 }),
            TestContext.Current.CancellationToken);

        timedOut.GetProperty("isError").GetBoolean().Should().BeTrue();
        timedOut.GetProperty("cli").GetProperty("timedOut").GetBoolean().Should().BeTrue();

        await using var cancelledConnection = await ConnectProbeAsync([sleep], timeoutSeconds: 30);
        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = () => cancelledConnection.CallToolAsync(
            "sleep",
            JsonSerializer.SerializeToElement(new { milliseconds = 10_000 }),
            callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Per_upstream_concurrency_limit_bounds_process_count()
    {
        var sleep = Probe(
            "sleep",
            "sleep",
            [new CliParameterSpec(
                "milliseconds", Type: CliParameterType.Integer, Position: 0, Required: true)]);
        await using var connection = await ConnectProbeAsync([sleep], maxConcurrency: 1);
        var arguments = JsonSerializer.SerializeToElement(new { milliseconds = 350 });
        var stopwatch = Stopwatch.StartNew();

        await Task.WhenAll(
            connection.CallToolAsync("sleep", arguments, TestContext.Current.CancellationToken),
            connection.CallToolAsync("sleep", arguments, TestContext.Current.CancellationToken));

        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task Timeout_kills_the_spawned_process_tree()
    {
        var spawn = Probe(
            "spawn",
            "spawn-child",
            [new CliParameterSpec(
                "milliseconds", Type: CliParameterType.Integer, Position: 0, Required: true)]);
        await using var connection = await ConnectProbeAsync([spawn], timeoutSeconds: 1);

        var result = await connection.CallToolAsync(
            "spawn",
            JsonSerializer.SerializeToElement(new { milliseconds = 10_000 }),
            TestContext.Current.CancellationToken);
        var childId = int.Parse(
            result.GetProperty("cli").GetProperty("stdout").GetProperty("text").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

        var childExited = false;
        for (var attempt = 0; attempt < 40 && !childExited; attempt++)
        {
            try
            {
                using var child = Process.GetProcessById(childId);
                childExited = child.HasExited;
            }
            catch (ArgumentException)
            {
                childExited = true;
            }

            if (!childExited)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        childExited.Should().BeTrue("der Timeout muss auch Kindprozesse beenden");
    }

    [Fact]
    public async Task Disposing_connection_cancels_an_active_process()
    {
        var sleep = Probe(
            "sleep",
            "sleep",
            [new CliParameterSpec(
                "milliseconds", Type: CliParameterType.Integer, Position: 0, Required: true)]);
        var connection = await ConnectProbeAsync([sleep], timeoutSeconds: 30);
        var call = connection.CallToolAsync(
            "sleep",
            JsonSerializer.SerializeToElement(new { milliseconds = 10_000 }),
            TestContext.Current.CancellationToken);

        await Task.Delay(150, TestContext.Current.CancellationToken);
        await connection.DisposeAsync();
        var act = async () => await call;

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Path_parameters_cannot_escape_their_access_roots()
    {
        var allowedRoot = Path.Combine(Path.GetTempPath(), $"bifrost-cli-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(allowedRoot);
        var allowedFile = Path.Combine(allowedRoot, "allowed.txt");
        await File.WriteAllTextAsync(
            allowedFile, "ok", TestContext.Current.CancellationToken);
        var outsideFile = Path.GetTempFileName();
        try
        {
            var tool = Probe(
                "path",
                "args",
                [new CliParameterSpec(
                    "path",
                    Type: CliParameterType.Path,
                    Position: 0,
                    Required: true,
                    PathAccess: CliPathAccess.ReadOnly)]);
            await using var connection = await ConnectProbeAsync(
                [tool], allowedReadRoots: [allowedRoot]);

            var allowed = await connection.CallToolAsync(
                "path",
                JsonSerializer.SerializeToElement(new { path = allowedFile }),
                TestContext.Current.CancellationToken);
            allowed.GetProperty("isError").GetBoolean().Should().BeFalse();

            var act = () => connection.CallToolAsync(
                "path",
                JsonSerializer.SerializeToElement(new { path = outsideFile }),
                TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            File.Delete(outsideFile);
            Directory.Delete(allowedRoot, recursive: true);
        }
    }
}
