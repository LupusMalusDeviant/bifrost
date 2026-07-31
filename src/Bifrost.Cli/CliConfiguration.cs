using System.Text.Json;

namespace Bifrost.Cli;

public sealed record CliConfiguration(
    Uri Endpoint,
    string? TokenFile = null,
    string? Identity = null)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static async Task<(CliConfiguration Configuration, string? Token)> LoadAsync(
        string? configPath,
        bool tokenFromStdin,
        TextReader input,
        CancellationToken ct)
    {
        configPath ??= Environment.GetEnvironmentVariable("BIFROST_CONFIG");
        CliConfiguration? fileConfiguration = null;
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            await using var stream = File.OpenRead(Path.GetFullPath(configPath));
            fileConfiguration = await JsonSerializer.DeserializeAsync<CliConfiguration>(
                stream,
                WebJson,
                ct);
        }

        var endpointText = Environment.GetEnvironmentVariable("BIFROST_ENDPOINT");
        var endpoint = endpointText is { Length: > 0 }
            ? new Uri(endpointText, UriKind.Absolute)
            : fileConfiguration?.Endpoint ?? new Uri("http://localhost:8080");
        var identity = Environment.GetEnvironmentVariable("BIFROST_IDENTITY")
            ?? fileConfiguration?.Identity;
        var configuration = new CliConfiguration(
            EnsureTrailingSlash(endpoint),
            fileConfiguration?.TokenFile,
            identity);

        string? token;
        if (tokenFromStdin)
        {
            token = (await input.ReadLineAsync(ct))?.Trim();
        }
        else
        {
            token = Environment.GetEnvironmentVariable("BIFROST_TOKEN");
            if (string.IsNullOrWhiteSpace(token) && configuration.TokenFile is { Length: > 0 } tokenFile)
            {
                token = (await File.ReadAllTextAsync(Path.GetFullPath(tokenFile), ct)).Trim();
            }
        }

        return (configuration, string.IsNullOrWhiteSpace(token) ? null : token);
    }

    private static Uri EnsureTrailingSlash(Uri endpoint)
        => endpoint.AbsoluteUri.EndsWith('/')
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);
}
