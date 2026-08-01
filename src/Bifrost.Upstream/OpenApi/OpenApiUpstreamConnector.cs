using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Upstream.Http;

namespace Bifrost.Upstream.OpenApi;

/// <summary>
/// API→MCP-Brücke (FR-19, ADR-0008): eine per OpenAPI-Spec beschriebene REST-API erscheint
/// als normaler Upstream — hot-swappable, profilierbar und auditiert wie jeder MCP-Server.
/// <para>
/// Spec-Quelle und Ziel-API laufen durch dieselbe Zielprüfung wie beim OpenRPC-Konnektor
/// (<see cref="RemoteSpecFetcher"/>). Vorgabe ist fail-closed: Ein Ziel im internen Netz verlangt
/// <see cref="OpenApiTransportOptions.AllowPrivateTargets"/>.
/// </para>
/// </summary>
public sealed class OpenApiUpstreamConnector : IUpstreamConnector
{
    private static readonly TimeSpan SpecTimeout = TimeSpan.FromSeconds(30);

    public UpstreamTransportKind Kind => UpstreamTransportKind.OpenApi;

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.OpenApi
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine OpenApi-Optionen.", nameof(config));

        // Größenbegrenzung UND Zielprüfung (Security-Audit WP7.2, nachgezogen mit Phase 8):
        // Eine vom Betreiber genannte URL abzurufen, ohne das Ziel zu prüfen, macht das Gateway zum
        // Werkzeug, interne Dienste zu erreichen. Weiterleitungen prüft der Fetcher einzeln.
        var specJson = await RemoteSpecFetcher.FetchAsync(
                options.SpecLocation, options.AllowPrivateTargets, SpecTimeout, Fail, ct)
            .ConfigureAwait(false);
        var (operations, serverUrl) = OpenApiSpecParser.Parse(specJson);

        var baseAddress = options.BaseAddress
            ?? serverUrl
            ?? throw new OpenApiImportException(
                "Weder BaseAddress konfiguriert noch eine absolute Server-URL in der Spec — Ziel-API unbekannt.");

        // Auch die Ziel-API wird geprüft, nicht nur die Spec-Quelle. Sonst genügte eine Spec von
        // einer harmlosen Adresse — oder aus einer lokalen Datei —, deren `servers`-Eintrag nach
        // innen zeigt, und die Prüfung an der Quelle wäre eine Formalie.
        await RemoteSpecFetcher
            .EnsureTargetAllowedAsync(baseAddress, options.AllowPrivateTargets, Fail, ct)
            .ConfigureAwait(false);

        // Weiterleitungen im Aufrufpfad werden nicht verfolgt: Sonst führte ein 302 der Ziel-API an
        // der eben geprüften Adresse vorbei — auf 127.0.0.1 oder den Metadatendienst. Ein 3xx
        // kommt stattdessen als Fehler beim Aufrufer an.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        try
        {
            ApplyAuth(http, options);
        }
        catch
        {
            http.Dispose();
            throw;
        }

        return new OpenApiUpstreamConnection(id, operations, http);
    }

    private static Exception Fail(string message) => new OpenApiImportException(message);

    private static void ApplyAuth(HttpClient http, OpenApiTransportOptions options)
    {
        switch (options.AuthKind)
        {
            case OpenApiAuthKind.None:
                break;
            case OpenApiAuthKind.Bearer:
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer", RequireCredential(options));
                break;
            case OpenApiAuthKind.Basic:
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(RequireCredential(options))));
                break;
            case OpenApiAuthKind.ApiKeyHeader:
                http.DefaultRequestHeaders.Add(
                    options.ApiKeyHeaderName ?? "X-Api-Key", RequireCredential(options));
                break;
            default:
                throw new OpenApiImportException($"AuthKind {options.AuthKind} wird nicht unterstützt.");
        }
    }

    private static string RequireCredential(OpenApiTransportOptions options)
        => options.Credential
            ?? throw new OpenApiImportException($"AuthKind {options.AuthKind} verlangt ein Credential.");
}

internal sealed class OpenApiUpstreamConnection : IUpstreamConnection
{
    private readonly Dictionary<string, OpenApiOperationSpec> _operations;
    private readonly HttpClient _http;

    public OpenApiUpstreamConnection(ServerId id, IReadOnlyList<OpenApiOperationSpec> operations, HttpClient http)
    {
        Id = id;
        _operations = operations.ToDictionary(o => o.OperationId, StringComparer.Ordinal);
        _http = http;
    }

    public ServerId Id { get; }

    /// <summary>
    /// Ausdrücklich „nicht zutreffend", nicht „unbekannt": Hier wird HTTP gegen eine REST-API
    /// gesprochen, kein MCP. Es gibt keine Fassung, die jemand ermitteln könnte — und wer das
    /// wüsste, hörte auf zu suchen.
    /// </summary>
    public UpstreamProtocolInfo Protocol { get; } = UpstreamProtocolInfo.NotApplicable(
        "Ein OpenAPI-Upstream spricht kein MCP: Der Gateway ruft die beschriebene REST-API direkt "
        + "auf. Es wird keine Protokollfassung ausgehandelt.");

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add { }
        remove { }
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(new UpstreamInventory(
            [.. _operations.Values.Select(o => new ToolDescriptor(o.OperationId, o.Description, o.InputSchema))],
            [],
            []));

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        if (!_operations.TryGetValue(toolName, out var operation))
        {
            throw new InvalidOperationException($"Operation '{toolName}' existiert nicht in der importierten Spec.");
        }

        using var request = BuildRequest(operation, args);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        // Eine Weiterleitung wird bewusst nicht verfolgt (siehe Konnektor): Sie zeigt auf eine
        // Adresse, die nie geprüft wurde. Der Aufrufer bekommt den Grund genannt statt eines leeren
        // Rumpfs mit Statuscode.
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            return Result(
                $"Die API antwortet mit einer Weiterleitung auf '{response.Headers.Location}'. "
                + "Weiterleitungen werden nicht verfolgt — das Ziel dahinter ist ungeprüft. "
                + "Wenn es beabsichtigt ist, gehört die Zieladresse als BaseAddress in die Konfiguration.",
                isError: true);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Result(
            body.Length > 0 ? body : $"HTTP {(int)response.StatusCode}",
            isError: !response.IsSuccessStatusCode);
    }

    /// <summary>CallToolResult-Form wie bei echten MCP-Upstreams — der Rest des Gateways bleibt uniform.</summary>
    private static JsonElement Result(string text, bool isError)
        => JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            isError,
        });

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => throw new NotSupportedException("OpenAPI-Upstreams haben keine Resources.");

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => throw new NotSupportedException("OpenAPI-Upstreams haben keine Prompts.");

    public async Task PingAsync(CancellationToken ct)
    {
        // Erreichbarkeit genügt — der Statuscode ist egal (viele APIs haben keinen Health-Pfad).
        using var request = new HttpRequestMessage(HttpMethod.Head, _http.BaseAddress);
        using var _ = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static HttpRequestMessage BuildRequest(OpenApiOperationSpec operation, JsonElement args)
    {
        string? GetArg(string name)
            => args.ValueKind is JsonValueKind.Object && args.TryGetProperty(name, out var value)
                ? value.ValueKind is JsonValueKind.String ? value.GetString() : value.GetRawText()
                : null;

        var path = operation.PathTemplate;
        var query = new List<string>();
        var request = new HttpRequestMessage(HttpMethod.Parse(operation.HttpMethod), (Uri?)null);

        foreach (var parameter in operation.Parameters)
        {
            var value = GetArg(parameter.Name);
            if (value is null)
            {
                if (parameter.Required)
                {
                    throw new InvalidOperationException(
                        $"Pflicht-Parameter '{parameter.Name}' fehlt für Operation '{operation.OperationId}'.");
                }

                continue;
            }

            switch (parameter.Location)
            {
                case OpenApiParameterLocation.Path:
                    path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
                    break;
                case OpenApiParameterLocation.Query:
                    query.Add($"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(value)}");
                    break;
                case OpenApiParameterLocation.Header:
                    // CR/LF aus Aufrufer-Argumenten würde Header-Injection erlauben (Security-Audit WP7.2).
                    if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Header-Parameter '{parameter.Name}' enthält unzulässige Zeilenumbrüche.");
                    }

                    request.Headers.TryAddWithoutValidation(parameter.Name, value);
                    break;
            }
        }

        if (operation.HasBody
            && args.ValueKind is JsonValueKind.Object
            && args.TryGetProperty("body", out var bodyElement))
        {
            request.Content = new StringContent(bodyElement.GetRawText(), Encoding.UTF8, "application/json");
        }

        var uri = path + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
        request.RequestUri = new Uri(uri.TrimStart('/'), UriKind.Relative);
        return request;
    }
}
