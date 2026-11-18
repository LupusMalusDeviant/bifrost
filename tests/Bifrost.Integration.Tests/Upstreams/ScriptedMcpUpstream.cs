using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Bifrost.Integration.Tests.Upstreams;

/// <summary>
/// Ein echter MCP-Server über Streamable HTTP <b>mit Sitzungen</b>, der sich von außen steuern
/// lässt: anhalten und auf demselben Port neu starten, jede Anfrage ablehnen, bestehende Sitzungen
/// für ungültig erklären, einen laufenden Aufruf abreißen.
/// <para>
/// Er läuft im Testprozess und nicht als eigener Prozess: Die Tests müssen zählen, was beim
/// Upstream <i>ankommt</i> (Anfragen, <c>initialize</c>, Ausführungen eines Werkzeugs) — nur so lässt
/// sich belegen, dass etwas <b>nicht</b> passiert ist, etwa ein zweiter Aufruf.
/// </para>
/// </summary>
internal sealed class ScriptedMcpUpstream : IAsyncDisposable
{
    private static readonly JsonElement MessageSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"message":{"type":"string"}}}""").RootElement.Clone();

    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.Ordinal);
    private WebApplication? _app;
    private HttpContext? _callInFlight;
    private TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requests;
    private int _initializes;
    private int _executions;
    private volatile int _rejectWith;
    private volatile string _echoDescription = "Echoes the message back.";

    public ScriptedMcpUpstream() => Port = FreePort();

    public int Port { get; }

    public Uri Endpoint => new($"http://127.0.0.1:{Port}/mcp");

    /// <summary>Alle eingegangenen HTTP-Anfragen, auch die abgelehnten.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Wie oft ein Client eine Sitzung begonnen hat.</summary>
    public int Initializes => Volatile.Read(ref _initializes);

    private int _toolCallPosts;
    private int _resumes;

    /// <summary>
    /// Wie oft ein <c>tools/call</c> beim Upstream <b>ankam</b> — gezählt vor jeder Sitzungsprüfung.
    /// Eine wiederholte Anfrage zählt hier auch dann, wenn der Upstream sie abweist.
    /// </summary>
    public int ToolCallPosts => Volatile.Read(ref _toolCallPosts);

    /// <summary>Wie oft ein Client einen Antwortstrom mit <c>Last-Event-ID</c> wieder aufnehmen wollte.</summary>
    public int Resumes => Volatile.Read(ref _resumes);

    /// <summary>
    /// Mit Ereignisspeicher: Der Server vergibt Ereignis-IDs, ein Client kann einen abgerissenen
    /// Antwortstrom wieder aufnehmen. Vor <see cref="StartAsync"/> setzen.
    /// </summary>
    public bool Resumable { get; init; }

    /// <summary>Wie oft ein Werkzeug wirklich <b>ausgeführt</b> wurde.</summary>
    public int Executions => Volatile.Read(ref _executions);

    /// <summary>0 = normal; sonst antwortet der Server auf jede Anfrage mit diesem Status.</summary>
    public int RejectWith
    {
        get => _rejectWith;
        set => _rejectWith = value;
    }

    /// <summary>Die Beschreibung, mit der <c>echo</c> gemeldet wird — umschaltbar zwischen zwei Sitzungen.</summary>
    public string EchoDescription
    {
        get => _echoDescription;
        set => _echoDescription = value;
    }

    /// <summary>Erfüllt, sobald ein Aufruf von <c>stall</c> beim Werkzeug angekommen ist.</summary>
    public Task Stalled => _stalled.Task;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var mcp = builder.Services.AddMcpServer();
        if (Resumable)
        {
            builder.Services.AddDistributedMemoryCache();
            // Die Wiederaufnahme gibt es nur im zustandsbehafteten Modus, den das SDK als Altlast
            // führt — genau den sprechen Upstreams mit Sitzungen, und um die geht es hier.
#pragma warning disable MCP9006
            mcp.WithDistributedCacheEventStreamStore();
#pragma warning restore MCP9006
        }

        mcp
            .WithHttpTransport(options => options.Stateless = false)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool { Name = "echo", Description = _echoDescription, InputSchema = MessageSchema },
                    new Tool { Name = "stall", Description = "Never answers.", InputSchema = MessageSchema },
                ],
            }))
            .WithCallToolHandler(async (context, ct) =>
            {
                Interlocked.Increment(ref _executions);
                if (context.Params?.Name == "stall")
                {
                    _stalled.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return new CallToolResult { Content = [new TextContentBlock { Text = "Echo" }] };
            });

        var app = builder.Build();
        app.Urls.Add($"http://127.0.0.1:{Port}");
        app.Use(InterceptAsync);
        app.MapMcp("/mcp");
        await app.StartAsync();
        _app = app;
    }

    /// <summary>Der Upstream ist weg: Der Port ist danach geschlossen.</summary>
    public async Task StopAsync()
    {
        if (_app is { } app)
        {
            _app = null;
            // Mit Frist: Ein Client hält bei Sitzungen einen offenen Antwortstrom, und ohne Frist
            // wartete das Anhalten auf dessen Ende.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(grace.Token);
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// Erklärt jede bisher ausgegebene Sitzung für ungültig: Anfragen mit einer dieser
    /// <c>Mcp-Session-Id</c> bekommen ab jetzt HTTP 404 — so, wie ein neu gestarteter Upstream
    /// antwortet. Neue Sitzungen bleiben möglich.
    /// </summary>
    public void RevokeSessions()
    {
        foreach (var session in _sessions.Keys)
        {
            _revoked[session] = 0;
        }
    }

    /// <summary>Reißt die Verbindung des gerade laufenden Werkzeugaufrufs ab — ohne Antwort.</summary>
    public void AbortCallInFlight()
        => (Volatile.Read(ref _callInFlight)
            ?? throw new InvalidOperationException("Es läuft kein Werkzeugaufruf.")).Abort();

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task InterceptAsync(HttpContext context, RequestDelegate next)
    {
        Interlocked.Increment(ref _requests);

        if (_rejectWith is var status and not 0)
        {
            // Wie ein Upstream, der in seiner Absage zitiert, was ihm vorgelegt wurde — genau der
            // Fall, in dem der Antwortkörper nicht in eine Meldung des Gateways gehört.
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { error = $"rejected credential {context.Request.Headers.Authorization}" }));
            return;
        }

        // Erst zählen, dann die Sitzung prüfen: Auch eine Anfrage auf eine verworfene Sitzung ist
        // angekommen — sonst übersähe der Test genau den wiederholten Aufruf, den er sucht (D13).
        if (HttpMethods.IsPost(context.Request.Method))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            context.Request.Body.Position = 0;

            if (body.Contains("\"method\":\"initialize\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _initializes);
            }

            if (body.Contains("\"method\":\"tools/call\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _toolCallPosts);
                Volatile.Write(ref _callInFlight, context);
            }
        }
        else if (HttpMethods.IsGet(context.Request.Method) && context.Request.Headers.ContainsKey("Last-Event-ID"))
        {
            Interlocked.Increment(ref _resumes);
        }

        var session = context.Request.Headers["Mcp-Session-Id"].ToString();
        if (session.Length > 0 && _revoked.ContainsKey(session))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
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
