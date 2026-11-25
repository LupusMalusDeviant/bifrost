using System.Net;
using AwesomeAssertions;
using Faolan.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace Faolan.Upstream.Tests;

/// <summary>
/// Plan 0005 H9 (N42): Die bereinigte Kette ist in der Tiefe begrenzt. Eine Ablehnung (401/403)
/// hinter der Grenze darf dabei nicht verloren gehen — sonst würde aus einer abgelehnten Anmeldung
/// „nicht erreichbar", nur weil eine äußere Meldung das Token zitierte.
/// </summary>
public sealed class HandshakeFailureDepthTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void A_refusal_deeper_than_the_limit_survives_the_rebuild(HttpStatusCode status)
    {
        var token = "tok-" + Guid.NewGuid().ToString("N");
        Exception chain = new HttpRequestException(HttpRequestError.Unknown, $"abgelehnt: {token}", null, status);
        for (var i = 0; i < 9; i++)
        {
            chain = new McpException($"Ebene {i}: {token}", chain);
        }

        var sanitized = HandshakeFailure.Sanitize(chain, [token]);

        var links = Links(sanitized).ToList();
        links.OfType<HttpRequestException>().Should().ContainSingle()
            .Which.StatusCode.Should().Be(status, "die Fehlerart hängt an diesem Glied");
        links.Should().AllSatisfy(link => link.Message.Should().NotContain(token));
    }

    [Fact]
    public void A_deep_chain_without_a_refusal_stays_bounded()
    {
        var token = "tok-" + Guid.NewGuid().ToString("N");
        Exception chain = new IOException($"weg: {token}");
        for (var i = 0; i < 12; i++)
        {
            chain = new McpException($"Ebene {i}: {token}", chain);
        }

        var sanitized = HandshakeFailure.Sanitize(chain, [token]);

        Links(sanitized).Should().HaveCount(8);
    }

    private static IEnumerable<Exception> Links(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
