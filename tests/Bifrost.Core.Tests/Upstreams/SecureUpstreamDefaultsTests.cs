using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Core.Upstreams;

using Xunit;

namespace Bifrost.Core.Tests.Upstreams;

/// <summary>
/// Die Vorgaben für <b>neu angelegte</b> Upstreams (ADR-0025 E2/E5, WP3.2).
/// <para>
/// Zwei Sätze, die zusammen den ganzen Sinn dieser Klasse ergeben: Was ab jetzt entsteht, ist
/// isoliert und hat die SSRF-Frage beantwortet. Was schon da ist, ändert sich dadurch <b>nicht</b> —
/// das regelt die Bestandsübernahme aus WP3.1, und eine zweite Umstellungslogik wäre schlimmer als
/// keine.
/// </para>
/// </summary>
public class SecureUpstreamDefaultsTests
{
    private static UpstreamServerConfig Stdio(IsolationOptions? isolation)
        => new("neu", "Neu", UpstreamTransportKind.Stdio, true,
            Stdio: new StdioTransportOptions("/usr/bin/server", ["--stdio"], Isolation: isolation));

    private static UpstreamServerConfig Http(bool? allowPrivate)
        => new("neu-http", "Neu", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                new Uri("https://beispiel.test/mcp"), AllowPrivateTargets: allowPrivate));

    /// <summary>
    /// Ein neu angelegter nativer Upstream ohne Isolationsangabe wird abgewiesen — mit einer
    /// Meldung, die das fehlende Feld und beide Auswege nennt. Ihn stillschweigend im Host-Modus
    /// anzulegen wäre genau die Vorgabe, die dieses Paket abschafft.
    /// </summary>
    [Fact]
    public void A_new_native_upstream_without_an_isolation_section_is_refused()
    {
        var act = () => SecureUpstreamDefaults.ForNewUpstream(Stdio(null));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Stdio.Isolation*")
            .And.Message.Should().Contain("Container");
    }

    /// <summary>Dasselbe für CLI: derselbe Satz, weil es dasselbe Modell ist.</summary>
    [Fact]
    public void The_same_holds_for_a_new_cli_upstream()
    {
        var config = new UpstreamServerConfig(
            "neu-cli", "Neu", UpstreamTransportKind.Cli, true,
            Cli: new CliTransportOptions("/usr/bin/werkzeug", [new CliToolSpec("lauf")]));

        var act = () => SecureUpstreamDefaults.ForNewUpstream(config);

        act.Should().Throw<ArgumentException>().WithMessage("*Cli.Isolation*");
    }

    /// <summary>
    /// Container mit Image geht durch und bleibt, wie er dasteht — die Vorgabe ergänzt, sie
    /// überschreibt nicht.
    /// </summary>
    [Fact]
    public void A_container_configuration_passes_unchanged()
    {
        var isolation = new IsolationOptions(IsolationMode.Container, Image: "alpine:3.20");

        SecureUpstreamDefaults.ForNewUpstream(Stdio(isolation)).Stdio!.Isolation
            .Should().Be(isolation);
    }

    /// <summary>
    /// <b>Ausdrücklich gesetzt schlägt Vorgabe</b> — auch beim Host-Modus. Ob er erlaubt ist,
    /// entscheidet die Ausführungs-Policy aus WP3.1 und nicht diese Klasse; zwei Stellen, die
    /// dieselbe Frage beantworten, beantworten sie irgendwann verschieden.
    /// </summary>
    [Fact]
    public void An_explicit_host_choice_is_left_alone_and_judged_elsewhere()
        => SecureUpstreamDefaults.ForNewUpstream(Stdio(new IsolationOptions(IsolationMode.Host)))
            .Stdio!.Isolation!.Mode.Should().Be(IsolationMode.Host);

    /// <summary>
    /// Die verschobene SSRF-Lücke: <c>null</c> heisst „nicht entschieden" und damit heute
    /// „erlaubt". Für eine Neuanlage wird der Wert deshalb <b>gesetzt</b>, nicht offengelassen.
    /// </summary>
    [Fact]
    public void A_new_http_upstream_carries_the_private_target_decision()
    {
        var secured = SecureUpstreamDefaults.ForNewUpstream(Http(null));

        secured.Http!.AllowPrivateTargets.Should().BeFalse(
            "die Vorgabe fuer Neuanlagen ist geschlossen");
        SecureUpstreamDefaults.DecidesPrivateTargets(secured).Should().BeTrue();
        SecureUpstreamDefaults.DecidesPrivateTargets(Http(null)).Should().BeFalse(
            "die Gegenprobe: unentschieden ist ein anderer Zustand als 'verboten'");
    }

    /// <summary>
    /// Wer ein internes Ziel wirklich braucht, hakt es an — und behält es. Eine Vorgabe, die eine
    /// ausdrückliche Angabe überschreibt, ist keine Vorgabe, sondern eine Bevormundung.
    /// </summary>
    [Fact]
    public void An_explicit_yes_to_private_targets_survives()
        => SecureUpstreamDefaults.ForNewUpstream(Http(true)).Http!.AllowPrivateTargets
            .Should().BeTrue();

    /// <summary>
    /// Der wichtigste Test dieser Datei: Ein <b>bestehender</b> Upstream läuft nicht durch diese
    /// Klasse. Die Vorgabe wirkt beim Anlegen, nicht beim Lesen — sonst änderte ein Upgrade das
    /// Verhalten laufender Installationen, und genau das lehnt ADR-0025 E3 ab.
    /// <para>
    /// Belegt wird das hier an der Datenklasse selbst: Ein aus dem Speicher gelesener Upstream ist
    /// unverändert, solange ihn niemand durch <see cref="SecureUpstreamDefaults.ForNewUpstream"/>
    /// schickt — und das tun nur die beiden Anlege-Wege (API-POST und Formular), nicht der
    /// Wiederherstellungsweg des Supervisors.
    /// </para>
    /// </summary>
    [Fact]
    public void An_existing_configuration_is_not_touched_by_reading_it()
    {
        var bestand = Stdio(null) with
        {
            Http = new HttpTransportOptions(new Uri("http://192.168.178.61/mcp")),
        };

        bestand.Stdio!.Isolation.Should().BeNull("Host-Modus, wie er seit jeher lief");
        bestand.Http!.AllowPrivateTargets.Should().BeNull("nicht entschieden, also erlaubt");
    }
}
