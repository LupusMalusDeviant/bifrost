using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Upstream.Cli;
using Xunit;

namespace McpMcp.Upstream.Tests;

/// <summary>
/// ADR-0018, Container-Modus. Die Mindestpolicy ist hier festgenagelt: Was der Container-Aufruf
/// mitgibt, ist prüfbar, ohne dass eine Runtime laufen muss — und genau deshalb fällt eine
/// weggefallene Härtung in einem Test auf und nicht erst im Betrieb.
/// </summary>
public sealed class ContainerIsolationTests
{
    private static CliTransportOptions Options(
        CliIsolationOptions? isolation = null,
        IReadOnlyList<string>? readRoots = null,
        IReadOnlyList<string>? writeRoots = null,
        IReadOnlyDictionary<string, string>? environment = null)
        => new(
            "/usr/bin/tool",
            [new CliToolSpec("run")],
            WorkingDirectory: null,
            EnvironmentVariables: environment,
            AllowedReadRoots: readRoots,
            AllowedWriteRoots: writeRoots,
            Isolation: isolation ?? new CliIsolationOptions(
                CliIsolationMode.Container, Image: "alpine:3.20"));

    private static IReadOnlyList<string> Run(CliTransportOptions options)
        => ContainerLaunchPolicy.BuildRunArguments(options, options.Isolation!);

    /// <summary>Jede Zeile der Mindestpolicy aus ADR-0018 muss im Aufruf ankommen.</summary>
    [Fact]
    public void The_minimum_policy_reaches_the_runtime()
    {
        var arguments = Run(Options());

        arguments.Should().ContainInOrder("run", "--rm");
        arguments.Should().Contain("--read-only", "das Wurzeldateisystem bleibt unbeschreibbar");
        arguments.Should().ContainInOrder("--user", "65532:65532");
        arguments.Should().ContainInOrder("--cap-drop", "ALL");
        arguments.Should().ContainInOrder("--security-opt", "no-new-privileges");
        arguments.Should().ContainInOrder("--pids-limit", "128");
        arguments.Should().ContainInOrder("--memory", "512m");
        arguments.Should().ContainInOrder("--cpus", "1");
        arguments.Should().Contain(a => a.StartsWith("/tmp:rw,noexec,nosuid", StringComparison.Ordinal));
        arguments[^1].Should().Be("alpine:3.20", "das Image steht vor dem Kommando");
    }

    /// <summary>
    /// Ohne ausdrückliche Freigabe kein Netzwerk. Die Vorgabe muss die geschlossene sein — ein
    /// vergessenes Feld darf keinen Netzzugang öffnen.
    /// </summary>
    [Fact]
    public void No_network_unless_explicitly_allowed()
        => Run(Options()).Should().ContainInOrder("--network", "none");

    /// <summary>
    /// Eine Netzwerk-Allowlist ist noch nicht gebaut — und wird deshalb abgelehnt statt als offenes
    /// Bridge-Netz durchgereicht. Ein offenes Netz mit dem Etikett „Allowlist" wäre schlimmer als
    /// eine ehrliche Absage.
    /// </summary>
    [Fact]
    public void A_network_allowlist_is_refused_rather_than_faked()
    {
        var act = () => Run(Options(new CliIsolationOptions(
            CliIsolationMode.Container, Image: "alpine:3.20", NetworkAllow: ["example.org:443"])));

        act.Should().Throw<NotSupportedException>().WithMessage("*Allowlist*");
    }

    /// <summary>
    /// Mounts kommen ausschliesslich aus den kanonischen Allowlisten, die der Host-Modus schon
    /// durchsetzt — lesend als <c>ro</c>, schreibend als <c>rw</c>.
    /// </summary>
    [Fact]
    public void Mounts_come_only_from_the_configured_roots()
    {
        var arguments = Run(Options(readRoots: ["/daten/ein"], writeRoots: ["/daten/aus"]));

        arguments.Should().ContainInOrder("--volume", "/daten/ein:/daten/ein:ro");
        arguments.Should().ContainInOrder("--volume", "/daten/aus:/daten/aus:rw");
        arguments.Count(a => a == "--volume").Should().Be(2, "nichts ausser den Allowlisten wird eingehängt");
    }

    /// <summary>
    /// Der wichtigste Test dieser Datei: Ein Secret darf <b>nie</b> in der Kommandozeile stehen.
    /// <c>--env NAME=wert</c> wäre für jeden lesbar, der die Prozessliste sieht; <c>--env NAME</c>
    /// lässt die Runtime den Wert aus ihrer eigenen Umgebung nehmen.
    /// </summary>
    [Fact]
    public void A_secret_never_appears_on_the_command_line()
    {
        var arguments = Run(Options(environment: new Dictionary<string, string>
        {
            ["API_TOKEN"] = "streng-geheim-1234",
        }));

        arguments.Should().ContainInOrder("--env", "API_TOKEN");
        arguments.Should().NotContain(a => a.Contains("streng-geheim", StringComparison.Ordinal),
            "der Wert gehört in die Umgebung des Runtime-Prozesses, nicht in seine Argumente");
    }

    [Fact]
    public void A_container_without_an_image_is_refused()
    {
        var act = () => Run(Options(new CliIsolationOptions(CliIsolationMode.Container)));

        act.Should().Throw<ArgumentException>().WithMessage("*Image*");
    }

    /// <summary>
    /// Ein Hash-Pin prüft eine Datei auf <em>diesem</em> Host. Im Container liegt das Programm im
    /// Image — der Pin liefe ins Leere und würde Sicherheit vortäuschen.
    /// </summary>
    [Fact]
    public void A_host_hash_pin_is_refused_in_container_mode()
    {
        var options = Options() with { ExecutableSha256 = new string('a', 64) };

        var act = () => CliProcessPolicy.Resolve(options);

        act.Should().Throw<ArgumentException>().WithMessage("*Image-Digest*");
    }

    /// <summary>
    /// Der Host-Modus bleibt unberührt: Eine Konfiguration ohne Isolation-Abschnitt verhält sich
    /// wie vorher. Sonst hätte das Hinzufügen der Option bestehende Upstreams verändert.
    /// </summary>
    [Fact]
    public void Without_the_option_nothing_changes()
    {
        var host = new CliTransportOptions("/usr/bin/tool", [new CliToolSpec("run")]);

        host.Isolation.Should().BeNull();
        (host.Isolation?.Mode ?? CliIsolationMode.Host).Should().Be(CliIsolationMode.Host);
    }
}
