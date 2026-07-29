using System.Net;
using AwesomeAssertions;
using McpMcp.Server;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Zweiter Fund aus dem echten Betrieb (Badwolf, 2026-07-28): Hinter dem TLS-Proxy baute der
/// Gateway seine Umleitungen aus dem, was er selbst sah — <c>http://</c>. Wer eine geschützte Seite
/// abgemeldet aufrief, landete bei „400 The plain HTTP request was sent to HTTPS port".
/// <para>
/// Die Auswertung ist <b>opt-in</b>, und das ist der Punkt: Steht der Gateway direkt im Netz, kann
/// jeder Client <c>X-Forwarded-Proto: https</c> behaupten.
/// </para>
/// </summary>
public class ForwardedProxyOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_setting_forwarded_headers_stay_off(string? configured)
        => ForwardedProxyOptions.TryCreate(configured, out _).Should().BeFalse(
            "ungefragt einem X-Forwarded-Proto zu glauben hiesse, jedem Client zu glauben");

    /// <summary>
    /// Für die Middleware bedeutet „beide Listen leer": keine Herkunftsprüfung. Das ist der
    /// Container-Fall — der Proxy kommt über das Docker-Netz, und die Vorgabe (nur Loopback) passt
    /// dort nie.
    /// </summary>
    [Fact]
    public void Any_trusts_every_sender()
    {
        ForwardedProxyOptions.TryCreate("any", out var options).Should().BeTrue();

        options.KnownProxies.Should().BeEmpty();
        options.KnownIPNetworks.Should().BeEmpty();
        options.ForwardLimit.Should().Be(1, "ein Proxy, keine Kette");
    }

    [Fact]
    public void Single_addresses_and_cidr_ranges_are_both_accepted()
    {
        ForwardedProxyOptions.TryCreate("172.17.0.1, 10.0.0.0/8", out var options).Should().BeTrue();

        options.KnownProxies.Should().ContainSingle()
            .Which.Should().Be(IPAddress.Parse("172.17.0.1"));
        options.KnownIPNetworks.Should().ContainSingle();
    }

    /// <summary>
    /// Ein Tippfehler darf nicht still zu „aus" führen — und erst recht nicht zu „any". Beides wäre
    /// eine Sicherheitsaussage, die niemand getroffen hat.
    /// </summary>
    [Fact]
    public void A_typo_fails_loudly_instead_of_silently_trusting_everyone()
    {
        var act = () => ForwardedProxyOptions.TryCreate("nginx-proxy-manager", out _);

        act.Should().Throw<InvalidOperationException>().WithMessage("*weder eine IP-Adresse*");
    }

    [Fact]
    public void The_scheme_is_what_this_is_for()
    {
        ForwardedProxyOptions.TryCreate("any", out var options);

        options.ForwardedHeaders.Should().HaveFlag(
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
            "ohne das Schema bleibt die Umleitung http:// — genau der Fehler aus dem Betrieb");
    }
}
