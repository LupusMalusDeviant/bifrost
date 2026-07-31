using Bifrost.Abstractions;

namespace Bifrost.Core.Upstreams;

/// <summary>
/// Führt eine bearbeitete Server-Konfiguration mit der bestehenden Konfiguration zusammen.
/// </summary>
public static class UpstreamConfigMerge
{
    /// <summary>
    /// Secret-Patch-Semantik: nicht geliefert oder <c>***</c> bedeutet beibehalten, ein neuer Wert
    /// ersetzt den alten und ein leerer Wert beziehungsweise eine leere Map löscht ihn explizit.
    /// Maskenwerte werden niemals persistiert.
    /// </summary>
    public static UpstreamServerConfig CarryOverSecrets(
        UpstreamServerConfig edited, UpstreamServerConfig previous)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(previous);

        return edited with
        {
            Stdio = edited.Stdio is { } stdio
                ? stdio with
                {
                    EnvironmentVariables = MergeSecretValues(
                        stdio.EnvironmentVariables, previous.Stdio?.EnvironmentVariables),
                }
                : edited.Stdio,
            Http = edited.Http is { } http
                ? http with
                {
                    Headers = MergeSecretValues(http.Headers, previous.Http?.Headers),
                }
                : edited.Http,
            OpenApi = edited.OpenApi is { } api
                ? api with
                {
                    Credential = MergeCredential(api.Credential, previous.OpenApi?.Credential),
                }
                : edited.OpenApi,
            Cli = edited.Cli is { } cli
                ? cli with
                {
                    EnvironmentVariables = MergeSecretValues(
                        cli.EnvironmentVariables, previous.Cli?.EnvironmentVariables),
                }
                : edited.Cli,
        };
    }

    private static IReadOnlyDictionary<string, string>? MergeSecretValues(
        IReadOnlyDictionary<string, string>? edited,
        IReadOnlyDictionary<string, string>? previous)
    {
        if (edited is null)
        {
            return previous;
        }

        if (edited.Count == 0)
        {
            return edited;
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in edited)
        {
            if (value.Length == 0)
            {
                continue;
            }

            if (value == UpstreamConfigRedactor.Mask)
            {
                if (previous is null || !previous.TryGetValue(key, out var previousValue))
                {
                    throw new ArgumentException(
                        $"Maskierter Secret-Wert für '{key}' hat keinen bestehenden Wert.");
                }

                merged[key] = previousValue;
                continue;
            }

            merged[key] = value;
        }

        return merged;
    }

    private static string? MergeCredential(string? edited, string? previous)
        => edited switch
        {
            null => previous,
            UpstreamConfigRedactor.Mask => previous
                ?? throw new ArgumentException("Maskiertes OpenAPI-Credential hat keinen bestehenden Wert."),
            "" => null,
            _ => edited,
        };
}
