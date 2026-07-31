using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.Cli;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// ADR-0018 an einer <b>laufenden</b> Container-Runtime. <see cref="ContainerIsolationTests"/> in
/// den Upstream-Tests prüft, was der Aufruf mitgibt; hier läuft er wirklich — sonst wäre die
/// Isolation konstruiert und nicht ausgeführt.
/// <para>
/// Ohne Runtime werden die Tests übersprungen; <c>BIFROST_REQUIRE_CONTAINER=1</c> erzwingt sie
/// (im Docker-CI-Job gesetzt), damit der Nachweis nicht still ausfällt.
/// </para>
/// </summary>
public sealed class ContainerIsolationE2ETests
{
    private const string Image = "alpine:3.20";

    private static void RequireRuntime()
    {
        var required = Environment.GetEnvironmentVariable("BIFROST_REQUIRE_CONTAINER") is "1" or "true";
        var available = ContainerLaunchPolicy
            .ProbeAsync(new CliIsolationOptions(CliIsolationMode.Container, Image: Image), CancellationToken.None)
            .GetAwaiter().GetResult() is null;
        if (!available)
        {
            Assert.SkipUnless(required, "Keine Container-Runtime erreichbar — Docker starten oder BIFROST_REQUIRE_CONTAINER setzen.");
            Assert.Fail("BIFROST_REQUIRE_CONTAINER ist gesetzt, aber keine Container-Runtime erreichbar.");
        }
    }

    private static CliTransportOptions ContainerOptions(
        string executable, IReadOnlyList<string> fixedArguments,
        IReadOnlyDictionary<string, string>? environment = null)
        => new(
            executable,
            [new CliToolSpec("run", FixedArguments: fixedArguments)],
            EnvironmentVariables: environment,
            TimeoutSeconds: 60,
            Isolation: new CliIsolationOptions(CliIsolationMode.Container, Image: Image));

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        CliTransportOptions options)
    {
        var connector = new CliUpstreamConnector();
        await using var connection = await connector.ConnectAsync(
            new ServerId(Guid.NewGuid()),
            new UpstreamServerConfig("cli-container", "CLI im Container", UpstreamTransportKind.Cli, true, Cli: options),
            TestContext.Current.CancellationToken);

        var result = await connection.CallToolAsync(
            "run", JsonSerializer.Deserialize<JsonElement>("{}"), TestContext.Current.CancellationToken);
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        return (result.GetProperty("isError").GetBoolean() ? 1 : 0, text, text);
    }

    /// <summary>Der Aufruf läuft wirklich im Container und liefert dessen Ausgabe zurück.</summary>
    [Fact]
    public async Task A_command_runs_inside_the_container()
    {
        RequireRuntime();

        var (_, stdout, _) = await RunAsync(ContainerOptions("/bin/echo", ["hallo-aus-dem-container"]));

        stdout.Should().Contain("hallo-aus-dem-container");
    }

    /// <summary>
    /// Nicht-root im Container. Läuft das Kommando als root, ist jede weitere Härtung Makulatur.
    /// </summary>
    [Fact]
    public async Task The_command_does_not_run_as_root()
    {
        RequireRuntime();

        var (_, stdout, _) = await RunAsync(ContainerOptions("/usr/bin/id", ["-u"]));

        stdout.Trim().Should().Be("65532", "der konfigurierte Nicht-root-Benutzer");
    }

    /// <summary>
    /// Das Wurzeldateisystem ist read-only. Ein Schreibversuch ausserhalb von <c>/tmp</c> muss
    /// scheitern — sonst wäre <c>--read-only</c> nur ein Flag ohne Wirkung.
    /// </summary>
    [Fact]
    public async Task The_root_filesystem_is_read_only()
    {
        RequireRuntime();

        var (exitCode, output, _) = await RunAsync(
            ContainerOptions("/bin/sh", ["-c", "echo x > /etc/versuch"]));

        exitCode.Should().NotBe(0, "der Schreibversuch muss scheitern");
        output.Should().Contain("only", "die Runtime nennt das read-only Dateisystem");
    }

    /// <summary>Und <c>/tmp</c> ist beschreibbar — sonst wäre der Modus für viele Programme unbrauchbar.</summary>
    [Fact]
    public async Task Tmp_stays_writable()
    {
        RequireRuntime();

        var (exitCode, output, _) = await RunAsync(
            ContainerOptions("/bin/sh", ["-c", "echo geschrieben > /tmp/probe && cat /tmp/probe"]));

        exitCode.Should().Be(0);
        output.Should().Contain("geschrieben");
    }

    /// <summary>
    /// Kein Netzwerk ohne Freigabe. Der Versuch, eine Adresse aufzulösen, muss ins Leere laufen.
    /// </summary>
    [Fact]
    public async Task Without_an_allowlist_there_is_no_network()
    {
        RequireRuntime();

        var (exitCode, _, _) = await RunAsync(ContainerOptions(
            "/bin/sh", ["-c", "ping -c1 -W1 1.1.1.1"]));

        exitCode.Should().NotBe(0, "ohne Netzwerk gibt es kein Ziel");
    }

    /// <summary>
    /// Ein Secret erreicht das Programm über die Umgebung — und steht dabei <b>nicht</b> in der
    /// Kommandozeile des Container-Prozesses, wo jeder es über die Prozessliste läse.
    /// <para>
    /// Das Programm meldet nur, <em>dass</em> es den Wert hat, nicht welchen. Den Wert
    /// zurückzugeben wäre kein tauglicher Nachweis: Die Redaction des Gateways maskiert ihn auf dem
    /// Rückweg zu <c>***</c> (ADR-0011) — der Test würde dann die Maskierung prüfen statt die
    /// Zustellung.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_secret_reaches_the_program_without_touching_the_command_line()
    {
        RequireRuntime();
        const string secret = "s3hr-geheim-xyz";
        var environment = new Dictionary<string, string> { ["API_TOKEN"] = secret };

        var (_, stdout, _) = await RunAsync(ContainerOptions(
            "/bin/sh", ["-c", "printf %s \"${API_TOKEN:+empfangen}\""], environment));

        stdout.Should().Contain("empfangen", "die Variable ist im Container gesetzt und nicht leer");
        stdout.Should().NotContain(secret, "der Wert selbst hat im Ergebnis nichts verloren");

        // Und er steht auch nicht in den Argumenten, mit denen die Runtime gestartet wird.
        var arguments = ContainerLaunchPolicy.BuildRunArguments(
            ContainerOptions("/bin/sh", [], environment),
            new CliIsolationOptions(CliIsolationMode.Container, Image: Image));
        arguments.Should().ContainInOrder("--env", "API_TOKEN");
        arguments.Should().NotContain(a => a.Contains(secret, StringComparison.Ordinal));
    }

    /// <summary>
    /// Kein stiller Rückfall (ADR-0018): Mit einer Runtime, die es nicht gibt, kommt der Upstream
    /// nicht hoch. Auf den Host auszuweichen hiesse, die Isolation abzuschalten, ohne dass es
    /// jemand merkt.
    /// </summary>
    [Fact]
    public async Task A_missing_runtime_refuses_the_upstream_instead_of_falling_back()
    {
        var options = new CliTransportOptions(
            "/bin/echo",
            [new CliToolSpec("run", FixedArguments: ["egal"])],
            Isolation: new CliIsolationOptions(
                CliIsolationMode.Container, Image: Image, Runtime: "gibt-es-nicht-als-runtime"));
        var connector = new CliUpstreamConnector();

        var act = async () => await connector.ConnectAsync(
            new ServerId(Guid.NewGuid()),
            new UpstreamServerConfig("cli-norun", "ohne Runtime", UpstreamTransportKind.Cli, true, Cli: options),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Rückfall auf den Host findet nicht statt*");
    }
}
