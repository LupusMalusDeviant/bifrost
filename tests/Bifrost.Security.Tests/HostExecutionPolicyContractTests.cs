using System.Text.RegularExpressions;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Security.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bifrost.Security.Tests;

/// <summary>
/// <b>Invariante 7:</b> Paket- und Importpfade duerfen die Host-Policy nicht umgehen.
/// <para>
/// <b>Stand dieser Datei:</b> Der Vertrag (<c>src/Bifrost.Abstractions/Execution.cs</c>) ist vom
/// Lead gelegt und eingefroren; die Umsetzung gehoert WP3.1. Die Tests hier sind deshalb gegen den
/// <b>Vertrag</b> geschrieben und nicht gegen eine Klasse: Sie holen die Policy so, wie der Dienst
/// sie zusammensetzt, und pruefen die Zusicherung aus ADR-0025 E1. Fehlt die Umsetzung, melden sie
/// das als uebersprungen — ein gruener Test ohne Pruefling waere eine Luege (M3-Vertrag §7).
/// </para>
/// <para>
/// <b>Abgrenzung zu WP3.1.</b> Die Umsetzung bringt eine eigene Architekturpruefung mit
/// (<c>HostExecutionCheckedAttribute</c> plus IL-Lesung). Dieses Paket dupliziert sie nicht,
/// sondern prueft die Aussagen, die der <em>Vertrag</em> macht: eindeutige Codes, „unbekannt
/// heisst nein", und dass der Importpfad ueberhaupt ein Urteil traegt.
/// </para>
/// </summary>
public class HostExecutionPolicyContractTests : IClassFixture<SecurityGatewayFixture>
{
    private readonly SecurityGatewayFixture _gateway;

    public HostExecutionPolicyContractTests(SecurityGatewayFixture gateway) => _gateway = gateway;

    /// <summary>
    /// Die Reason-Codes sind das, worauf ein Betreiber ein Runbook oder eine Suche stuetzt. Ein
    /// doppelt vergebener Code macht zwei verschiedene Lagen ununterscheidbar — dieselbe Regel,
    /// die bei den Diagnosecodes aus M2 schon einmal eine Kollision verhindert hat.
    /// </summary>
    [Fact]
    public void The_reason_codes_are_unique_and_inside_the_reserved_range()
    {
        var codes = typeof(HostExecutionReason)
            .GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()!))
            .ToArray();

        codes.Should().NotBeEmpty();
        codes.Select(code => code.Value).Should().OnlyHaveUniqueItems(
            "zwei Lagen mit demselben Code sind fuer eine Suche dieselbe Lage");

        foreach (var (name, value) in codes)
        {
            var match = Regex.Match(value, @"^BFR-POL-(\d{4})$");
            match.Success.Should().BeTrue($"'{name}' = '{value}' folgt nicht dem Vertragsformat BFR-POL-NNNN");
            int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeInRange(1, 99, "reserviert ist BFR-POL-0001…0099");
        }
    }

    /// <summary>
    /// <b>Unbekannt heisst nein</b> (ADR-0025 E1). Eine Policy, die im Zweifel erlaubt, ist eine
    /// Dokumentation.
    /// <para>
    /// Die Policy wird aus der Zusammensetzung des laufenden Dienstes geholt — nicht per
    /// <c>Activator</c> erzeugt. Der Unterschied ist der Punkt: Geprueft wird die Policy, die im
    /// Betrieb tatsaechlich entscheidet, samt ihrer Einstellungen; eine frisch gebaute Instanz
    /// koennte eine andere Antwort geben als die registrierte.
    /// </para>
    /// </summary>
    [Fact]
    public void An_undetermined_configuration_is_refused_by_the_registered_policy()
    {
        var policy = _gateway.Services.GetService<IHostExecutionPolicy>();
        Assert.SkipWhen(
            policy is null,
            "Keine IHostExecutionPolicy registriert — geschrieben, laeuft nach WP3.1.");

        // Eine Konfiguration ohne jede Angabe zur Ausfuehrungsart: kein Transport befuellt. Das
        // ist der Fall, in dem eine nachlaessige Policy „ja" sagt.
        var undetermined = new UpstreamServerConfig(
            "unklar", "Ohne Angabe", UpstreamTransportKind.Stdio, Enabled: true);

        var decision = policy!.Evaluate(undetermined);

        decision.Allowed.Should().BeFalse(
            "eine Konfiguration, ueber die die Policy nichts weiss, darf nicht nativ starten");
        decision.ReasonCode.Should().MatchRegex(@"^BFR-POL-\d{4}$");
        decision.Summary.Should().NotBeNullOrWhiteSpace(
            "ein Verbot ohne Satz fuer Menschen ist im Betrieb ein Raetsel");
    }

    /// <summary>
    /// Die Gegenprobe zur Policy: Ein Upstream, der gar nicht nativ startet, darf nicht am selben
    /// Verbot haengen bleiben. Ohne diesen Test waere eine Policy, die <em>alles</em> verbietet,
    /// gruen und trotzdem falsch — sie legte jeden isolierten Upstream still.
    /// </summary>
    [Fact]
    public void An_isolated_upstream_is_not_caught_by_the_host_ban()
    {
        var policy = _gateway.Services.GetService<IHostExecutionPolicy>();
        Assert.SkipWhen(policy is null, "Keine IHostExecutionPolicy registriert — laeuft nach WP3.1.");

        var isolated = new UpstreamServerConfig(
            "wasi", "Isoliert", UpstreamTransportKind.Wasi, Enabled: true,
            Wasi: new WasiTransportOptions(
                "bifrost-wasi-host", "component.wasm", "component.sig", ["cHVibGlzaGVy"]));

        var decision = policy!.Evaluate(isolated);

        decision.Allowed.Should().BeTrue(
            "ein isolierter Upstream ist von der Host-Policy nicht betroffen (BFR-POL-0001)");
    }

    /// <summary>
    /// Der Importpfad. Ein Connector-Paket bringt seine Upstream-Definition mit; wird sie beim
    /// Import nicht gegen die Policy gehalten, ist der Paketimport der Weg, auf dem eine verbotene
    /// Host-Ausfuehrung ins System kommt — an der Formularvalidierung vorbei, die nur die
    /// Oberflaeche kennt.
    /// <para>
    /// <b>Wie er bei einer neuen Stelle rot wird:</b> Geprueft wird jede Datei, die sich eine
    /// <see cref="UpstreamServerConfig"/> selbst <em>baut</em> — also aus einem Manifest, einem
    /// Archiv oder einer Vorlage eine Konfiguration erzeugt, die kein Mensch im Formular gesehen
    /// hat. Genau dort entsteht ein Startweg, an dem keine Validierung haengt. Eine neue solche
    /// Datei traegt keine der beiden Kennzeichnungen aus <c>Bifrost.Core.Execution</c> und faellt
    /// hier auf. Der Test kennt die Fehlerklasse, nicht die Faelle.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_path_that_builds_a_config_itself_carries_a_verdict()
    {
        var builders = ConfigBuilders();

        var withoutVerdict = builders
            .Where(file => !Text(file).Contains("HostExecutionChecked", StringComparison.Ordinal)
                && !Text(file).Contains("NoHostExecution", StringComparison.Ordinal))
            .ToArray();

        withoutVerdict.Should().BeEmpty(
            "hier entsteht eine Upstream-Konfiguration, ohne dass jemand gesagt hat, ob sie etwas "
            + "starten kann (ADR-0025). Entweder [HostExecutionChecked] oder [NoHostExecution] "
            + "mit Begruendung. Offen: " + string.Join(", ", withoutVerdict));
    }

    /// <summary>
    /// Der Beleg, dass der vorige Test ueberhaupt etwas anfassen kann. Faende er keine Stelle,
    /// waere er gruen, ohne je hingesehen zu haben — genau die Bauart, die dieses Paket
    /// verhindern soll.
    /// </summary>
    [Fact]
    public void Some_path_really_builds_a_config_itself()
        => ConfigBuilders().Should().NotBeEmpty(
            "ohne eine einzige Bau-Stelle prueft der Waechter darueber nichts");

    private static string[] ConfigBuilders()
        => [.. RepositorySources
            .Find(new Regex(@"new\s+UpstreamServerConfig\s*\(", RegexOptions.CultureInvariant))
            .Select(hit => hit.File)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static string Text(string relativePath)
        => string.Join('\n', RepositorySources.Production
            .First(file => file.RelativePath == relativePath).Lines);
}
