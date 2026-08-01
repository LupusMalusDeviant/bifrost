using System.Diagnostics;

using AwesomeAssertions;

using Bifrost.Abstractions.Operations;
using Bifrost.Core.Diagnostics;

using Xunit;

namespace Bifrost.Core.Tests.Diagnostics;

public class DiagnosticServiceTests
{
    [Fact]
    public async Task A_hanging_check_does_not_hold_up_the_report()
    {
        // Der eigentliche Vertrag des Dienstes: Der Bericht kommt. Der hängende Check ignoriert den
        // CancellationToken ausdrücklich — genau deshalb reicht ein 'await' nicht aus.
        var service = new DiagnosticService(
            DiagnosticWorld.Context(),
            [
                new HangingCheck(DiagnosticCodes.DatabaseReachable, DiagnosticScope.Database, TimeSpan.FromMilliseconds(150)),
                new ConstantCheck(DiagnosticCodes.DataDirectory, DiagnosticScope.Configuration, CheckStatus.Pass),
            ]);

        var stopwatch = Stopwatch.StartNew();
        var report = await service.RunAsync(DiagnosticScope.All, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        report.Checks.Should().HaveCount(2);
        report.Checks.Single(c => c.Code == DiagnosticCodes.DataDirectory).Status.Should().Be(CheckStatus.Pass);

        var hanging = report.Checks.Single(c => c.Code == DiagnosticCodes.DatabaseReachable);
        hanging.Status.Should().Be(CheckStatus.Fail);
        hanging.Summary.Should().Contain("Zeitlimit");
        report.HasFailures.Should().BeTrue();
    }

    [Fact]
    public async Task A_throwing_check_becomes_a_failure_and_not_a_crash()
    {
        var service = new DiagnosticService(
            DiagnosticWorld.Context(),
            [new ThrowingCheck(DiagnosticCodes.DataDirectory, new InvalidOperationException("kaputt"))]);

        var report = await service.RunAsync(DiagnosticScope.All, TestContext.Current.CancellationToken);

        var result = report.Checks.Single();
        result.Status.Should().Be(CheckStatus.Fail);
        result.Summary.Should().Contain("kaputt");
        result.SafeDetails!["fehlerart"].Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public void Two_checks_under_the_same_code_are_rejected_at_construction()
    {
        var act = () => new DiagnosticService(
            DiagnosticWorld.Context(),
            [
                new ConstantCheck(DiagnosticCodes.DataDirectory, DiagnosticScope.Configuration, CheckStatus.Pass),
                new ConstantCheck(DiagnosticCodes.DataDirectory, DiagnosticScope.Network, CheckStatus.Fail),
            ]);

        act.Should().Throw<ArgumentException>().WithMessage("*doppelt*");
    }

    [Fact]
    public async Task A_narrowed_scope_runs_only_its_own_checks()
    {
        var service = new DiagnosticService(
            DiagnosticWorld.Context(),
            [
                new ConstantCheck(DiagnosticCodes.DataDirectory, DiagnosticScope.Configuration, CheckStatus.Pass),
                new ConstantCheck(DiagnosticCodes.ListenPort, DiagnosticScope.Network, CheckStatus.Pass),
            ]);

        var report = await service.RunAsync(DiagnosticScope.Network, TestContext.Current.CancellationToken);

        report.Checks.Should().ContainSingle().Which.Code.Should().Be(DiagnosticCodes.ListenPort);
        report.Scope.Should().Be(DiagnosticScope.Network);
    }

    [Fact]
    public async Task The_report_is_ordered_by_code_so_two_runs_are_comparable()
    {
        var service = new DiagnosticService(
            DiagnosticWorld.Context(),
            [
                new ConstantCheck(DiagnosticCodes.WasiHost, DiagnosticScope.Runtime, CheckStatus.Pass),
                new ConstantCheck(DiagnosticCodes.DataDirectory, DiagnosticScope.Configuration, CheckStatus.Pass),
                new ConstantCheck(DiagnosticCodes.ListenPort, DiagnosticScope.Network, CheckStatus.Pass),
            ]);

        var report = await service.RunAsync(DiagnosticScope.All, TestContext.Current.CancellationToken);

        report.Checks.Select(c => c.Code).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Warnings_and_failures_are_visible_on_the_report()
    {
        var service = new DiagnosticService(
            DiagnosticWorld.Context(),
            [
                new ConstantCheck(DiagnosticCodes.DataDirectory, DiagnosticScope.Configuration, CheckStatus.Warning),
                new ConstantCheck(DiagnosticCodes.ListenPort, DiagnosticScope.Network, CheckStatus.Pass),
            ]);

        var report = await service.RunAsync(DiagnosticScope.All, TestContext.Current.CancellationToken);

        report.HasWarnings.Should().BeTrue();
        report.HasFailures.Should().BeFalse();
    }

    [Fact]
    public async Task The_shipped_set_answers_for_every_area_without_any_probe_wired_up()
    {
        // Der Zustand, in dem WP2.4 abgibt: keine Datenbank-, keine Upstream-Sonde. Nichts darf
        // still bestehen, und der Bericht muss trotzdem vollständig sein.
        var context = DiagnosticWorld.Context(new Dictionary<string, string>
        {
            ["BIFROST_DATA_DIR"] = "/data",
            ["ASPNETCORE_URLS"] = "http://+:8080",
        });
        var service = DiagnosticService.CreateDefault(context);

        var report = await service.RunAsync(DiagnosticScope.All, TestContext.Current.CancellationToken);

        report.Checks.Select(c => c.Code).Should().BeEquivalentTo(DiagnosticCodes.InstanceReport);
        report.Checks.Where(c => c.Status is CheckStatus.Skipped)
            .Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Summary), "Skipped ohne Grund ist stilles Bestehen");
        report.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }
}
