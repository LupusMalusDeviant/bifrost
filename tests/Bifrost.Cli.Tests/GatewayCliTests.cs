using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace Bifrost.Cli.Tests;

public class GatewayCliTests
{
    [Fact]
    public async Task Search_uses_public_tools_contract_and_has_stable_json_output()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """
            {"tools":[
              {"name":"git__status","description":"repository status","inputSchema":{}},
              {"name":"mail__send","description":"send mail","inputSchema":{}}
            ]}
            """));
        using var client = Client(handler);
        var output = new StringWriter();
        var cli = new GatewayCli(client, TextReader.Null, output, TextWriter.Null, jsonOutput: true);

        var exit = await cli.RunAsync(
            ["tools", "search", "git"], TestContext.Current.CancellationToken);

        exit.Should().Be(GatewayCli.Success);
        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsolutePath.Should().Be("/api/v1/tools");
        using var document = JsonDocument.Parse(output.ToString());
        document.RootElement.GetProperty("tools").GetArrayLength().Should().Be(1);
        document.RootElement.GetProperty("tools")[0].GetProperty("name")
            .GetString().Should().Be("git__status");
    }

    [Fact]
    public async Task Invoke_can_read_arguments_from_stdin_without_putting_secrets_in_argv()
    {
        var handler = new RecordingHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/api/v1/tools/vault__read/invoke");
            return Json(HttpStatusCode.OK, """{"status":"Success","content":{"value":1}}""");
        });
        using var client = Client(handler);
        var output = new StringWriter();
        var cli = new GatewayCli(
            client,
            new StringReader("""{"secretReference":"vault:prod/key"}"""),
            output,
            TextWriter.Null,
            jsonOutput: true);

        var exit = await cli.RunAsync(
            ["tools", "invoke", "vault__read", "--file", "-"],
            TestContext.Current.CancellationToken);

        exit.Should().Be(GatewayCli.Success);
        handler.Bodies.Should().ContainSingle()
            .Which.Should().Be("""{"secretReference":"vault:prod/key"}""");
    }

    [Fact]
    public async Task Approval_required_has_a_dedicated_exit_code()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.Conflict,
            """{"status":"ApprovalRequired","error":"Freigabe erforderlich"}"""));
        using var client = Client(handler);
        var error = new StringWriter();
        var cli = new GatewayCli(
            client, TextReader.Null, TextWriter.Null, error, jsonOutput: true);

        var exit = await cli.RunAsync(
            ["tools", "invoke", "danger__delete", "--json", "{}"],
            TestContext.Current.CancellationToken);

        exit.Should().Be(GatewayCli.ApprovalRequired);
        error.ToString().Should().Contain("ApprovalRequired");
    }

    [Fact]
    public async Task Server_enable_uses_management_api_only()
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = Client(handler);
        var cli = new GatewayCli(
            client, TextReader.Null, TextWriter.Null, TextWriter.Null, jsonOutput: false);

        var exit = await cli.RunAsync(
            ["servers", "enable", id.ToString()], TestContext.Current.CancellationToken);

        exit.Should().Be(GatewayCli.Success);
        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be($"/api/v1/servers/{id}/enabled");
        handler.Bodies.Single().Should().Be("""{"enabled":true}""");
    }

    [Fact]
    public async Task Configuration_accepts_token_from_stdin()
    {
        var (configuration, token) = await CliConfiguration.LoadAsync(
            configPath: null,
            tokenFromStdin: true,
            new StringReader("mcpk_from_stdin\n"),
            TestContext.Current.CancellationToken);

        configuration.Endpoint.Should().Be(new Uri("http://localhost:8080/"));
        token.Should().Be("mcpk_from_stdin");
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://gateway.example/"),
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return responseFactory(request);
        }
    }
}
