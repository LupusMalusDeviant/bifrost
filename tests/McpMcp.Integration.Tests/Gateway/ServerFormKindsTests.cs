using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Web;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Die Auswahlliste auf <c>/servers</c> bot vier Upstream-Arten an, der „Bearbeiten"-Knopf stand
/// aber an <b>jeder</b> Zeile — auch an einem WASI-Upstream, den das Formular nicht abbilden kann.
/// Beim Speichern hätte der Auffangzweig daraus eine OpenAPI-Konfiguration gebaut.
/// <para>
/// Diese Tests halten fest, was das Formular kann und was nicht. Sie sind der Grund, warum die
/// Liste nicht in der Seite steht: Kommt eine Art dazu, fällt hier ein Test, statt dass sie
/// stillschweigend im Auffangzweig landet.
/// </para>
/// </summary>
public class ServerFormKindsTests
{
    [Fact]
    public void Every_transport_kind_is_either_supported_or_has_a_reason()
    {
        foreach (var kind in Enum.GetValues<UpstreamTransportKind>())
        {
            if (ServerFormKinds.CanEdit(kind))
            {
                continue;
            }

            ServerFormKinds.ReasonNotEditable(kind).Should().NotBe(
                "Diese Art kennt das Formular nicht.",
                $"'{kind}' steht nicht im Formular — dann muss dort stehen, warum und was "
                + "stattdessen gilt. Sonst sucht jemand einen Knopf, den es absichtlich nicht gibt");
        }
    }

    [Fact]
    public void Package_based_and_openrpc_upstreams_are_not_editable_in_the_form()
    {
        ServerFormKinds.CanEdit(UpstreamTransportKind.Wasi).Should().BeFalse(
            "ein WASI-Upstream wird über sein Connector-Paket konfiguriert, nicht über das Formular");
        ServerFormKinds.CanEdit(UpstreamTransportKind.OpenRpc).Should().BeFalse();
    }

    [Fact]
    public void The_four_kinds_with_form_fields_are_editable()
        => ServerFormKinds.Supported.Should().Equal(
        [
            UpstreamTransportKind.Stdio,
            UpstreamTransportKind.StreamableHttp,
            UpstreamTransportKind.OpenApi,
            UpstreamTransportKind.Cli,
        ]);
}
