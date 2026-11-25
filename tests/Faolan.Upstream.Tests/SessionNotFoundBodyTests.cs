using AwesomeAssertions;
using Xunit;

namespace Faolan.Upstream.Tests;

/// <summary>
/// Plan 0005 E12: Als verlorene Sitzung gilt nur ein 404, dessen Antwortkörper ein JSON-RPC-Fehler
/// ist. Ein Proxy-404 (leer, HTML, anderes JSON) beweist nicht, dass nichts ausgeführt wurde.
/// </summary>
public sealed class SessionNotFoundBodyTests
{
    private const string Prefix = "Response status code does not indicate success: 404 (Not Found). Response body: ";

    [Fact]
    public void A_json_rpc_error_body_is_the_mcp_server()
        => SdkUpstreamConnection.IsMcpErrorBody(
            Prefix + """{"jsonrpc":"2.0","error":{"code":-32001,"message":"Session not found"},"id":null}""")
            .Should().BeTrue();

    [Theory]
    [InlineData("Response status code does not indicate success: 404 (Not Found).")]
    [InlineData(Prefix)]
    [InlineData(Prefix + "<html><body>404 Not Found</body></html>")]
    [InlineData(Prefix + """{"error":"not found"}""")]
    [InlineData(Prefix + """{"jsonrpc":"2.0","result":{}}""")]
    [InlineData(Prefix + """{"jsonrpc":"2.0","error":{"code":-32001""")]
    public void Anything_else_is_not(string message)
        => SdkUpstreamConnection.IsMcpErrorBody(message).Should().BeFalse();

    /// <summary>
    /// Ein JSON-RPC-Fehlerobjekt hat eine ganzzahlige <c>code</c> und eine Zeichenkette
    /// <c>message</c> (JSON-RPC 2.0, 5.1). Ein Zwischenglied, das irgendein Objekt unter
    /// <c>error</c> liefert, ist nicht der MCP-Server.
    /// </summary>
    [Theory]
    [InlineData(Prefix + """{"jsonrpc":"2.0","error":{}}""")]
    [InlineData(Prefix + """{"jsonrpc":"2.0","error":{"message":"Session not found"}}""")]
    [InlineData(Prefix + """{"jsonrpc":"2.0","error":{"code":"-32001","message":"Session not found"}}""")]
    [InlineData(Prefix + """{"jsonrpc":"2.0","error":{"code":-32001.5,"message":"Session not found"}}""")]
    [InlineData(Prefix + """{"jsonrpc":"2.0","error":{"code":-32001}}""")]
    [InlineData(Prefix + """{"jsonrpc":"2.0","error":{"code":-32001,"message":null}}""")]
    [InlineData(Prefix + """{"jsonrpc":"2.0","error":{"code":-32001,"message":42}}""")]
    public void An_error_object_without_integer_code_and_string_message_is_not(string message)
        => SdkUpstreamConnection.IsMcpErrorBody(message).Should().BeFalse();
}
