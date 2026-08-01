using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Security.Tests.Infrastructure;
using Xunit;

namespace Bifrost.Security.Tests;

/// <summary>
/// <b>Invariante 1 (M3-Vertrag §6.1):</b> Keine Toolausfuehrung ausserhalb
/// <see cref="IToolInvoker"/>.
/// <para>
/// Der Invoker ist die Stelle, an der RBAC, Argumentpruefung, Freigabe, Guardrail, Zeitlimit,
/// Kuerzung und Audit zusammenkommen. Wer an ihm vorbei einen Toolaufruf absetzt, umgeht nicht
/// eine dieser Pruefungen, sondern alle — und zwar geraeuschlos, weil ein direkter Aufruf genauso
/// aussieht wie ein richtiger.
/// </para>
/// </summary>
public class ToolExecutionOnlyViaInvokerTests
{
    /// <summary>
    /// Wer einen Toolaufruf absetzen darf. Die Liste enthaelt <b>keine</b> Fassade: Sie ist der
    /// Bauplan der Verbindung selbst (Connector, Aufsicht, Huelle) plus die eine Stelle, die daraus
    /// einen Toolaufruf macht.
    /// </summary>
    private static readonly string[] MayCallATool =
    [
        "src/Bifrost.Abstractions/Upstream.cs",
        "src/Bifrost.Abstractions/PublisherTrust.cs",
        "src/Bifrost.Upstream/",
        "src/Bifrost.Core/Upstreams/",
        "src/Bifrost.Core/Invocation/ToolInvoker.cs",
    ];

    /// <summary>
    /// Die harte Regel. Ein neuer Adapter — eine zweite REST-Fassade, ein Cron-Ausloeser, ein
    /// Batchlauf — der sich die Verbindung vom Supervisor holt und selbst <c>CallToolAsync</c>
    /// ruft, taucht hier als neue Fundstelle auf und macht den Test rot.
    /// <para>
    /// <b>Warum ueber die Fundstelle und nicht ueber die Typen:</b> Ein Aufruf an
    /// <c>supervisor.GetConnection(id).CallToolAsync(...)</c> hinterlaesst in der Typsignatur des
    /// Aufrufers nichts. Er waere per Reflexion unsichtbar. Sichtbar ist er nur dort, wo er steht.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_the_invoker_calls_a_tool()
    {
        var callers = RepositorySources
            .Find(new Regex(@"\.CallToolAsync\s*\(", RegexOptions.CultureInvariant))
            .Where(hit => !MayCallATool.Any(allowed =>
                hit.File.StartsWith(allowed, StringComparison.Ordinal)))
            .ToArray();

        callers.Should().BeEmpty(
            "ein Toolaufruf an IToolInvoker vorbei umgeht RBAC, Argumentpruefung, Freigabe, "
            + "Guardrail, Zeitlimit und Audit auf einmal. Gefunden:\n"
            + string.Join('\n', callers.Select(hit => hit.ToString())));
    }

    /// <summary>
    /// Wer sich eine aktive Verbindung geben laesst, ohne ein Tool aufzurufen. Jeder Eintrag ist
    /// eine Entscheidung mit Begruendung — und mit dem, was an dieser Stelle <em>fehlt</em>.
    /// </summary>
    private static readonly Dictionary<string, string> MayHoldAConnection = new(StringComparer.Ordinal)
    {
        ["src/Bifrost.Server/GatewayMcpHandlers.cs"] =
            "resources/read und prompts/get sind keine Toolaufrufe; RBAC und Audit laufen dort "
            + "eigenstaendig. ANMERKUNG: die Inhaltspruefung (IContentGuard) laeuft dort NICHT — "
            + "gemeldet, nicht hier behoben.",
        ["src/Bifrost.Server/HostedServices.cs"] =
            "prueft beim Widerruf eines Herausgeberschluessels die Signaturkette einer laufenden "
            + "Verbindung; liest nur",
        ["src/Bifrost.Server/Diagnostics/UpstreamNegotiationProbe.cs"] =
            "Diagnosesonde der Upstream-Zeitlinie (WP4.6): liest zwei Eigenschaften der bestehenden "
            + "Verbindung — PushesCatalogChanges und Protocol (die ausgehandelte Fassung samt "
            + "Faehigkeitsnamen). Beides sind gepufferte Angaben aus dem Handshake; es geht KEINE "
            + "Anfrage an die Gegenstelle. Sie ruft weder ein Tool noch eine Resource ab und haelt "
            + "die Verbindung nicht — die lokale Variable endet mit der Methode, und die Methode "
            + "bleibt bewusst synchron, damit sie nicht in einer Zustandsmaschine landet. Sie "
            + "oeffnet auch keine eigene: Angezeigt wird nur, was der Supervisor ohnehin fuehrt",
        ["src/Bifrost.Server/WasiPackageProbe.cs"] =
            "Quarantaeneprobe eines Connector-Pakets: verbindet und ruft DiscoverAsync, nie ein "
            + "Tool. Traegt bereits [HostExecutionChecked]",
    };

    /// <summary>
    /// Der zweite Waechter: Wer eine Verbindung ueberhaupt in die Hand bekommt, kann sie
    /// aufrufen. Eine neue solche Stelle muss hier mit Begruendung eintragen werden — das ist die
    /// Gelegenheit, an der jemand fragt, warum sie nicht durch den Invoker geht.
    /// </summary>
    [Fact]
    public void Holding_a_connection_outside_the_invoker_is_an_explicit_decision()
    {
        var holders = RepositorySources
            .Find(new Regex(@"\bGetConnection\s*\(", RegexOptions.CultureInvariant))
            .Where(hit => !MayCallATool.Any(allowed =>
                hit.File.StartsWith(allowed, StringComparison.Ordinal)))
            .Where(hit => !MayHoldAConnection.ContainsKey(hit.File))
            .ToArray();

        holders.Should().BeEmpty(
            "wer eine aktive Upstream-Verbindung haelt, umgeht die Invoker-Pipeline — das ist "
            + "zulaessig, aber nicht nebenbei. Gefunden:\n"
            + string.Join('\n', holders.Select(hit => hit.ToString())));
    }

    /// <summary>
    /// Dieselbe Regel ueber die Typen: Kein Feld und kein Konstruktorparameter ausserhalb der
    /// erlaubten Zone traegt eine Upstream-Verbindung. Faengt den Fall, den die Fundstellensuche
    /// nicht faengt — eine Klasse, die sich die Verbindung injizieren laesst und erst spaeter
    /// benutzt.
    /// </summary>
    [Fact]
    public void No_facade_type_declares_an_upstream_connection()
    {
        var connection = typeof(IUpstreamConnection);
        var offenders = new List<string>();

        foreach (var type in BifrostAssemblies.AllTypes())
        {
            if (IsInsideTheConnectionZone(type))
            {
                continue;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var field in type.GetFields(flags))
            {
                if (connection.IsAssignableFrom(field.FieldType))
                {
                    offenders.Add($"{type.FullName}.{field.Name} : {field.FieldType.Name}");
                }
            }

            foreach (var constructor in type.GetConstructors(flags))
            {
                foreach (var parameter in constructor.GetParameters()
                    .Where(p => connection.IsAssignableFrom(p.ParameterType)))
                {
                    offenders.Add($"{type.FullName}..ctor({parameter.Name} : {parameter.ParameterType.Name})");
                }
            }
        }

        offenders.Should().BeEmpty(
            "wer eine IUpstreamConnection haelt, kann sie aufrufen — und tut es irgendwann. "
            + "Gefunden:\n" + string.Join('\n', offenders));
    }

    /// <summary>
    /// Ein Typ gehoert zur Verbindungszone, wenn er im Upstream-Projekt liegt oder im
    /// Upstreams-/Invocation-Namensraum des Kerns. Die drei begruendeten Halter aus
    /// <see cref="MayHoldAConnection"/> kommen dazu — bei ihnen steckt die Verbindung in einer
    /// compilergenerierten Zustandsmaschine, deren Name den Wirt traegt.
    /// </summary>
    private static bool IsInsideTheConnectionZone(Type type)
    {
        var name = type.FullName ?? string.Empty;
        return type.Assembly.GetName().Name is "Bifrost.Upstream" or "Bifrost.Abstractions"
            || name.StartsWith("Bifrost.Core.Upstreams.", StringComparison.Ordinal)
            || name.StartsWith("Bifrost.Core.Invocation.ToolInvoker", StringComparison.Ordinal)
            || name.StartsWith("Bifrost.Server.GatewayMcpHandlers", StringComparison.Ordinal)
            || name.StartsWith("Bifrost.Server.WasiPackageProbe", StringComparison.Ordinal)
            || name.StartsWith("Bifrost.Server.GatewayStartupService", StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Vertrag selbst: Es gibt genau eine Umsetzung von <see cref="IToolInvoker"/>. Zwei
    /// Umsetzungen waeren zwei Orte, an denen dieselbe Governance getroffen wird — und sie werden
    /// irgendwann verschieden getroffen (dieselbe Begruendung wie im M3-Vertrag §3).
    /// </summary>
    [Fact]
    public void There_is_exactly_one_implementation_of_the_invoker()
    {
        var implementations = BifrostAssemblies.AllTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(typeof(IToolInvoker).IsAssignableFrom)
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        implementations.Should().BeEquivalentTo(
            ["Bifrost.Core.Invocation.ToolInvoker"],
            "eine zweite Umsetzung ist eine zweite Governance-Entscheidung");
    }

    /// <summary>
    /// Die Umkehrung der Ausnahmeliste: Jede begruendete Ausnahme muss es noch geben. Sonst
    /// verrottet sie zu einem Namen, den niemand mehr nachprueft.
    /// </summary>
    [Fact]
    public void The_exception_list_has_no_dead_entries()
    {
        var existing = RepositorySources.Production
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        MayHoldAConnection.Keys.Where(path => !existing.Contains(path)).Should().BeEmpty();
    }
}
