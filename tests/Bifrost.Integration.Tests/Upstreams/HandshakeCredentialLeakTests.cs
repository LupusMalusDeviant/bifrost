using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Core.Execution;
using Bifrost.Core.Upstreams;
using Bifrost.Upstream;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Bifrost.Integration.Tests.Upstreams;

/// <summary>
/// Plan 0005 G1: Der Handshake (<c>initialize</c>) legt das OAuth-Token schon vor, bevor eine
/// Verbindung entsteht, die es nennen könnte. Zitiert der Upstream es in seiner Absage, darf es
/// weder im Status noch im Audit, im Log oder in der Ausnahme an einen wartenden Aufrufer stehen —
/// und die Einordnung des Fehlers bleibt, was sie war.
/// </summary>
public sealed class HandshakeCredentialLeakTests : IAsyncLifetime
{
    private static readonly JsonElement Hello = JsonSerializer.SerializeToElement(new { message = "hallo" });

    private readonly QuotingUpstream _upstream = new();
    private readonly RecordingAudit _audit = new();
    private readonly CapturingLogger _log = new();
    private readonly Tokens _tokens = new();
    private UpstreamSupervisor? _supervisor;

    /// <summary>Je Lauf neu erzeugt; kein Schlüssel im Repository.</summary>
    private readonly string _accessToken = "at-" + Guid.NewGuid().ToString("N");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _upstream.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }

        await _upstream.DisposeAsync();
    }

    /// <summary>
    /// Der Upstream lehnt <c>initialize</c> mit einem JSON-RPC-Fehler (HTTP 200) ab, der das vorgelegte
    /// Token zitiert. Es steht danach nirgends, wo es hingehen könnte.
    /// </summary>
    [Fact]
    public async Task A_handshake_error_quoting_the_token_shows_it_nowhere()
    {
        _upstream.FailInitialize = true;
        var supervisor = CreateSupervisor();

        var id = await AddAsync(supervisor);
        await FailedAttemptBookedAsync(supervisor, id);

        var status = supervisor.GetStatus(id)!;
        status.Reconnect!.Kind.Should().Be(
            UpstreamFailureKind.Unreachable, "ein JSON-RPC-Fehler ist keine abgelehnte Anmeldung — daran ändert die Bereinigung nichts");
        _upstream.QuotedInitializes.Should().BeGreaterThan(0, "der Upstream hat das Token wirklich zitiert");
        AssertNowhere(status.LastError);
    }

    /// <summary>
    /// Eine Sitzung geht verloren, und der Neuaufbau scheitert am Handshake, dessen Absage das Token
    /// zitiert. Der Aufrufer, der auf den Neuaufbau gewartet hat, sieht es nicht — weder in der
    /// Meldung noch in einer inneren Ausnahme.
    /// </summary>
    [Fact]
    public async Task A_failed_rebuild_handshake_does_not_hand_the_token_to_the_waiting_caller()
    {
        var supervisor = CreateSupervisor();
        var id = await AddAsync(supervisor);
        await IntegrationSupport.WaitUntilAsync(
            () => supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: "der Upstream muss zuerst verbunden sein");
        var connection = supervisor.GetConnection(id)!;

        _upstream.FailInitialize = true;
        _upstream.RevokeSessions();
        var act = () => connection.CallToolAsync("echo", Hello, Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        thrown.Should().NotBeOfType<TimeoutException>("der Aufruf endet am gescheiterten Neuaufbau, nicht an der Testfrist");
        _upstream.QuotedInitializes.Should().BeGreaterThan(0, "der Neuaufbau ist wirklich am zitierenden Handshake gescheitert");
        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            current.Message.Should().NotContain(_accessToken, "auch keine innere Ausnahme trägt das Token zum Aufrufer");
            current.Message.Should().NotContain(Prefix);
        }

        thrown.ToString().Should().NotContain(Prefix);

        await FailedAttemptBookedAsync(supervisor, id);
        AssertNowhere(supervisor.GetStatus(id)!.LastError);
    }

    /// <summary>
    /// Der Connector selbst: Seine Ausnahme nennt das Token nicht, und sie bleibt eingeordnet — ein
    /// 401 bleibt ein <see cref="HttpRequestException"/> mit Statuscode (abgelehnte Anmeldung), ein
    /// JSON-RPC-Fehler bleibt eine <see cref="ModelContextProtocol.McpProtocolException"/>.
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    public async Task The_connector_scrubs_the_handshake_failure_and_keeps_its_kind(int status)
    {
        _upstream.FailInitialize = true;
        _upstream.InitializeStatus = status;
        var connector = new StreamableHttpUpstreamConnector(tokens: _tokens);
        var id = new ServerId(Guid.NewGuid());
        await _tokens.SaveAsync(Token(id), Ct);

        var act = () => connector.ConnectAsync(id, Config(), Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        _upstream.QuotedInitializes.Should().BeGreaterThan(0);
        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            current.Message.Should().NotContain(Prefix);
        }

        if (status == 401)
        {
            thrown.Should().BeOfType<HttpRequestException>()
                .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            UpstreamFailure.Classify(thrown).Should().Be(UpstreamFailureKind.Authentication);
        }
        else
        {
            thrown.Should().BeOfType<ModelContextProtocol.McpProtocolException>();
            UpstreamFailure.Classify(thrown).Should().Be(UpstreamFailureKind.Unreachable);
        }

        UpstreamFailure.TypeChain(thrown).Should().Contain(thrown.GetType().Name, "die Typnamen der ursprünglichen Kette bleiben");
    }

    private string Prefix => _accessToken[..UpstreamConnectionTester.MinimumPrefixLength];

    private void AssertNowhere(string? lastError)
    {
        var texts = _audit.Details.Append(lastError ?? string.Empty).ToList();
        texts.Should().AllSatisfy(text => text.Should().NotContain(Prefix));
        _log.Entries.Should().AllSatisfy(entry =>
        {
            entry.Message.Should().NotContain(Prefix);
            (entry.Exception?.ToString() ?? string.Empty).Should().NotContain(Prefix);
        });
    }

    /// <summary>
    /// Bis der Fehlversuch im Status, im Audit und im Log steht — erst dann ist belegt, dass jeder
    /// dieser Wege den Grund schon bekommen hat.
    /// </summary>
    private async Task FailedAttemptBookedAsync(UpstreamSupervisor supervisor, ServerId id)
    {
        await IntegrationSupport.WaitUntilAsync(
            () => supervisor.GetStatus(id)?.Reconnect is { NextAttemptAt: not null }
                && _audit.Details.Any(d => d.Contains(": Failed", StringComparison.Ordinal))
                && _log.Entries.Any(e => e.Message.Contains("Failed (", StringComparison.Ordinal)),
            because: "der Fehlversuch muss in Status, Audit und Log verbucht sein");
    }

    private UpstreamSupervisor CreateSupervisor()
        => _supervisor = new UpstreamSupervisor(
            [new StreamableHttpUpstreamConnector(tokens: _tokens)],
            new InMemoryUpstreamConfigStore(),
            new SupervisorOptions
            {
                HealthCheckInterval = TimeSpan.FromMinutes(5),
                DefaultCallTimeout = TimeSpan.FromSeconds(10),
                DefaultDrainGrace = TimeSpan.FromSeconds(1),
                DefaultRestartPolicy = new RestartPolicy(
                    MaxRetries: null, TimeSpan.FromSeconds(30), 1.5, TimeSpan.FromSeconds(60)),
                AuthenticationRetryInterval = TimeSpan.FromSeconds(60),
                ConnectAttemptTimeout = TimeSpan.FromSeconds(15),
                SessionRebuildTimeout = TimeSpan.FromSeconds(15),
            },
            logger: _log,
            audit: _audit,
            hostExecution: HostExecutionPolicy.AllowedByOperator());

    private async Task<ServerId> AddAsync(UpstreamSupervisor supervisor)
    {
        // Die Id vergibt der Supervisor; das Token muss vorher unter ihr liegen. Deshalb wird es beim
        // ersten Lesen nachgereicht (siehe Tokens).
        _tokens.Pending = _accessToken;
        return await supervisor.AddAsync(Config(), Ct);
    }

    private UpstreamServerConfig Config()
        => new(
            "zitiert", "Zitierender Upstream", UpstreamTransportKind.StreamableHttp, Enabled: true,
            Http: new HttpTransportOptions(
                _upstream.Endpoint,
                OAuth: new UpstreamOAuthOptions("gateway", AllowPrivateTargets: true),
                AllowPrivateTargets: true));

    private UpstreamOAuthToken Token(ServerId id)
        => new(
            id, _accessToken, "rt-" + Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddHours(1), ["mcp:read"], "http://127.0.0.1:1", DateTimeOffset.UtcNow);

    /// <summary>
    /// Token-Ablage ohne Datenbank. Liest der Supervisor für eine Id, unter der noch nichts liegt, und
    /// ist ein Token vorgemerkt, wird es für diese Id angelegt — die Id steht erst nach dem Hinzufügen
    /// fest, der erste Aufbau läuft aber schon.
    /// </summary>
    private sealed class Tokens : IUpstreamOAuthTokenStore
    {
        private readonly Dictionary<ServerId, UpstreamOAuthToken> _tokens = [];

        public string? Pending { get; set; }

        public Task<UpstreamOAuthToken?> GetAsync(ServerId server, CancellationToken ct)
        {
            lock (_tokens)
            {
                if (!_tokens.ContainsKey(server) && Pending is { } pending)
                {
                    _tokens[server] = new UpstreamOAuthToken(
                        server, pending, "rt-" + Guid.NewGuid().ToString("N"),
                        DateTimeOffset.UtcNow.AddHours(1), ["mcp:read"], "http://127.0.0.1:1", DateTimeOffset.UtcNow);
                }

                return Task.FromResult(_tokens.GetValueOrDefault(server));
            }
        }

        public Task SaveAsync(UpstreamOAuthToken token, CancellationToken ct)
        {
            lock (_tokens)
            {
                _tokens[token.Server] = token;
                return Task.CompletedTask;
            }
        }

        public Task RemoveAsync(ServerId server, CancellationToken ct)
        {
            lock (_tokens)
            {
                _tokens.Remove(server);
                return Task.CompletedTask;
            }
        }

        public Task<bool> ReplaceAsync(UpstreamOAuthToken expected, UpstreamOAuthToken replacement, CancellationToken ct)
        {
            lock (_tokens)
            {
                if (!_tokens.TryGetValue(expected.Server, out var current) || current.AccessToken != expected.AccessToken)
                {
                    return Task.FromResult(false);
                }

                _tokens[expected.Server] = replacement;
                return Task.FromResult(true);
            }
        }
    }

    private sealed class RecordingAudit : IAuditSink
    {
        private readonly ConcurrentQueue<AuditEvent> _events = new();

        public IReadOnlyList<string> Details
            => [.. _events.Where(e => e.Kind == AuditEventKind.ServerLifecycle).Select(e => e.Detail ?? string.Empty)];

        public void Record(AuditEvent evt) => _events.Enqueue(evt);
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<UpstreamSupervisor>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    /// <summary>
    /// Ein echter MCP-Server über Streamable HTTP mit Sitzungen, der auf Zuruf <c>initialize</c>
    /// ablehnt und dabei die vorgelegte <c>Authorization</c>-Kopfzeile zitiert — als JSON-RPC-Fehler
    /// mit HTTP 200 oder mit einem HTTP-Status.
    /// </summary>
    private sealed class QuotingUpstream : IAsyncDisposable
    {
        private static readonly JsonElement MessageSchema = JsonDocument.Parse(
            """{"type":"object","properties":{"message":{"type":"string"}}}""").RootElement.Clone();

        private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.Ordinal);
        private WebApplication? _app;
        private int _quotedInitializes;

        public QuotingUpstream() => Port = FreePort();

        public int Port { get; }

        public Uri Endpoint => new($"http://127.0.0.1:{Port}/mcp");

        private volatile bool _failInitialize;

        public bool FailInitialize
        {
            get => _failInitialize;
            set => _failInitialize = value;
        }

        /// <summary>200: JSON-RPC-Fehler; sonst dieser HTTP-Status mit zitierendem Körper.</summary>
        public int InitializeStatus { get; set; } = 200;

        public int QuotedInitializes => Volatile.Read(ref _quotedInitializes);

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = false)
                .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = [new Tool { Name = "echo", Description = "Echoes.", InputSchema = MessageSchema }],
                }))
                .WithCallToolHandler((_, _) => ValueTask.FromResult(
                    new CallToolResult { Content = [new TextContentBlock { Text = "Echo" }] }));

            var app = builder.Build();
            app.Urls.Add($"http://127.0.0.1:{Port}");
            app.Use(InterceptAsync);
            app.MapMcp("/mcp");
            await app.StartAsync();
            _app = app;
        }

        public void RevokeSessions()
        {
            foreach (var session in _sessions.Keys)
            {
                _revoked[session] = 0;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is { } app)
            {
                _app = null;
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await app.StopAsync(grace.Token);
                await app.DisposeAsync();
            }
        }

        private async Task InterceptAsync(HttpContext context, RequestDelegate next)
        {
            if (HttpMethods.IsPost(context.Request.Method))
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;

                if (FailInitialize && body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _quotedInitializes);
                    var quoted = $"Zugriff verweigert fuer {context.Request.Headers.Authorization}";
                    context.Response.ContentType = "application/json";
                    if (InitializeStatus == 200)
                    {
                        var id = JsonNode.Parse(body)?["id"]?.DeepClone();
                        var error = new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = id,
                            ["error"] = new JsonObject { ["code"] = -32600, ["message"] = quoted },
                        };
                        await context.Response.WriteAsync(error.ToJsonString());
                    }
                    else
                    {
                        context.Response.StatusCode = InitializeStatus;
                        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = quoted }));
                    }

                    return;
                }
            }

            var session = context.Request.Headers["Mcp-Session-Id"].ToString();
            if (session.Length > 0 && _revoked.ContainsKey(session))
            {
                // So antwortet das MCP-SDK für ASP.NET Core auf eine unbekannte Sitzung.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"jsonrpc":"2.0","error":{"code":-32001,"message":"Session not found"},"id":null}""");
                return;
            }

            context.Response.OnStarting(() =>
            {
                if (context.Response.Headers.TryGetValue("Mcp-Session-Id", out var issued) && issued.Count > 0)
                {
                    _sessions[issued.ToString()] = 0;
                }

                return Task.CompletedTask;
            });

            await next(context);
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
