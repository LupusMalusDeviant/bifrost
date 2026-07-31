using Bifrost.Abstractions;

namespace Bifrost.Core.Configuration;

/// <summary>Eine Upstream-Konfiguration ohne Zugangsdaten, samt der Liste dessen, was entfernt wurde.</summary>
public sealed record ScrubbedUpstream(UpstreamServerConfig Config, IReadOnlyList<SecretPlaceholder> References);

/// <summary>
/// Ersetzt jeden Wert, der ein Zugangsdatum sein kann, durch eine <b>Referenz</b> (ADR-0024 E8).
/// <para>
/// <b>Die Referenz wird ausschließlich aus dem Ort abgeleitet, nie aus dem Wert.</b> Kein Präfix,
/// kein Hash, keine Länge, keine Zeichenklasse. Ein maskiertes Zugangsdatum ist ein Zugangsdatum mit
/// weniger Zeichen — wer <c>ghp_abcd…***</c> in ein Repository legt, hat den Rest bereits verraten.
/// </para>
/// <para>
/// <b>Umgebungsvariablen gelten vollständig als Geheimnis.</b> Man sieht einer Variablen nicht an, ob
/// sie <c>NODE_ENV=production</c> oder <c>GITHUB_TOKEN=…</c> ist; eine Heuristik über Namen wäre
/// genau die Sorte Beinahe-Erkennung, die im Zweifel danebenliegt. Fail-closed heißt hier: Die
/// harmlose Variable muss auf der Zielinstanz nachgetragen werden. Das ist Mehrarbeit; der andere
/// Fehler wäre ein Leck.
/// </para>
/// </summary>
public static class ConfigurationSecretScrubber
{
    public const string ReferencePrefix = "${bifrost:secret/";

    public const string ReferenceSuffix = "}";

    public static string Reference(string path) => ReferencePrefix + path + ReferenceSuffix;

    /// <summary>Ist dieser Wert ein unaufgelöster Platzhalter aus einem Standardexport?</summary>
    public static bool IsReference(string? value)
        => value is not null
            && value.StartsWith(ReferencePrefix, StringComparison.Ordinal)
            && value.EndsWith(ReferenceSuffix, StringComparison.Ordinal);

    /// <summary>Entfernt alle Zugangsdaten aus einer Upstream-Konfiguration.</summary>
    public static ScrubbedUpstream Scrub(string slug, UpstreamServerConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(config);

        var collected = new List<SecretPlaceholder>();
        var scrubbed = Transform(slug, config, (path, location, _) =>
        {
            var reference = Reference(path);
            collected.Add(new SecretPlaceholder(reference, location));
            return reference;
        });

        return new ScrubbedUpstream(scrubbed, collected);
    }

    /// <summary>
    /// Die Platzhalter, die in dieser Konfiguration noch stehen — also die Zugangsdaten, die auf der
    /// Zielinstanz fehlen. Der Import baut darauf seine Ansage und schaltet den Upstream ab, statt
    /// ihn mit einem Platzhalter als Passwort starten zu lassen.
    /// </summary>
    public static IReadOnlyList<SecretPlaceholder> FindUnresolvedReferences(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var found = new List<SecretPlaceholder>();
        Transform("_", config, (_, location, value) =>
        {
            if (IsReference(value))
            {
                found.Add(new SecretPlaceholder(value, location));
            }

            return null;
        });

        return found;
    }

    /// <summary>
    /// Läuft genau einmal über alle Stellen, an denen ein Zugangsdatum stehen kann. Eine Stelle, die
    /// hier fehlt, fehlt in <b>beiden</b> Richtungen — das ist der Grund für den gemeinsamen Weg
    /// statt zweier Kopien, die auseinanderlaufen.
    /// </summary>
    /// <param name="map">
    /// Bekommt Pfad, Klartextbeschreibung und aktuellen Wert; liefert den Ersatz oder <c>null</c>
    /// für „unverändert".
    /// </param>
    private static UpstreamServerConfig Transform(
        string slug, UpstreamServerConfig config, Func<string, string, string, string?> map)
    {
        var stdio = config.Stdio;
        if (stdio is { EnvironmentVariables: { Count: > 0 } stdioEnv })
        {
            stdio = stdio with
            {
                EnvironmentVariables = MapValues(
                    $"upstream/{slug}/stdio-env", $"Upstream '{slug}': Umgebungsvariable", stdioEnv, map),
            };
        }

        var http = config.Http;
        if (http is { Headers: { Count: > 0 } headers })
        {
            http = http with
            {
                Headers = MapValues(
                    $"upstream/{slug}/http-header", $"Upstream '{slug}': HTTP-Header", headers, map),
            };
        }

        if (http is { OAuth: { ClientSecret: { Length: > 0 } clientSecret } oauth })
        {
            var replaced = map(
                $"upstream/{slug}/http-oauth/client-secret",
                $"Upstream '{slug}': OAuth-Client-Secret",
                clientSecret);
            if (replaced is not null)
            {
                http = http with { OAuth = oauth with { ClientSecret = replaced } };
            }
        }

        var openApi = config.OpenApi;
        if (openApi is { Credential: { Length: > 0 } openApiCredential })
        {
            var replaced = map(
                $"upstream/{slug}/openapi/credential",
                $"Upstream '{slug}': OpenAPI-Zugangsdatum",
                openApiCredential);
            if (replaced is not null)
            {
                openApi = openApi with { Credential = replaced };
            }
        }

        // OpenRPC trägt dasselbe Credential-Feld wie OpenAPI. Es fehlt im heutigen
        // UpstreamConfigRedactor (Bifrost.Core/Upstreams) — hier ist es abgedeckt, dort ist es als
        // Befund gemeldet.
        var openRpc = config.OpenRpc;
        if (openRpc is { Credential: { Length: > 0 } openRpcCredential })
        {
            var replaced = map(
                $"upstream/{slug}/openrpc/credential",
                $"Upstream '{slug}': OpenRPC-Zugangsdatum",
                openRpcCredential);
            if (replaced is not null)
            {
                openRpc = openRpc with { Credential = replaced };
            }
        }

        var cli = config.Cli;
        if (cli is { EnvironmentVariables: { Count: > 0 } cliEnv })
        {
            cli = cli with
            {
                EnvironmentVariables = MapValues(
                    $"upstream/{slug}/cli-env", $"Upstream '{slug}': CLI-Umgebungsvariable", cliEnv, map),
            };
        }

        var wasi = config.Wasi;
        if (wasi is { Secrets: { Count: > 0 } wasiSecrets })
        {
            wasi = wasi with
            {
                Secrets = MapValues(
                    $"upstream/{slug}/wasi-secret", $"Upstream '{slug}': WASI-Secret", wasiSecrets, map),
            };
        }

        return config with
        {
            Stdio = stdio,
            Http = http,
            OpenApi = openApi,
            OpenRpc = openRpc,
            Cli = cli,
            Wasi = wasi,
        };
    }

    private static IReadOnlyDictionary<string, string> MapValues(
        string basePath,
        string baseLocation,
        IReadOnlyDictionary<string, string> values,
        Func<string, string, string, string?> map)
    {
        Dictionary<string, string>? result = null;
        foreach (var pair in values)
        {
            if (pair.Value.Length == 0)
            {
                continue;
            }

            var replaced = map($"{basePath}/{pair.Key}", $"{baseLocation} '{pair.Key}'", pair.Value);
            if (replaced is null)
            {
                continue;
            }

            result ??= values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            result[pair.Key] = replaced;
        }

        return result ?? values;
    }
}
