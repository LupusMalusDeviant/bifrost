using System.Reflection;

using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Upstream.OpenApi;
using Bifrost.Upstream.OpenRpc;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>
/// Die ausgehandelte Protokollfassung auf dem Weg nach oben.
/// <para>
/// Der Befund, gegen den diese Tests gebaut sind: <c>SdkUpstreamConnection</c> kannte Fassung und
/// Fähigkeiten, aber <see cref="IUpstreamConnection"/> reichte davon nur
/// <see cref="IUpstreamConnection.PushesCatalogChanges"/> durch. Die Diagnose konnte deshalb nur
/// die Fassungs<em>familie</em> zeigen — und „2026-07-28 oder neuer" beantwortet die Frage nicht,
/// wegen der jemand die Diagnose aufruft.
/// </para>
/// </summary>
public sealed class UpstreamProtocolInfoTests
{
    // ───────────────────────── Das Modell ─────────────────────────

    /// <summary>
    /// Der Kern der Modellierung: „nicht zutreffend" und „unbekannt" sind zwei Aussagen. Wer sie
    /// zusammenwirft, schickt einen Betreiber bei einem CLI-Upstream auf die Suche nach einer
    /// Fassung, die es nie gab.
    /// </summary>
    [Fact]
    public void Not_applicable_and_unknown_are_not_the_same_answer()
    {
        var notApplicable = UpstreamProtocolInfo.NotApplicable("spricht kein MCP");
        var unknown = UpstreamProtocolInfo.Unknown("war nicht abzulesen");

        notApplicable.Availability.Should().Be(UpstreamProtocolAvailability.NotApplicable);
        unknown.Availability.Should().Be(UpstreamProtocolAvailability.Unknown);
        notApplicable.Should().NotBe(unknown,
            "beides als dieselbe Auskunft zu melden waere eine, die man nicht benutzen kann");
    }

    /// <summary>
    /// Die Bauart erzwingt, was WP4.6 als Regel gesetzt hat: kein erfundener Wert, und wo eine
    /// Angabe fehlt, steht warum. Ein Zustand ohne Begruendung laesst sich gar nicht erst bauen.
    /// </summary>
    [Fact]
    public void A_missing_version_always_carries_a_reason()
    {
        var withoutReason = () => UpstreamProtocolInfo.Unknown("   ");
        var withoutApplicabilityReason = () => UpstreamProtocolInfo.NotApplicable(string.Empty);
        var withoutVersion = () => UpstreamProtocolInfo.Negotiated(string.Empty);

        withoutReason.Should().Throw<ArgumentException>();
        withoutApplicabilityReason.Should().Throw<ArgumentException>();
        withoutVersion.Should().Throw<ArgumentException>();
    }

    /// <summary>Eine Fassung gibt es nur zusammen mit dem Zustand, der sie behauptet.</summary>
    [Fact]
    public void Only_a_negotiated_answer_carries_a_version()
    {
        UpstreamProtocolInfo.Negotiated("2026-07-28").Version.Should().Be("2026-07-28");
        UpstreamProtocolInfo.Unknown("x").Version.Should().BeNull();
        UpstreamProtocolInfo.NotApplicable("x").Version.Should().BeNull();
    }

    // ───────────────────────── Die Konnektoren ─────────────────────────

    /// <summary>
    /// <b>Jede</b> Verbindung dieses Projekts beantwortet die Frage selbst. Die Vorgabe des
    /// Vertrags ist bewusst „unbekannt" — sie behauptet nichts, taugt aber auch nichts. Ein neuer
    /// Konnektor, der sie stillschweigend erbt, meldete „nicht ermittelt" statt „spricht kein MCP",
    /// und der Unterschied ist genau der, um den es hier geht. Dieser Test ist die Stelle, an der
    /// das auffaellt.
    /// </summary>
    [Fact]
    public void Every_connection_of_this_project_answers_the_protocol_question_itself()
    {
        var connections = typeof(StdioUpstreamConnector).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => typeof(IUpstreamConnection).IsAssignableFrom(type))
            .ToList();

        connections.Should().NotBeEmpty("sonst prueft dieser Test nichts");

        var silent = connections
            .Where(type => type.GetProperty(
                nameof(IUpstreamConnection.Protocol),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is null)
            .Select(type => type.Name)
            .ToList();

        silent.Should().BeEmpty(
            "wer die Frage nicht selbst beantwortet, faellt auf 'unbekannt' zurueck — und meldet "
            + "damit eine Luecke, wo gar keine ist. Gefunden: " + string.Join(", ", silent));
    }

    /// <summary>OpenAPI: HTTP gegen eine REST-API. Es gibt nichts auszuhandeln, und das steht da.</summary>
    [Fact]
    public void An_openapi_connection_says_not_applicable()
    {
        using var http = new HttpClient();
        var connection = new OpenApiUpstreamConnection(ServerId.New(), [], http);

        connection.Protocol.Availability.Should().Be(UpstreamProtocolAvailability.NotApplicable);
        connection.Protocol.Reason.Should().Contain("kein MCP");
    }

    /// <summary>OpenRPC: nacktes JSON-RPC, kein MCP-Handshake.</summary>
    [Fact]
    public void An_openrpc_connection_says_not_applicable()
    {
        using var http = new HttpClient();
        var connection = new OpenRpcUpstreamConnection(
            ServerId.New(), [], http, new OpenRpcTransportOptions(new Uri("https://ziel.invalid/rpc")));

        connection.Protocol.Availability.Should().Be(UpstreamProtocolAvailability.NotApplicable);
        connection.Protocol.Reason.Should().Contain("kein MCP");
    }

    // ───────────────────────── Die Fähigkeiten ─────────────────────────

    /// <summary>Ohne Capability-Objekt gibt es nichts zu melden — und nichts zu erfinden.</summary>
    [Fact]
    public void Without_server_capabilities_the_list_is_empty()
        => SdkUpstreamConnection.DescribeCapabilities(null).Should().BeEmpty();

    /// <summary>
    /// Die gemeldeten Fähigkeiten werden zu <b>Namen</b>, inklusive der Unterfragen: Ob ein Server
    /// <c>resources</c> anbietet, ist eine andere Auskunft als ob er dafuer Aenderungen meldet.
    /// </summary>
    [Fact]
    public void Declared_capabilities_become_names()
    {
        var names = SdkUpstreamConnection.DescribeCapabilities(new ServerCapabilities
        {
            Tools = new ToolsCapability { ListChanged = true },
            Resources = new ResourcesCapability { Subscribe = true, ListChanged = false },
            Prompts = new PromptsCapability(),
        });

        names.Should().BeEquivalentTo(
            ["tools", "tools.listChanged", "resources", "resources.subscribe", "prompts"],
            options => options.WithStrictOrdering());
    }

    /// <summary>
    /// <c>experimental</c> und <c>extensions</c> sind offene Woerterbuecher: Die Gegenstelle darf
    /// dort alles hineinschreiben. Nach oben gehen nur die Namen — ein Wert, den niemand
    /// vorhergesehen hat, ist ein Wert, den niemand anzeigen sollte.
    /// </summary>
    [Fact]
    public void Open_dictionaries_contribute_names_only()
    {
        var names = SdkUpstreamConnection.DescribeCapabilities(new ServerCapabilities
        {
            Experimental = new Dictionary<string, object>
            {
                ["zweitens"] = new { geheim = "wert-der-nicht-nach-oben-geht" },
                ["erstens"] = new { },
            },
        });

        names.Should().BeEquivalentTo(["experimental:erstens", "experimental:zweitens"],
            options => options.WithStrictOrdering());
        names.Should().NotContain(name => name.Contains("wert-der-nicht-nach-oben-geht", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ein Upstream kann beliebig viele Namen melden. Der Bericht ist eine Auskunft fuer einen
    /// Menschen, kein Abbild der Gegenstelle — gekuerzt wird deshalb, aber <b>sichtbar</b>.
    /// </summary>
    [Fact]
    public void A_flood_of_extension_names_is_capped_and_says_so()
    {
        var flood = Enumerable.Range(0, 100)
            .ToDictionary(index => $"ext.{index:D3}", _ => (object)new { });

        var names = SdkUpstreamConnection.DescribeCapabilities(
            new ServerCapabilities { Extensions = flood });

        names.Should().HaveCount(17, "16 Namen und die Angabe, dass gekuerzt wurde");
        names[^1].Should().Contain("+84 weitere");
    }

    /// <summary>Auch ein einzelner Name kommt von aussen und darf keine Tabellenzeile sprengen.</summary>
    [Fact]
    public void A_very_long_name_is_truncated()
    {
        var names = SdkUpstreamConnection.DescribeCapabilities(new ServerCapabilities
        {
            Extensions = new Dictionary<string, object> { [new string('x', 500)] = new { } },
        });

        names.Should().ContainSingle();
        names[0].Length.Should().BeLessThan(200);
        names[0].Should().EndWith("…");
    }
}
