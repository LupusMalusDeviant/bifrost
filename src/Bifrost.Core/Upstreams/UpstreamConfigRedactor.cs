using Bifrost.Abstractions;

namespace Bifrost.Core.Upstreams;

/// <summary>
/// Zentrale Ausgabemaskierung für gespeicherte Upstream-Konfigurationen.
/// Die persistierte Instanz wird nicht verändert.
/// </summary>
public static class UpstreamConfigRedactor
{
    public const string Mask = "***";

    public static UpstreamServerConfig Redact(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config with
        {
            Stdio = config.Stdio is { EnvironmentVariables: { Count: > 0 } stdioEnv } stdio
                ? stdio with { EnvironmentVariables = RedactValues(stdioEnv) }
                : config.Stdio,
            Http = config.Http is { Headers: { Count: > 0 } headers } http
                ? http with { Headers = RedactValues(headers) }
                : config.Http,
            OpenApi = config.OpenApi is { Credential: not null } openApi
                ? openApi with { Credential = Mask }
                : config.OpenApi,
            // OpenRPC trägt dasselbe Credential-Feld wie OpenAPI und wurde beim Nachziehen
            // schlicht vergessen — der Wert ging über ApiEndpoints im Klartext an die Oberfläche.
            OpenRpc = config.OpenRpc is { Credential: not null } openRpc
                ? openRpc with { Credential = Mask }
                : config.OpenRpc,
            Cli = config.Cli is { EnvironmentVariables: { Count: > 0 } cliEnv } cli
                ? cli with { EnvironmentVariables = RedactValues(cliEnv) }
                : config.Cli,
            // WASI-Secrets sind Werte wie Header oder Credentials und gehören nicht in eine
            // Ausgabe (Plan 0003, WP4).
            Wasi = config.Wasi is { Secrets: { Count: > 0 } wasiSecrets } wasi
                ? wasi with { Secrets = RedactValues(wasiSecrets) }
                : config.Wasi,
        };
    }

    private static Dictionary<string, string> RedactValues(
        IReadOnlyDictionary<string, string> values)
        => values.ToDictionary(pair => pair.Key, _ => Mask, StringComparer.Ordinal);
}
