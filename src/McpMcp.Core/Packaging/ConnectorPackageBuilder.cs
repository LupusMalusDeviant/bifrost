using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpMcp.Abstractions;

namespace McpMcp.Core.Packaging;

/// <summary>
/// Baut ein Connector-Paket (<c>.mcpkg</c>) — die Gegenseite zu
/// <see cref="ConnectorPackageReader"/>.
/// <para>
/// Bewusst im Produktcode und nicht nur im Testprojekt: Das Paketformat ist ein Vertrag mit
/// Dritten, und ein Format, das nur die Prüfseite kennt, ist keiner. Wer einen Connector
/// veröffentlicht, erzeugt das Paket hiermit und signiert das Manifest mit seinem privaten
/// Ed25519-Schlüssel — <b>außerhalb</b> des Gateways, das nie einen privaten Schlüssel sieht.
/// </para>
/// </summary>
public static class ConnectorPackageBuilder
{
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    /// <summary>
    /// Schreibt ein Paket. <paramref name="files"/> bildet Pfad im Archiv auf Inhalt ab; die Hashes
    /// trägt diese Methode ins Manifest ein, damit sie nicht von Hand gepflegt werden müssen.
    /// </summary>
    /// <param name="sign">
    /// Signiert die serialisierten Manifest-Bytes. Genau diese Bytes landen als
    /// <c>manifest.json</c> im Archiv — signiert wird, was ausgeliefert wird.
    /// </param>
    public static byte[] Build(
        ConnectorManifest manifest,
        IReadOnlyDictionary<string, byte[]> files,
        Func<byte[], byte[]> sign)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sign);

        var complete = manifest with
        {
            Payloads =
            [
                .. files.OrderBy(f => f.Key, StringComparer.Ordinal)
                    .Select(f => new ConnectorPayload(f.Key, Convert.ToHexStringLower(SHA256.HashData(f.Value)))),
            ],
        };

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(complete, ManifestJson);
        var signature = sign(manifestBytes);

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, ConnectorPackageReader.ManifestEntry, manifestBytes);
            Write(archive, ConnectorPackageReader.SignatureEntry, signature);
            foreach (var (path, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                Write(archive, path, content);
            }
        }

        return buffer.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}
