using System.IO.Compression;
using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Core.Packaging;

namespace Bifrost.ConnectorKit;

/// <summary>
/// Liest ein vorhandenes Paket in seine Bestandteile zurück — Manifest und Nutzlast, ohne Prüfung.
/// <para>
/// <b>Nur für <c>sign</c>.</b> Ein Paket neu zu signieren heißt, dieselben Bestandteile erneut zu
/// bauen; die Prüfung übernimmt danach <see cref="ConnectorPackageReader"/> wie bei jedem anderen
/// Paket. Die Namen der beiden Sondereinträge kommen aus dem Leser des Produkts, damit hier keine
/// zweite Formatkenntnis entsteht.
/// </para>
/// </summary>
internal static class ConnectorPackageSource
{
    public static (ConnectorManifest Manifest, IReadOnlyDictionary<string, byte[]> Files) Read(byte[] package)
    {
        using var buffer = new MemoryStream(package);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        var manifestEntry = archive.GetEntry(ConnectorPackageReader.ManifestEntry)
            ?? throw new ConnectorKitException(
                $"Kein '{ConnectorPackageReader.ManifestEntry}' im Archiv — das ist kein Connector-Paket.",
                ConnectorKitExit.PackageInvalid);

        ConnectorManifest manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<ConnectorManifest>(stream, ConnectorProject.Json)
                ?? throw new ConnectorKitException(
                    "Das Manifest im Archiv ist leer.", ConnectorKitExit.PackageInvalid);
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName is ConnectorPackageReader.ManifestEntry or ConnectorPackageReader.SignatureEntry)
            {
                continue;
            }

            using var stream = entry.Open();
            using var content = new MemoryStream();
            stream.CopyTo(content);
            files[entry.FullName] = content.ToArray();
        }

        return (manifest, files);
    }
}
