using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Abstractions;

namespace Bifrost.ConnectorKit;

/// <summary>
/// Die Quellform eines Connectors auf der Platte: <c>connector.json</c> plus alles, was daneben
/// liegt.
/// <para>
/// <b>Was hier ausdrücklich nicht steht:</b> Prüfsummen und Signatur. Beide entstehen beim Bau —
/// eine von Hand gepflegte Prüfsumme ist eine, die irgendwann nicht mehr stimmt und trotzdem
/// dasteht.
/// </para>
/// <para>
/// <b>Welche Dateien ins Paket kommen:</b> alle unterhalb des Projektverzeichnisses, außer
/// <c>connector.json</c>, Schlüsseldateien und dem Ausgabeverzeichnis. Kein Glob-Dialekt — eine
/// Auswahlsprache wäre eine zweite Stelle, an der ein Paket unvollständig werden kann.
/// </para>
/// </summary>
internal sealed record ConnectorProject(ConnectorManifest Manifest, IReadOnlyDictionary<string, byte[]> Files)
{
    public const string FileName = "connector.json";

    /// <summary>Was nie ins Paket gehört — Schlüssel zuerst.</summary>
    private static readonly string[] Excluded = [FileName, "publisher.key.json", ".gitignore"];

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static ConnectorProject Load(string directory, string publisherKeyId)
    {
        var manifestPath = Path.Combine(directory, FileName);
        if (!File.Exists(manifestPath))
        {
            throw new ConnectorKitException(
                $"In '{directory}' liegt keine {FileName}. 'bifrost-connector init' legt sie an.",
                ConnectorKitExit.UsageError);
        }

        ConnectorManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ConnectorManifest>(File.ReadAllText(manifestPath), Json)
                ?? throw new ConnectorKitException($"'{manifestPath}' ist leer.", ConnectorKitExit.UsageError);
        }
        catch (JsonException exception)
        {
            throw new ConnectorKitException(
                $"'{manifestPath}' ist kein gültiges Manifest: {exception.Message}", ConnectorKitExit.UsageError);
        }

        // Der Herausgeber im Manifest kommt aus dem Schlüssel, mit dem gebaut wird. Stünde er nur
        // in der Datei, könnte ein Paket einen fremden Herausgeber behaupten und wäre erst beim
        // Einspielen auffällig — dort, wo niemand mehr weiß, wie es dazu kam.
        manifest = manifest with { PublisherKeyId = publisherKeyId };

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (Excluded.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                || relative.StartsWith("out/", StringComparison.Ordinal)
                || relative.EndsWith(".mcpkg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files[relative] = File.ReadAllBytes(path);
        }

        if (files.Count == 0)
        {
            throw new ConnectorKitException(
                $"'{directory}' enthält außer der {FileName} keine Dateien — ein Paket ohne Nutzlast "
                + "beschreibt nichts.",
                ConnectorKitExit.UsageError);
        }

        if (!files.ContainsKey(manifest.EntryPoint))
        {
            throw new ConnectorKitException(
                $"Der Einstiegspunkt '{manifest.EntryPoint}' liegt nicht im Projekt. Vorhanden: "
                + string.Join(", ", files.Keys.Order(StringComparer.Ordinal)),
                ConnectorKitExit.UsageError);
        }

        return new ConnectorProject(manifest, files);
    }

    /// <summary>Das Gerüst, das <c>init</c> schreibt — gültig, aber ohne fremde Annahmen.</summary>
    public static ConnectorManifest Skeleton(string id, string publisherKeyId, string entryPoint) => new(
        ConnectorManifest.SchemaV1,
        id,
        "0.1.0",
        ConnectorManifest.SupportedContractVersion,
        publisherKeyId,
        id,
        UpstreamTransportKind.Wasi,
        entryPoint,
        entryPoint + ".sig",
        Payloads: [],
        Grants: null,
        Platforms: null,
        Description: "Kurz: was dieser Connector kann und was er dafür braucht.");
}
