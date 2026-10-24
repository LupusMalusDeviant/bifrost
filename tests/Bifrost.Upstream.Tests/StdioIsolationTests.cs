using System.Diagnostics;

using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// stdio im Container (ADR-0025 E5) — und vor allem: <b>kein stiller Rückfall</b> (ADR-0018,
/// ADR-0025 E6).
/// <para>
/// Diese Tests brauchen keine Runtime. Der wichtigste von ihnen braucht ausdrücklich <em>keine</em>:
/// Er prüft, was geschieht, wenn keine da ist.
/// </para>
/// </summary>
public sealed class StdioIsolationTests
{
    private static UpstreamServerConfig Config(IsolationOptions? isolation)
        => new("stdio-probe", "stdio-Probe", UpstreamTransportKind.Stdio, true,
            Stdio: new StdioTransportOptions(
                "/bin/mcp-server", ["--stdio"], Isolation: isolation));

    /// <summary>
    /// Ohne erreichbare Runtime kommt der Upstream <b>nicht</b> hoch. Auf den Host auszuweichen
    /// hiesse, die Isolation abzuschalten, ohne dass es jemand merkt — der Upstream liefe weiter,
    /// nur ungeschützt.
    /// </summary>
    [Fact]
    public async Task A_missing_runtime_refuses_the_upstream_instead_of_falling_back()
    {
        var connector = new StdioUpstreamConnector();
        // Eine zulässige Runtime, die auf den Testrechnern nicht installiert ist — geprüft wird die
        // fehlende Runtime, nicht die Liste.
        var config = Config(new IsolationOptions(
            IsolationMode.Container, Image: "alpine:3.20", Runtime: "finch"));

        var act = async () => await connector.ConnectAsync(
            new ServerId(Guid.NewGuid()), config, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Rückfall auf den Host findet nicht statt*");
    }

    /// <summary>
    /// Die Gegenprobe zur Absage: Sie darf keinen Hostprozess hinterlassen haben. Ein Test, der nur
    /// die Ausnahme prüft, wäre auch dann grün, wenn nebenher das Programm gestartet worden wäre.
    /// </summary>
    [Fact]
    public async Task The_refusal_starts_no_process_at_all()
    {
        var before = Process.GetProcesses().Length;
        var connector = new StdioUpstreamConnector();

        // Ein Kommando, das es auf diesem Rechner mit Sicherheit nicht gibt: Wäre der Rückfall
        // gebaut, käme hier eine Meldung über das fehlende PROGRAMM statt über die fehlende
        // RUNTIME — und genau daran erkennt man den Unterschied.
        var act = async () => await connector.ConnectAsync(
            new ServerId(Guid.NewGuid()),
            Config(new IsolationOptions(
                IsolationMode.Container, Image: "alpine:3.20", Runtime: "gibt-es-nicht-als-runtime")),
            TestContext.Current.CancellationToken);

        var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        exception.Message.Should().Contain(
            "gibt-es-nicht-als-runtime",
            "die Absage nennt die Runtime — nicht das Programm, denn das wurde nie versucht");
        exception.Message.Should().NotContain(
            "/bin/mcp-server", "das Programm ist nie angefasst worden");

        Process.GetProcesses().Length.Should().BeCloseTo(before, 20,
            "die Absage hinterlaesst keinen laufenden Prozess");
    }

    /// <summary>
    /// Der Weg am Verbot nativer Ausführung vorbei (ADR-0025): Ein Container-Upstream gilt als
    /// isoliert, die Runtime aber startet nativ. Ein Bestand, der vor der Liste mit einem beliebigen
    /// Programm als „Runtime" angelegt wurde, darf dieses Programm auch beim Start nicht ausführen —
    /// der Connector prüft selbst und verlässt sich nicht auf den Validator.
    /// </summary>
    [Fact]
    public async Task A_runtime_outside_the_list_is_refused_without_being_started()
    {
        var connector = new StdioUpstreamConnector();

        var act = async () => await connector.ConnectAsync(
            new ServerId(Guid.NewGuid()),
            Config(new IsolationOptions(IsolationMode.Container, Image: "alpine:3.20", Runtime: "/bin/sh")),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*'/bin/sh' ist nicht zulässig*Rückfall auf den Host findet nicht statt*");
    }

    /// <summary>
    /// Der Host-Modus bleibt unberührt: Eine bestehende stdio-Konfiguration ohne
    /// Isolation-Abschnitt verhält sich wie vorher. Das Feld allein ändert nichts.
    /// </summary>
    [Fact]
    public void Without_the_option_a_stdio_upstream_stays_on_the_host()
    {
        var bestand = new StdioTransportOptions("/bin/mcp-server", []);

        bestand.Isolation.Should().BeNull();
        (bestand.Isolation?.Mode ?? IsolationMode.Host).Should().Be(IsolationMode.Host);
    }
}
