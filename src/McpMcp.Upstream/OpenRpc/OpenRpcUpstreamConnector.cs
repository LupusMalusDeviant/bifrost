using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpMcp.Abstractions;

namespace McpMcp.Upstream.OpenRpc;

/// <summary>
/// OpenRPC/JSON-RPC als Upstream (Roadmap Phase 8).
/// <para>
/// Die Beschreibung kommt aus einem statischen Dokument oder über <c>rpc.discover</c>. Beide Wege
/// laufen durch dieselbe Prüfung von Ziel, Größe und Schema — der Discovery-Weg bekommt keinen
/// Vertrauensvorschuss, nur weil er standardisiert ist.
/// </para>
/// </summary>
public sealed class OpenRpcUpstreamConnector : IUpstreamConnector
{
    public UpstreamTransportKind Kind => UpstreamTransportKind.OpenRpc;

    public async Task<IUpstreamConnection> ConnectAsync(
        ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.OpenRpc
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine OpenRpc-Optionen.", nameof(config));

        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        // Auch der Aufruf-Endpunkt wird geprüft, nicht nur die Dokumentquelle: Sonst wäre die
        // Zielprüfung eine Formalie, die man durch ein lokales Dokument mit interner Endpunkt-URL
        // umgeht.
        await SpecFetcher.EnsureTargetAllowedAsync(options.Endpoint, options.AllowPrivateTargets, ct)
            .ConfigureAwait(false);

        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        ApplyAuth(http, options);

        try
        {
            var document = options.SpecLocation is { } location
                ? await SpecFetcher
                    .FetchAsync(location, options.AllowPrivateTargets, timeout, ct)
                    .ConfigureAwait(false)
                : await DiscoverAsync(http, options, timeout, ct).ConfigureAwait(false);

            var methods = OpenRpcDocumentParser.Parse(document);
            return new OpenRpcUpstreamConnection(id, methods, http, options);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Holt die Beschreibung über <c>rpc.discover</c>. Die Antwort wird wie ein fremdes Dokument
    /// behandelt — inklusive Größengrenze.
    /// </summary>
    private static async Task<string> DiscoverAsync(
        HttpClient http, OpenRpcTransportOptions options, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var response = await OpenRpcUpstreamConnection
            .SendAsync(http, options.Endpoint, "rpc.discover", null, cts.Token)
            .ConfigureAwait(false);

        if (response.Error is { } error)
        {
            throw new OpenRpcImportException(
                $"'rpc.discover' scheiterte ({error.Code}): {error.Message}. "
                + "Ohne Discovery braucht der Upstream ein statisches Dokument über SpecLocation.");
        }

        var raw = response.Result?.GetRawText()
            ?? throw new OpenRpcImportException("'rpc.discover' lieferte kein Ergebnis.");
        if (raw.Length > SpecFetcher.MaxBytes)
        {
            throw new OpenRpcImportException(
                $"Antwort auf 'rpc.discover' überschreitet {SpecFetcher.MaxBytes / (1024 * 1024)} MB.");
        }

        return raw;
    }

    private static void ApplyAuth(HttpClient http, OpenRpcTransportOptions options)
    {
        switch (options.AuthKind)
        {
            case OpenApiAuthKind.None:
                break;
            case OpenApiAuthKind.Bearer:
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", Require(options));
                break;
            case OpenApiAuthKind.Basic:
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Require(options))));
                break;
            case OpenApiAuthKind.ApiKeyHeader:
                http.DefaultRequestHeaders.Add(
                    options.ApiKeyHeaderName ?? "X-Api-Key", Require(options));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static string Require(OpenRpcTransportOptions options)
        => options.Credential is { Length: > 0 } credential
            ? credential
            : throw new ArgumentException("Auth-Art gesetzt, aber kein Credential hinterlegt.", nameof(options));
}

/// <summary>Eine JSON-RPC-Antwort, zerlegt in Ergebnis und Fehler.</summary>
internal sealed record JsonRpcResponse(JsonElement? Result, JsonRpcError? Error);

internal sealed record JsonRpcError(int Code, string Message, string? Data);

internal sealed class OpenRpcUpstreamConnection : IUpstreamConnection
{
    /// <summary>Obergrenze für das <c>data</c>-Feld eines Fehlers — es kommt vom Upstream.</summary>
    private const int MaxErrorDataChars = 2_000;

    private readonly IReadOnlyList<OpenRpcMethod> _methods;
    private readonly HttpClient _http;
    private readonly OpenRpcTransportOptions _options;
    private bool _disposed;

    public OpenRpcUpstreamConnection(
        ServerId id, IReadOnlyList<OpenRpcMethod> methods, HttpClient http,
        OpenRpcTransportOptions options)
    {
        Id = id;
        _methods = methods;
        _http = http;
        _options = options;
    }

    public ServerId Id { get; }

    // JSON-RPC kennt server-initiierte Notifications, aber der Gateway abonniert nichts: Ein
    // HTTP-Aufruf hat genau eine Antwort. Das Event bleibt bewusst leer verdrahtet.
    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add { }
        remove { }
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(new UpstreamInventory(
            [.. _methods.Select(OpenRpcDocumentParser.ToToolDescriptor)], [], []));

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        if (_methods.FirstOrDefault(m => m.Name == toolName) is not { } method)
        {
            return Result($"OpenRPC-Upstream kennt keine Methode '{toolName}'.", isError: true);
        }

        // by-name bleibt ein Objekt, by-position wird ein Array in der Reihenfolge der Descriptors.
        // Die Reihenfolge ist der Vertrag — sie aus einem Objekt zu raten ginge schief, sobald ein
        // Aufrufer die Felder anders sortiert.
        JsonNode? parameters;
        if (method.ParamStructure is ParamStructure.ByPosition)
        {
            var positional = new JsonArray();
            foreach (var name in method.ParameterOrder)
            {
                positional.Add(args.ValueKind is JsonValueKind.Object
                    && args.TryGetProperty(name, out var value)
                        ? JsonNode.Parse(value.GetRawText())
                        : null);
            }

            parameters = positional;
        }
        else
        {
            parameters = args.ValueKind is JsonValueKind.Object
                ? JsonNode.Parse(args.GetRawText())
                : new JsonObject();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        JsonRpcResponse response;
        try
        {
            response = await SendAsync(_http, _options.Endpoint, method.Name, parameters, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result($"Zeitüberschreitung nach {_options.TimeoutSeconds} s.", isError: true);
        }
        catch (HttpRequestException exception)
        {
            return Result($"Upstream nicht erreichbar: {exception.Message}", isError: true);
        }

        if (response.Error is { } error)
        {
            // Der JSON-RPC-Fehler bleibt strukturiert: Code und Meldung getrennt, `data` begrenzt.
            var detail = error.Data is { Length: > 0 } data
                ? $" ({data[..Math.Min(data.Length, MaxErrorDataChars)]})"
                : string.Empty;
            return Result($"JSON-RPC-Fehler {error.Code}: {error.Message}{detail}", isError: true);
        }

        return Result(
            response.Result is { } result ? result.GetRawText() : string.Empty, isError: false);
    }

    /// <summary>
    /// Schickt einen Aufruf. Die Request-Id wird <b>hier</b> erzeugt und mit der Antwort abgeglichen
    /// — eine Antwort mit fremder Id gehört nicht zu diesem Aufruf und wird nicht als Ergebnis
    /// ausgegeben.
    /// </summary>
    internal static async Task<JsonRpcResponse> SendAsync(
        HttpClient http, Uri endpoint, string method, JsonNode? parameters, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        using var content = JsonContent.Create(request);
        using var httpResponse = await http.PostAsync(endpoint, content, ct).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();

        var body = await httpResponse.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        if (body.ValueKind is JsonValueKind.Array)
        {
            // Batch ist für v1 ausgenommen: Jeder Aufruf darin müsste einzeln durch RBAC,
            // Guardrail, Approval und Audit — sonst entstünde ein Weg, an der Governance vorbei
            // mehrere Dinge zu tun.
            throw new OpenRpcImportException(
                "Batch-Antworten werden nicht unterstützt (v1). Ein Batch umginge die Governance je Aufruf.");
        }

        if (body.TryGetProperty("id", out var responseId)
            && responseId.ValueKind is JsonValueKind.String
            && responseId.GetString() != id)
        {
            throw new OpenRpcImportException(
                $"Antwort trägt die Id '{responseId.GetString()}' statt '{id}' — sie gehört nicht zu diesem Aufruf.");
        }

        if (body.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object)
        {
            return new JsonRpcResponse(null, new JsonRpcError(
                error.TryGetProperty("code", out var code) ? code.GetInt32() : 0,
                error.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                error.TryGetProperty("data", out var data) ? data.GetRawText() : null));
        }

        return new JsonRpcResponse(
            body.TryGetProperty("result", out var result) ? result.Clone() : null, null);
    }

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => throw new NotSupportedException("OpenRPC-Upstreams haben keine Resources.");

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => throw new NotSupportedException("OpenRPC-Upstreams haben keine Prompts.");

    /// <summary>
    /// Erreichbarkeit über <c>rpc.discover</c>. Antwortet der Dienst mit einem JSON-RPC-Fehler, ist
    /// er erreichbar — nur ohne Discovery; das ist kein Gesundheitsproblem.
    /// </summary>
    public async Task PingAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        await SendAsync(_http, _options.Endpoint, "rpc.discover", null, cts.Token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static JsonElement Result(string text, bool isError)
        => JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            isError,
        });
}
