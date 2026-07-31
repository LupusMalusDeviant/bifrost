using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Checks;
using Bifrost.Core.Execution;

using Xunit;

namespace Bifrost.Core.Tests.Execution;

/// <summary>
/// Die Entscheidung selbst: Was ist nativ, was heißt „unbekannt", und trägt jeder Fall den Code,
/// auf den ein Runbook zeigen kann (ADR-0025 E1)?
/// </summary>
public sealed class HostExecutionPolicyTests
{
    [Fact]
    public void Stdio_laeuft_nativ_solange_es_kein_isolationsmodell_hat()
        => NativeExecution.RunsOnHost(HostExecutionAdoptionTests.Stdio("s")).Should().BeTrue();

    [Fact]
    public void Cli_ohne_isolationsangabe_laeuft_nativ()
        => NativeExecution.RunsOnHost(HostExecutionAdoptionTests.Cli("c", container: false))
            .Should().BeTrue("ein fehlendes Feld darf nichts erlauben, was ein gesetztes verboete");

    [Theory]
    [InlineData(UpstreamTransportKind.StreamableHttp)]
    [InlineData(UpstreamTransportKind.OpenApi)]
    [InlineData(UpstreamTransportKind.OpenRpc)]
    [InlineData(UpstreamTransportKind.Wasi)]
    public void Isolierte_transporte_beruehren_die_policy_nicht(UpstreamTransportKind kind)
        => NativeExecution.RunsOnHost(kind, null).Should().BeFalse();

    /// <summary>
    /// Ein Transport, den die Einordnung nicht kennt, ist neu. Ihn durchzuwinken wäre der stille
    /// Rückfall, den E1 verbietet — deshalb gilt er als nativ, bis jemand ihn einträgt.
    /// </summary>
    [Fact]
    public void Ein_unbekannter_transport_gilt_als_nativ()
        => NativeExecution.RunsOnHost((UpstreamTransportKind)99, null).Should().BeTrue();

    [Fact]
    public void Ein_isolierter_upstream_bekommt_den_code_fuer_nicht_betroffen()
        => HostExecutionPolicy.FreshInstance()
            .Evaluate(HostExecutionAdoptionTests.Http("web")).ReasonCode
            .Should().Be(HostExecutionReason.NotNative);

    [Fact]
    public void Die_absage_nennt_den_naechsten_schritt()
    {
        var decision = HostExecutionPolicy.FreshInstance()
            .Evaluate(HostExecutionAdoptionTests.Stdio("s"));

        decision.Allowed.Should().BeFalse();
        decision.ReasonCode.Should().Be(HostExecutionReason.Forbidden);
        decision.Remediation.Should().NotBeNullOrWhiteSpace();
        decision.Remediation.Should().Contain(HostExecutionSwitch.Name);
    }

    /// <summary>
    /// „Erlaubt, weil jemand das wollte" und „erlaubt, weil es schon immer so lief" sind
    /// verschiedene Aussagen — und nur die zweite verlangt eine Handlung. Deshalb sind es zwei
    /// Codes und nicht einer mit einem Zusatz im Text.
    /// </summary>
    [Fact]
    public void Erlaubt_und_uebernommen_sind_zwei_verschiedene_aussagen()
    {
        var wanted = HostExecutionPolicy.AllowedByOperator()
            .Evaluate(HostExecutionAdoptionTests.Stdio("s"));
        var adopted = new HostExecutionCoordinator(new MemorySettingStore(), null, TimeProvider.System);
        adopted.Resolve([HostExecutionAdoptionTests.Stdio("s")]);
        var carried = adopted.Evaluate(HostExecutionAdoptionTests.Stdio("s"));

        wanted.Allowed.Should().BeTrue();
        carried.Allowed.Should().BeTrue();
        wanted.ReasonCode.Should().Be(HostExecutionReason.Allowed);
        carried.ReasonCode.Should().Be(HostExecutionReason.AdoptedFromExistingInstance);
        wanted.Remediation.Should().BeNull("wer entschieden hat, muss nichts tun");
        carried.Remediation.Should().NotBeNullOrWhiteSpace("eine Uebernahme verlangt eine Handlung");
    }

    [Fact]
    public void Die_reason_codes_liegen_im_reservierten_bereich_und_sind_eindeutig()
    {
        string[] codes =
        [
            HostExecutionReason.NotNative,
            HostExecutionReason.Allowed,
            HostExecutionReason.Forbidden,
            HostExecutionReason.AdoptedFromExistingInstance,
            HostExecutionReason.Undetermined,
        ];

        codes.Should().OnlyHaveUniqueItems();
        codes.Should().OnlyContain(code => code.StartsWith("BFR-POL-", StringComparison.Ordinal));

        // Die Diagnosecodes teilen sich das Praefix, duerfen aber keine Nummer doppelt belegen.
        DiagnosticCodes.All.Intersect(codes, StringComparer.Ordinal).Should().BeEmpty();
    }
}

/// <summary>
/// Die Policyentscheidung im Diagnosebericht (WP3.1 Punkt 5). Der Bericht ist die Stelle, an der ein
/// Betreiber nachliest, wie seine Instanz tatsächlich eingestellt ist.
/// </summary>
public sealed class HostExecutionDiagnosticTests
{
    [Fact]
    public async Task Die_uebernahme_erscheint_als_warnung_mit_namen()
    {
        var state = Resolve([HostExecutionAdoptionTests.Stdio("alt"), HostExecutionAdoptionTests.Http("web")]);

        var check = await new HostExecutionAdoptionCheck()
            .RunAsync(Context(state), TestContext.Current.CancellationToken);

        check.Code.Should().Be(DiagnosticCodes.HostExecutionAdoption);
        check.Status.Should().Be(CheckStatus.Warning);
        check.Summary.Should().Contain("alt");
        check.Summary.Should().NotContain("web");
        check.Remediation.Should().Contain(HostExecutionSwitch.Name);
        check.SafeDetails!["betroffene_upstreams"].Should().Contain("alt");
    }

    [Fact]
    public async Task Ohne_uebernahme_bleibt_der_bericht_ruhig()
    {
        var check = await new HostExecutionAdoptionCheck()
            .RunAsync(Context(Resolve([])), TestContext.Current.CancellationToken);

        check.Status.Should().Be(CheckStatus.Pass);
    }

    [Fact]
    public async Task Der_zustand_der_policy_steht_im_bericht()
    {
        var check = await new HostExecutionPolicyCheck()
            .RunAsync(Context(Resolve([])), TestContext.Current.CancellationToken);

        check.Code.Should().Be(DiagnosticCodes.HostExecutionPolicy);
        check.Status.Should().Be(CheckStatus.Pass);
        check.SafeDetails!["native_ausfuehrung"].Should().Be("verboten");
        check.SafeDetails["reason_code"].Should().Be(HostExecutionReason.Forbidden);
    }

    [Fact]
    public async Task Eine_unklare_policy_ist_ein_fehlbefund_und_kein_uebersprungener()
    {
        var state = new HostExecutionCoordinator(new MemorySettingStore(), "vielleicht", TimeProvider.System)
            .Resolve([HostExecutionAdoptionTests.Stdio("alt")]);

        var check = await new HostExecutionPolicyCheck()
            .RunAsync(Context(state), TestContext.Current.CancellationToken);

        check.Status.Should().Be(CheckStatus.Fail);
        check.Remediation.Should().Contain(HostExecutionSwitch.Name);
    }

    [Fact]
    public async Task Ohne_verdrahtete_policy_meldet_der_check_uebersprungen_statt_bestanden()
    {
        var check = await new HostExecutionPolicyCheck()
            .RunAsync(Context(null), TestContext.Current.CancellationToken);

        check.Status.Should().Be(CheckStatus.Skipped);
    }

    [Fact]
    public void Beide_checks_sind_im_ausgelieferten_satz()
        => DiagnosticService.DefaultChecks.Select(check => check.Code)
            .Should().Contain([DiagnosticCodes.HostExecutionPolicy, DiagnosticCodes.HostExecutionAdoption]);

    private static HostExecutionState Resolve(UpstreamServerConfig[] existing)
        => new HostExecutionCoordinator(new MemorySettingStore(), null, TimeProvider.System)
            .Resolve(existing);

    private static DiagnosticContext Context(HostExecutionState? state)
        => new()
        {
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            HostExecution = state,
        };
}
