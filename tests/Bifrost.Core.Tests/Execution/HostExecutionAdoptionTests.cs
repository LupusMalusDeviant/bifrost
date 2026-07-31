using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Core.Execution;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace Bifrost.Core.Tests.Execution;

/// <summary>
/// Die Bestandsübernahme aus ADR-0025 E3 — der Kern von WP3.1.
/// <para>
/// Geprüft wird nicht „läuft weiter", sondern <b>läuft weiter, und alle wissen warum</b>: Der Wert
/// steht danach geschrieben da, er trägt einen eigenen Reason-Code, und die betroffenen Upstreams
/// sind namentlich genannt. Ohne diese drei ist die Übernahme genau das stillschweigende
/// Weiterlaufen, das das ADR ablehnt.
/// </para>
/// </summary>
public sealed class HostExecutionAdoptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bestandsinstanz_uebernimmt_ihren_zustand_und_laeuft_weiter()
    {
        var store = new MemorySettingStore();
        var coordinator = new HostExecutionCoordinator(store, environmentValue: null, Time());

        var state = coordinator.Resolve([Stdio("alt"), Http("web")]);

        state.Allowed.Should().BeTrue("eine bestehende Instanz wird nicht stillgelegt");
        state.Origin.Should().Be(HostExecutionOrigin.AdoptedFromExistingInstance);
        state.ReasonCode.Should().Be(HostExecutionReason.AdoptedFromExistingInstance);
        coordinator.Evaluate(Stdio("alt")).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Die_uebernahme_wird_geschrieben_und_nicht_angenommen()
    {
        var store = new MemorySettingStore();

        new HostExecutionCoordinator(store, null, Time()).Resolve([Stdio("alt")]);

        store.Written.Should().NotBeNull("aus einer unsichtbaren Vorgabe wird ein sichtbarer Wert");
        store.Written!.Allowed.Should().BeTrue();
        store.Written.Origin.Should().Be(HostExecutionOrigin.AdoptedFromExistingInstance);
        store.Written.WrittenAt.Should().Be(Now);
    }

    [Fact]
    public void Die_uebernahme_nennt_jeden_betroffenen_upstream_namentlich()
    {
        var store = new MemorySettingStore();

        var state = new HostExecutionCoordinator(store, null, Time())
            .Resolve([Http("web"), Stdio("zweiter"), Cli("erster", container: false), Cli("imcontainer", container: true)]);

        state.HostUpstreams.Should().HaveCount(2);
        state.HostUpstreams.Should().ContainSingle(entry => entry.StartsWith("erster", StringComparison.Ordinal));
        state.HostUpstreams.Should().ContainSingle(entry => entry.StartsWith("zweiter", StringComparison.Ordinal));
        state.HostUpstreams.Should().NotContain(entry => entry.Contains("imcontainer", StringComparison.Ordinal));
        store.Written!.Upstreams.Should().BeEquivalentTo(state.HostUpstreams);
    }

    /// <summary>
    /// Die Namen sind sortiert: Ein unveränderter Zustand darf beim nächsten Start nicht wie eine
    /// neue Meldung aussehen — sonst wird die Warnung zum Rauschen.
    /// </summary>
    [Fact]
    public void Die_namen_stehen_bei_jedem_start_in_derselben_reihenfolge()
    {
        var first = new HostExecutionCoordinator(new MemorySettingStore(), null, Time())
            .Resolve([Stdio("zulu"), Stdio("alpha")]);
        var second = new HostExecutionCoordinator(new MemorySettingStore(), null, Time())
            .Resolve([Stdio("alpha"), Stdio("zulu")]);

        first.HostUpstreams.Should().Equal(second.HostUpstreams);
    }

    /// <summary>
    /// Der eigene Reason-Code überlebt den Neustart. Verwandelte sich die Übernahme beim zweiten
    /// Start in ein reguläres „erlaubt", hätte der Betreiber die Warnung genau einmal gesehen — und
    /// die Instanz sähe danach aus wie eine, in der jemand entschieden hat.
    /// </summary>
    [Fact]
    public void Der_uebernommene_zustand_bleibt_beim_naechsten_start_eine_uebernahme()
    {
        var store = new MemorySettingStore();
        new HostExecutionCoordinator(store, null, Time()).Resolve([Stdio("alt")]);
        store.Persist();

        var second = new HostExecutionCoordinator(store, null, Time());
        var state = second.Resolve([Stdio("alt")]);

        state.Allowed.Should().BeTrue();
        state.Origin.Should().Be(HostExecutionOrigin.AdoptedFromExistingInstance);
        second.Evaluate(Stdio("alt")).ReasonCode
            .Should().Be(HostExecutionReason.AdoptedFromExistingInstance);
        store.Writes.Should().Be(1, "ein zweiter Start schreibt nicht erneut");
    }

    [Fact]
    public void Frische_instanz_verbietet_native_ausfuehrung()
    {
        var store = new MemorySettingStore();

        var state = new HostExecutionCoordinator(store, null, Time()).Resolve([]);

        state.Allowed.Should().BeFalse();
        state.Origin.Should().Be(HostExecutionOrigin.FreshInstanceDefault);
        state.ReasonCode.Should().Be(HostExecutionReason.Forbidden);
        store.Written!.Allowed.Should().BeFalse("auch die Vorgabe wird sichtbar festgeschrieben");
    }

    /// <summary>
    /// Eine Instanz, die nur isoliert laufende Upstreams hat, ist keine Bestandsinstanz im Sinne von
    /// E3 — es gibt nichts zu übernehmen.
    /// </summary>
    [Fact]
    public void Eine_instanz_ohne_native_upstreams_uebernimmt_nichts()
    {
        var state = new HostExecutionCoordinator(new MemorySettingStore(), null, Time())
            .Resolve([Http("web"), Cli("sicher", container: true)]);

        state.Adopted.Should().BeFalse();
        state.Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("ON", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void Die_ausdrueckliche_einstellung_gewinnt_ueber_den_bestand(string raw, bool allowed)
    {
        var store = new MemorySettingStore();

        var state = new HostExecutionCoordinator(store, raw, Time()).Resolve([Stdio("alt")]);

        state.Allowed.Should().Be(allowed);
        state.Origin.Should().Be(HostExecutionOrigin.Environment);
        state.Adopted.Should().BeFalse("wer die Einstellung gesetzt hat, hat entschieden");
        store.Written.Should().BeNull("eine ausdrueckliche Einstellung wird nicht ueberschrieben");
    }

    [Fact]
    public void Ein_undeutbarer_wert_heisst_nein_und_nicht_aus()
    {
        var coordinator = new HostExecutionCoordinator(new MemorySettingStore(), "ja bitte", Time());

        var state = coordinator.Resolve([Stdio("alt")]);

        state.Allowed.Should().BeFalse();
        state.ReasonCode.Should().Be(HostExecutionReason.Undetermined);
        coordinator.Evaluate(Stdio("alt")).ReasonCode.Should().Be(HostExecutionReason.Undetermined);
    }

    [Fact]
    public void Ein_unlesbarer_gespeicherter_wert_fuehrt_nicht_zu_einer_neuen_uebernahme()
    {
        var store = new MemorySettingStore { Broken = true };

        var state = new HostExecutionCoordinator(store, null, Time()).Resolve([Stdio("alt")]);

        state.Allowed.Should().BeFalse();
        state.Origin.Should().Be(HostExecutionOrigin.Unreadable);
        state.ReasonCode.Should().Be(HostExecutionReason.Undetermined);
        store.Written.Should().BeNull();
    }

    /// <summary>
    /// Schlägt das Schreiben fehl, läuft die Instanz trotzdem weiter. Eine bestehende Instanz
    /// stillzulegen, weil eine Datei nicht angelegt werden konnte, wäre genau der Ausfall, den
    /// ADR-0025 E3 verhindern will — der Fehlschlag steht stattdessen im Zustand.
    /// </summary>
    [Fact]
    public void Ein_fehlgeschlagenes_schreiben_legt_die_instanz_nicht_still()
    {
        var store = new MemorySettingStore { WriteFails = true };

        var state = new HostExecutionCoordinator(store, null, Time()).Resolve([Stdio("alt")]);

        state.Allowed.Should().BeTrue();
        state.Adopted.Should().BeTrue();
        state.Note.Should().Contain("nicht nach");
    }

    [Fact]
    public void Vor_der_ermittlung_wird_nichts_nativ_gestartet()
    {
        var coordinator = new HostExecutionCoordinator(new MemorySettingStore(), null, Time());

        coordinator.IsResolved.Should().BeFalse();
        coordinator.Evaluate(Stdio("alt")).Allowed.Should().BeFalse();
        coordinator.Evaluate(Stdio("alt")).ReasonCode.Should().Be(HostExecutionReason.Undetermined);
        coordinator.Evaluate(Http("web")).Allowed.Should().BeTrue("isolierte Upstreams sind nicht betroffen");
    }

    private static FakeTimeProvider Time() => new(Now);

    internal static UpstreamServerConfig Stdio(string slug)
        => new(slug, slug, UpstreamTransportKind.Stdio, true,
            Stdio: new StdioTransportOptions("/usr/bin/tool", []));

    internal static UpstreamServerConfig Http(string slug)
        => new(slug, slug, UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(new Uri("https://example.invalid/mcp")));

    internal static UpstreamServerConfig Cli(string slug, bool container)
        => new(slug, slug, UpstreamTransportKind.Cli, true,
            Cli: new CliTransportOptions(
                "/usr/bin/tool",
                [new CliToolSpec("lauf")],
                Isolation: container
                    ? new CliIsolationOptions(CliIsolationMode.Container, Image: "beispiel:1")
                    : null));
}

/// <summary>Ein Wertspeicher im Arbeitsspeicher, der auch die unangenehmen Fälle nachstellen kann.</summary>
internal sealed class MemorySettingStore : IHostExecutionSettingStore
{
    private HostExecutionSettingRecord? _stored;

    public HostExecutionSettingRecord? Written { get; private set; }

    public int Writes { get; private set; }

    public bool Broken { get; init; }

    public bool WriteFails { get; init; }

    public string Location => "test://host-execution";

    /// <summary>Macht den zuletzt geschriebenen Wert zu dem, was ein Neustart vorfände.</summary>
    public void Persist() => _stored = Written;

    public HostExecutionSettingRecord? Read()
        => Broken
            ? throw new HostExecutionSettingException("Der gespeicherte Wert ist beschädigt.")
            : _stored;

    public void Write(HostExecutionSettingRecord record)
    {
        if (WriteFails)
        {
            throw new IOException("Kein Schreibzugriff.");
        }

        Written = record;
        Writes++;
    }
}
