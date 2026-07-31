using AwesomeAssertions;
using Bifrost.Server;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Bifrost.Integration.Tests.Gateway;

/// <summary>
/// Der erste Fund aus dem echten Betrieb (Badwolf, 2026-07-28): Die Anmeldung in der Web-UI ging
/// über <c>http://</c> durch — Server antwortete 302 und setzte das Cookie — und der nächste
/// Seitenaufruf war wieder anonym. Ursache: Das Sitzungs-Cookie trägt außerhalb von Development
/// <c>Secure</c> (NFR-04), und ein Browser verwirft ein solches Cookie über Klartext-HTTP
/// <b>stillschweigend</b>.
/// <para>
/// Der Mangel war nicht die Cookie-Regel — die ist richtig. Der Mangel war das Schweigen: kein
/// Hinweis im Browser, keiner im Log, und das Symptom (Login-Schleife) zeigt nicht auf die Ursache.
/// </para>
/// </summary>
public class SessionCookieWarningTests
{
    [Fact]
    public void Plain_http_with_an_always_secure_cookie_is_the_case_that_needs_saying()
        => AuthEndpoints.WouldDropSessionCookie(
            requestIsHttps: false, forwardedProtoIsHttps: false, CookieSecurePolicy.Always)
            .Should().BeTrue();

    /// <summary>
    /// Der wichtigere Test: <b>kein</b> Alarm, wenn davor ein TLS-Proxy steht. Das ist der
    /// vorgesehene Produktionsaufbau (NFR-04); eine Warnung, die dort bei jeder Anmeldung
    /// erscheint, wird nach dem dritten Mal ignoriert — und dann fehlt sie, wenn sie zählt.
    /// </summary>
    [Fact]
    public void Behind_a_tls_proxy_nothing_is_said()
        => AuthEndpoints.WouldDropSessionCookie(
            requestIsHttps: false, forwardedProtoIsHttps: true, CookieSecurePolicy.Always)
            .Should().BeFalse();

    [Fact]
    public void Direct_https_says_nothing()
        => AuthEndpoints.WouldDropSessionCookie(
            requestIsHttps: true, forwardedProtoIsHttps: false, CookieSecurePolicy.Always)
            .Should().BeFalse();

    /// <summary>
    /// In Development steht die Regel auf <c>SameAsRequest</c> — dort hält die Anmeldung über HTTP,
    /// und es gibt nichts zu melden.
    /// </summary>
    [Theory]
    [InlineData(CookieSecurePolicy.SameAsRequest)]
    [InlineData(CookieSecurePolicy.None)]
    public void A_cookie_that_is_not_forced_secure_survives_plain_http(CookieSecurePolicy policy)
        => AuthEndpoints.WouldDropSessionCookie(
            requestIsHttps: false, forwardedProtoIsHttps: false, policy)
            .Should().BeFalse();
}
