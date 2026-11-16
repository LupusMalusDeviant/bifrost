using AwesomeAssertions;
using Bifrost.Abstractions;
using Xunit;

namespace Bifrost.Upstream.Tests;

/// <summary>Platzhalter bis WP1 — hält das Testprojekt lauffähig und prüft Vertrags-Defaults.</summary>
public class ContractSmokeTests
{
    [Fact]
    public void RestartPolicy_default_uses_exponential_backoff()
    {
        var policy = RestartPolicy.Default;

        // Vertrag „Selbsttätige Wiederverbindung": ohne Obergrenze, 5 s Anfang, Deckel 5 min.
        // Die alte Vorgabe (5 Versuche in rund 31 s, dann für immer Failed) war der Vorfall
        // vom 2026-11-16.
        policy.MaxRetries.Should().BeNull();
        policy.InitialBackoff.Should().Be(TimeSpan.FromSeconds(5));
        policy.MaxBackoff.Should().Be(TimeSpan.FromMinutes(5));
        policy.BackoffMultiplier.Should().BeGreaterThan(1.0);
        policy.MaxBackoff.Should().BeGreaterThan(policy.InitialBackoff);
    }

    [Fact]
    public void DrainPolicy_immediate_has_zero_grace_period()
    {
        DrainPolicy.Immediate.GracePeriod.Should().Be(TimeSpan.Zero);
        DrainPolicy.Graceful(TimeSpan.FromSeconds(30)).GracePeriod.Should().Be(TimeSpan.FromSeconds(30));
    }
}
