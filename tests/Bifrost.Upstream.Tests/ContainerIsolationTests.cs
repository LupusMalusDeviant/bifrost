using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.Cli;
using Bifrost.Upstream.Isolation;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// ADR-0018, Container-Modus. Die Mindestpolicy ist hier festgenagelt: Was der Container-Aufruf
/// mitgibt, ist prüfbar, ohne dass eine Runtime laufen muss — und genau deshalb fällt eine
/// weggefallene Härtung in einem Test auf und nicht erst im Betrieb.
/// <para>
/// Seit WP3.2 baut <b>eine</b> Stelle die Argumente für stdio und CLI. Deshalb steht die
/// Mindestpolicy hier einmal und wird für beide Lebensdauern geprüft — zwei Prüflisten wären zwei
/// Wahrheiten, von denen eine veraltet.
/// </para>
/// </summary>
public sealed class ContainerIsolationTests
{
    private static ContainerIdentity Identity()
        => ContainerIdentity.ForUpstream("werkzeug", "instanz-1");

    private static IReadOnlyList<string> Run(
        IsolationOptions? isolation = null,
        ContainerLifetime lifetime = ContainerLifetime.PerInvocation,
        IReadOnlyList<string>? readRoots = null,
        IReadOnlyList<string>? writeRoots = null,
        IReadOnlyList<string>? environmentNames = null,
        string? workingDirectory = null)
        => ContainerLaunchPolicy.BuildRunArguments(new ContainerLaunchRequest(
            isolation ?? new IsolationOptions(IsolationMode.Container, Image: "alpine:3.20"),
            Identity(),
            lifetime,
            readRoots,
            writeRoots,
            workingDirectory,
            environmentNames));

    /// <summary>Jede Zeile der Mindestpolicy aus ADR-0018 muss im Aufruf ankommen.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_minimum_policy_reaches_the_runtime(bool session)
    {
        var arguments = Run(lifetime: session
            ? ContainerLifetime.Session
            : ContainerLifetime.PerInvocation);

        arguments.Should().ContainInOrder("run", "--rm");
        arguments.Should().Contain("--read-only", "das Wurzeldateisystem bleibt unbeschreibbar");
        arguments.Should().ContainInOrder("--user", "65532:65532");
        arguments.Should().ContainInOrder("--cap-drop", "ALL");
        arguments.Should().ContainInOrder("--security-opt", "no-new-privileges");
        arguments.Should().ContainInOrder("--pids-limit", "128");
        arguments.Should().ContainInOrder("--memory", "512m");
        // Ohne Swapgrenze duerfte der Container ueber Swap weiterwachsen, und die RAM-Grenze waere
        // eine Empfehlung.
        arguments.Should().ContainInOrder("--memory-swap", "512m");
        arguments.Should().ContainInOrder("--cpus", "1");
        arguments.Should().Contain(a => a.StartsWith("/tmp:rw,noexec,nosuid", StringComparison.Ordinal));
        arguments[^1].Should().Be("alpine:3.20", "das Image steht vor dem Kommando");
    }

    /// <summary>
    /// Der Unterschied zwischen den Transporten ist die Lebensdauer und sonst nichts. Ein Job je
    /// Aufruf braucht kein stdin; eine stehende Sitzung lebt davon.
    /// </summary>
    [Fact]
    public void Only_the_lifetime_differs_between_the_two_transports()
    {
        Run(lifetime: ContainerLifetime.PerInvocation).Should().Contain("--interactive=false");

        var session = Run(lifetime: ContainerLifetime.Session);
        session.Should().Contain(
            "--interactive=true", "ueber stdin laeuft das MCP-Protokoll");
        session.Should().Contain(
            "--tty=false", "ein TTY wuerde Zeilen umbrechen, und JSON-RPC vertraegt das nicht");
        session.Should().ContainInOrder("--stop-timeout", "10");
    }

    /// <summary>
    /// Ohne ausdrückliche Freigabe kein Netzwerk. Die Vorgabe muss die geschlossene sein — ein
    /// vergessenes Feld darf keinen Netzzugang öffnen.
    /// </summary>
    [Fact]
    public void No_network_unless_explicitly_allowed()
        => Run().Should().ContainInOrder("--network", "none");

    /// <summary>
    /// Eine Netzwerk-Allowlist ist noch nicht durchsetzbar — und wird deshalb abgelehnt statt als
    /// offenes Bridge-Netz durchgereicht. Ein offenes Netz mit dem Etikett „Allowlist" wäre
    /// schlimmer als eine ehrliche Absage.
    /// </summary>
    [Fact]
    public void A_network_allowlist_is_refused_rather_than_faked()
    {
        var act = () => Run(new IsolationOptions(
            IsolationMode.Container, Image: "alpine:3.20", NetworkAllow: ["example.org:443"]));

        act.Should().Throw<NotSupportedException>().WithMessage("*Allowlist*");
    }

    /// <summary>
    /// Mounts kommen ausschliesslich aus den kanonischen Allowlisten, die der Host-Modus schon
    /// durchsetzt — lesend als <c>ro</c>, schreibend als <c>rw</c>.
    /// </summary>
    [Fact]
    public void Mounts_come_only_from_the_configured_roots()
    {
        var arguments = Run(readRoots: ["/daten/ein"], writeRoots: ["/daten/aus"]);

        arguments.Should().ContainInOrder("--volume", "/daten/ein:/daten/ein:ro");
        arguments.Should().ContainInOrder("--volume", "/daten/aus:/daten/aus:rw");
        arguments.Count(a => a == "--volume").Should().Be(
            2, "nichts ausser den Allowlisten wird eingehaengt");
    }

    /// <summary>Ohne Allowlisten gibt es gar keinen Mount — der Fall eines stdio-Upstreams.</summary>
    [Fact]
    public void Without_allowlists_nothing_is_mounted()
        => Run(lifetime: ContainerLifetime.Session).Should().NotContain("--volume");

    /// <summary>
    /// Ein Pfad mit <c>..</c> hält die Allowlist ein und landet trotzdem woanders; ein relativer
    /// Pfad bezöge sich auf das Arbeitsverzeichnis der Runtime. Beides wird abgewiesen, nicht
    /// bereinigt: Wer es eingetragen hat, soll es erfahren.
    /// </summary>
    [Theory]
    [InlineData("/daten/../etc")]
    [InlineData("daten/relativ")]
    public void A_mount_that_points_out_of_itself_is_refused(string root)
    {
        var act = () => Run(readRoots: [root]);

        act.Should().Throw<UnauthorizedAccessException>();
    }

    /// <summary>
    /// Steht ein Pfad in beiden Listen, gilt die Schreibfassung — und er wird <b>einmal</b>
    /// eingehängt. Zwei Mounts auf dasselbe Ziel lehnt die Runtime ab.
    /// </summary>
    [Fact]
    public void A_path_in_both_lists_is_mounted_once_and_writable()
    {
        var arguments = Run(readRoots: ["/daten"], writeRoots: ["/daten"]);

        arguments.Count(a => a == "--volume").Should().Be(1);
        arguments.Should().Contain("/daten:/daten:rw");
    }

    /// <summary>
    /// Der wichtigste Test dieser Datei: Ein Secret darf <b>nie</b> in der Kommandozeile stehen.
    /// <c>--env NAME=wert</c> wäre für jeden lesbar, der die Prozessliste sieht; <c>--env NAME</c>
    /// lässt die Runtime den Wert aus ihrer eigenen Umgebung nehmen.
    /// </summary>
    [Fact]
    public void A_secret_never_appears_on_the_command_line()
    {
        var arguments = Run(environmentNames: ["API_TOKEN"]);

        arguments.Should().ContainInOrder("--env", "API_TOKEN");
        arguments.Should().NotContain(
            a => a.StartsWith("API_TOKEN=", StringComparison.Ordinal),
            "der Wert gehoert in die Umgebung des Runtime-Prozesses, nicht in seine Argumente");
    }

    [Fact]
    public void A_container_without_an_image_is_refused()
    {
        var act = () => Run(new IsolationOptions(IsolationMode.Container));

        act.Should().Throw<ArgumentException>().WithMessage("*Image*");
    }

    /// <summary>
    /// Ein Container ohne Namen liesse sich später nicht abräumen. Name und Etiketten sind deshalb
    /// Teil der Mindestpolicy und nicht Beiwerk.
    /// </summary>
    [Fact]
    public void Every_container_carries_a_name_and_the_owning_instance()
    {
        var arguments = Run();

        arguments.Should().Contain("--name");
        arguments.Should().Contain(
            a => a.StartsWith(ContainerIdentity.NamePrefix, StringComparison.Ordinal));
        arguments.Should().Contain($"{ContainerIdentity.OwnerLabel}={ContainerIdentity.OwnerValue}");
        arguments.Should().Contain($"{ContainerIdentity.InstanceLabel}=instanz-1");
        arguments.Should().Contain($"{ContainerIdentity.SlugLabel}=werkzeug");
    }

    /// <summary>
    /// Zwei Starts desselben Upstreams tragen verschiedene Namen. Sonst scheiterte ein Neustart
    /// daran, dass eine noch nicht abgeräumte Leiche den Namen hält — mit „name already in use".
    /// </summary>
    [Fact]
    public void Two_launches_of_the_same_upstream_do_not_collide()
        => ContainerIdentity.ForUpstream("werkzeug", "instanz-1").Name
            .Should().NotBe(ContainerIdentity.ForUpstream("werkzeug", "instanz-1").Name);

    /// <summary>
    /// Ein Hash-Pin prüft eine Datei auf <em>diesem</em> Host. Im Container liegt das Programm im
    /// Image — der Pin liefe ins Leere und würde Sicherheit vortäuschen.
    /// </summary>
    [Fact]
    public void A_host_hash_pin_is_refused_in_container_mode()
    {
        var options = new CliTransportOptions(
            "/usr/bin/tool",
            [new CliToolSpec("run")],
            ExecutableSha256: new string('a', 64),
            Isolation: new IsolationOptions(IsolationMode.Container, Image: "alpine:3.20"));

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
        (host.Isolation?.Mode ?? IsolationMode.Host).Should().Be(IsolationMode.Host);
    }

    /// <summary>
    /// Ein Container ohne Namen lässt sich nicht abräumen — deshalb ist der Name im
    /// Container-Modus keine Option, sondern Voraussetzung.
    /// </summary>
    [Fact]
    public void Container_mode_without_an_identity_is_a_programming_error()
    {
        var options = new CliTransportOptions(
            "/usr/bin/tool",
            [new CliToolSpec("run")],
            Isolation: new IsolationOptions(IsolationMode.Container, Image: "alpine:3.20"));

        var act = () => CliProcessPolicy.CreateStartInfo(
            options, CliProcessPolicy.Resolve(options), identity: null);

        act.Should().Throw<ArgumentNullException>();
    }
}
