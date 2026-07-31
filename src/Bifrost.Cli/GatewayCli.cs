using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Bifrost.Cli;

public sealed class GatewayCli
{
    public const int Success = 0;
    public const int UsageError = 2;
    public const int AuthorizationError = 3;
    public const int NotFound = 4;
    public const int GatewayError = 5;
    public const int ApprovalRequired = 6;
    public const int TransportError = 10;

    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly HttpClient _client;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly bool _jsonOutput;

    public GatewayCli(
        HttpClient client,
        TextReader input,
        TextWriter output,
        TextWriter error,
        bool jsonOutput)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _input = input;
        _output = output;
        _error = error;
        _jsonOutput = jsonOutput;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var command = args.ToArray();
            return command switch
            {
                ["status"] => await StatusAsync(ct),
                ["tools", "search", .. var tail] when tail.Length > 0
                    => await SearchToolsAsync(string.Join(' ', tail), ct),
                ["tools", "describe", var tool] => await DescribeToolAsync(tool, ct),
                ["tools", "invoke", var tool, .. var options]
                    => await InvokeToolAsync(tool, options, ct),
                ["servers", "list"] => await GetAsync("api/v1/servers", "servers", ct),
                ["servers", "add", "--file", var file]
                    => await SendFileAsync(HttpMethod.Post, "api/v1/servers", file, ct),
                ["servers", "enable", var id]
                    => await SetServerEnabledAsync(id, enabled: true, ct),
                ["servers", "disable", var id]
                    => await SetServerEnabledAsync(id, enabled: false, ct),
                ["servers", "remove", var id]
                    => await SendAsync(HttpMethod.Delete, $"api/v1/servers/{Escape(id)}", null, ct),
                ["approvals", "list"] => await GetAsync("api/v1/approvals", null, ct),
                ["approvals", "approve", var id]
                    => await DecideApprovalAsync(id, approved: true, ct),
                ["approvals", "deny", var id]
                    => await DecideApprovalAsync(id, approved: false, ct),
                ["audit", "tail"] => await GetAsync("api/v1/audit?pageSize=100", "items", ct),
                _ => await UsageAsync(),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _error.WriteLineAsync("Abgebrochen.");
            return TransportError;
        }
        catch (HttpRequestException ex)
        {
            await _error.WriteLineAsync($"Gateway nicht erreichbar: {ex.Message}");
            return TransportError;
        }
        catch (IOException ex)
        {
            await _error.WriteLineAsync($"Ein-/Ausgabefehler: {ex.Message}");
            return TransportError;
        }
        catch (JsonException ex)
        {
            await _error.WriteLineAsync($"Ungültiges JSON: {ex.Message}");
            return UsageError;
        }
        catch (ArgumentException ex)
        {
            await _error.WriteLineAsync(ex.Message);
            return UsageError;
        }
    }

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        using var healthResponse = await _client.GetAsync("healthz", ct);
        var health = await ReadJsonAsync(healthResponse, ct);
        using var readinessResponse = await _client.GetAsync("readyz", ct);
        var readiness = await ReadJsonAsync(readinessResponse, ct);
        var combined = JsonSerializer.SerializeToElement(new { health, readiness });
        if (!healthResponse.IsSuccessStatusCode || !readinessResponse.IsSuccessStatusCode)
        {
            await WriteAsync(combined, "Gateway ist nicht bereit.");
            return GatewayError;
        }

        await WriteAsync(combined, "Gateway ist bereit.");
        return Success;
    }

    private async Task<int> SearchToolsAsync(string query, CancellationToken ct)
    {
        using var response = await _client.GetAsync("api/v1/tools", ct);
        var body = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            return await WriteErrorAsync(response.StatusCode, body);
        }

        var matches = body.GetProperty("tools").EnumerateArray()
            .Where(tool =>
                tool.GetProperty("name").GetString()!.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tool.GetProperty("description").GetString()!
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(tool => tool.Clone())
            .ToArray();
        var result = JsonSerializer.SerializeToElement(new { tools = matches });
        await WriteAsync(
            result,
            matches.Length == 0
                ? "Keine Tools gefunden."
                : string.Join(Environment.NewLine, matches.Select(ToolSummary)));
        return Success;
    }

    private async Task<int> DescribeToolAsync(string name, CancellationToken ct)
    {
        using var response = await _client.GetAsync("api/v1/tools", ct);
        var body = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            return await WriteErrorAsync(response.StatusCode, body);
        }

        var match = body.GetProperty("tools").EnumerateArray()
            .FirstOrDefault(tool => tool.GetProperty("name").GetString() == name);
        if (match.ValueKind == JsonValueKind.Undefined)
        {
            await _error.WriteLineAsync($"Tool '{name}' wurde nicht gefunden.");
            return NotFound;
        }

        await WriteAsync(match, JsonSerializer.Serialize(match, PrettyJson));
        return Success;
    }

    private async Task<int> InvokeToolAsync(
        string tool, IReadOnlyList<string> options, CancellationToken ct)
    {
        string json;
        if (options is ["--json", var inlineJson])
        {
            json = inlineJson;
        }
        else if (options is ["--file", var file])
        {
            json = file == "-"
                ? await _input.ReadToEndAsync(ct)
                : await File.ReadAllTextAsync(Path.GetFullPath(file), ct);
        }
        else
        {
            throw new ArgumentException(
                "tools invoke verlangt --json '<objekt>' oder --file <pfad|->.");
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool-Argumente müssen ein JSON-Objekt sein.");
        }

        return await SendAsync(
            HttpMethod.Post,
            $"api/v1/tools/{Escape(tool)}/invoke",
            new StringContent(json, Encoding.UTF8, "application/json"),
            ct);
    }

    private Task<int> SetServerEnabledAsync(string id, bool enabled, CancellationToken ct)
        => SendAsync(
            HttpMethod.Post,
            $"api/v1/servers/{Escape(id)}/enabled",
            JsonContent(new { enabled }),
            ct);

    private Task<int> DecideApprovalAsync(string id, bool approved, CancellationToken ct)
        => SendAsync(
            HttpMethod.Post,
            $"api/v1/approvals/{Escape(id)}/decide?approved={approved.ToString().ToLowerInvariant()}",
            JsonContent(new { }),
            ct);

    private async Task<int> SendFileAsync(
        HttpMethod method, string path, string file, CancellationToken ct)
    {
        var json = file == "-"
            ? await _input.ReadToEndAsync(ct)
            : await File.ReadAllTextAsync(Path.GetFullPath(file), ct);
        using var document = JsonDocument.Parse(json);
        return await SendAsync(
            method, path, new StringContent(json, Encoding.UTF8, "application/json"), ct);
    }

    private async Task<int> GetAsync(
        string path, string? humanArrayProperty, CancellationToken ct)
    {
        using var response = await _client.GetAsync(path, ct);
        var body = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            return await WriteErrorAsync(response.StatusCode, body);
        }

        var human = humanArrayProperty is not null
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty(humanArrayProperty, out var array)
            && array.ValueKind == JsonValueKind.Array
                ? string.Join(Environment.NewLine, array.EnumerateArray()
                    .Select(item => JsonSerializer.Serialize(item)))
                : JsonSerializer.Serialize(body, PrettyJson);
        await WriteAsync(body, human);
        return Success;
    }

    private async Task<int> SendAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await _client.SendAsync(request, ct);
        var body = await ReadJsonAsync(response, ct);
        if (!response.IsSuccessStatusCode)
        {
            return await WriteErrorAsync(response.StatusCode, body);
        }

        await WriteAsync(
            body,
            body.ValueKind == JsonValueKind.Undefined
                ? "OK"
                : JsonSerializer.Serialize(body, PrettyJson));
        return Success;
    }

    private async Task<int> WriteErrorAsync(HttpStatusCode status, JsonElement body)
    {
        var text = body.ValueKind == JsonValueKind.Undefined
            ? $"Gateway-Fehler {(int)status} ({status})."
            : JsonSerializer.Serialize(body, _jsonOutput ? null : PrettyJson);
        await _error.WriteLineAsync(text);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AuthorizationError,
            HttpStatusCode.NotFound => NotFound,
            HttpStatusCode.Conflict => ApprovalRequired,
            _ => GatewayError,
        };
    }

    private async Task WriteAsync(JsonElement json, string human)
    {
        await _output.WriteLineAsync(
            _jsonOutput ? JsonSerializer.Serialize(json) : human);
    }

    private async Task<int> UsageAsync()
    {
        await _error.WriteLineAsync(
            """
            Nutzung:
              bifrost [--json] [--config PATH] [--token-stdin] status
              bifrost [Optionen] tools search <query>
              bifrost [Optionen] tools describe <tool>
              bifrost [Optionen] tools invoke <tool> --json '{...}'
              bifrost [Optionen] tools invoke <tool> --file <args.json|->
              bifrost [Optionen] servers list|add|enable|disable|remove
              bifrost [Optionen] approvals list|approve|deny
              bifrost [Optionen] audit tail

            Authentifizierung: BIFROST_TOKEN, Token-Datei in --config oder --token-stdin.
            """);
        return UsageError;
    }

    private static StringContent JsonContent<T>(T value)
        => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string ToolSummary(JsonElement tool)
        => $"{tool.GetProperty("name").GetString()} — {tool.GetProperty("description").GetString()}";

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength == 0
            || response.StatusCode == HttpStatusCode.NoContent)
        {
            return default;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
    }
}
