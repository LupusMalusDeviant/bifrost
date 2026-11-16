using AwesomeAssertions;
using Bifrost.Server;
using Xunit;

namespace Bifrost.Integration.Tests.Upstreams;

/// <summary>
/// Vertrag „Selbsttätige Wiederverbindung", Abschnitt 3.2: die drei Betriebsoptionen, ihre Grenzen
/// und was bei einem ungültigen Wert geschieht.
/// </summary>
public sealed class UpstreamReconnectSettingsTests
{
    private static Func<string, string?> Settings(params (string Key, string Value)[] values)
        => key => values.Where(v => v.Key == key).Select(v => v.Value).FirstOrDefault();

    [Fact]
    public void Without_any_setting_the_contract_defaults_apply()
    {
        var (options, warnings) = UpstreamReconnectSettings.Read(Settings());

        options.DefaultRestartPolicy.MaxRetries.Should().BeNull("ohne Obergrenze");
        options.DefaultRestartPolicy.InitialBackoff.Should().Be(TimeSpan.FromSeconds(5));
        options.DefaultRestartPolicy.BackoffMultiplier.Should().Be(2.0);
        options.DefaultRestartPolicy.MaxBackoff.Should().Be(TimeSpan.FromMinutes(5));
        options.AuthenticationRetryInterval.Should().Be(TimeSpan.FromMinutes(15));
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Valid_values_are_taken()
    {
        var (options, warnings) = UpstreamReconnectSettings.Read(Settings(
            (UpstreamReconnectSettings.InitialSecondsKey, "2"),
            (UpstreamReconnectSettings.MaxSecondsKey, "120"),
            (UpstreamReconnectSettings.AuthSecondsKey, "3600")));

        options.DefaultRestartPolicy.InitialBackoff.Should().Be(TimeSpan.FromSeconds(2));
        options.DefaultRestartPolicy.MaxBackoff.Should().Be(TimeSpan.FromSeconds(120));
        options.AuthenticationRetryInterval.Should().Be(TimeSpan.FromHours(1));
        warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("301")]
    [InlineData("fuenf")]
    [InlineData("5.5")]
    public void An_invalid_value_falls_back_to_the_default_and_warns_without_quoting_it(string raw)
    {
        var (options, warnings) = UpstreamReconnectSettings.Read(Settings(
            (UpstreamReconnectSettings.InitialSecondsKey, raw)));

        options.DefaultRestartPolicy.InitialBackoff.Should().Be(
            TimeSpan.FromSeconds(5), "ein Tippfehler in einer Wartefrist hält den Start nicht auf");
        warnings.Should().ContainSingle()
            .Which.Should().Contain(UpstreamReconnectSettings.InitialSecondsKey)
            .And.Contain("von 1 bis 300")
            .And.NotContain($" {raw} ", "was in einer vertauschten Variable steht, weiß hier niemand");
    }

    [Fact]
    public void The_cap_may_not_lie_below_the_initial_delay()
    {
        var (options, warnings) = UpstreamReconnectSettings.Read(Settings(
            (UpstreamReconnectSettings.InitialSecondsKey, "60"),
            (UpstreamReconnectSettings.MaxSecondsKey, "30")));

        options.DefaultRestartPolicy.InitialBackoff.Should().Be(TimeSpan.FromSeconds(60));
        options.DefaultRestartPolicy.MaxBackoff.Should().Be(TimeSpan.FromSeconds(300));
        warnings.Should().ContainSingle().Which.Should().Contain(UpstreamReconnectSettings.MaxSecondsKey);
    }

    [Fact]
    public void Dependent_defaults_move_up_instead_of_becoming_invalid()
    {
        // Wer nur den Deckel auf eine Stunde setzt, hat damit keine zu kurze Auth-Frist konfiguriert.
        var (options, warnings) = UpstreamReconnectSettings.Read(Settings(
            (UpstreamReconnectSettings.MaxSecondsKey, "3600")));

        options.DefaultRestartPolicy.MaxBackoff.Should().Be(TimeSpan.FromHours(1));
        options.AuthenticationRetryInterval.Should().Be(
            TimeSpan.FromHours(1), "die Frist nach abgelehnter Anmeldung liegt nie unter dem Deckel");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void An_auth_interval_below_the_cap_is_rejected()
    {
        var (options, warnings) = UpstreamReconnectSettings.Read(Settings(
            (UpstreamReconnectSettings.AuthSecondsKey, "60")));

        options.AuthenticationRetryInterval.Should().Be(TimeSpan.FromMinutes(15));
        warnings.Should().ContainSingle().Which.Should().Contain(UpstreamReconnectSettings.AuthSecondsKey);
    }
}
