using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpMcp.Abstractions;
using McpMcp.Core.Packaging;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace McpMcp.Core.Tests.Packaging;

/// <summary>
/// Ein Herausgeber mit echtem Ed25519-Schlüsselpaar. Die Tests signieren damit wirklich — ein
/// Attrappen-Signierer würde die Prüfung nicht belegen, sondern umgehen.
/// </summary>
internal sealed class TestPublisher
{
    private readonly Ed25519PrivateKeyParameters _private;

    public TestPublisher(ConnectorTrustLevel level = ConnectorTrustLevel.Official, string label = "test")
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        _private = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKey = _private.GeneratePublicKey().GetEncoded();
        PublicKeyBase64 = Convert.ToBase64String(publicKey);
        KeyId = Convert.ToHexStringLower(SHA256.HashData(publicKey));
        Key = new PublisherKey(KeyId, PublicKeyBase64, label, DateTimeOffset.UnixEpoch, null, level);
    }

    public string KeyId { get; }

    public string PublicKeyBase64 { get; }

    public PublisherKey Key { get; }

    public byte[] Sign(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, _private);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }
}

/// <summary>Baut Testpakete — gültige und absichtlich kaputte.</summary>
internal static class TestPackage
{
    public const string ComponentEntry = "payload/component.wasm";
    public const string ComponentSignatureEntry = "payload/component.wasm.sig";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static ConnectorManifest Manifest(
        TestPublisher publisher,
        string id = "com.example.echo",
        string version = "1.0.0",
        ConnectorGrantRequest? grants = null,
        UpstreamTransportKind transport = UpstreamTransportKind.Wasi,
        IReadOnlyList<string>? platforms = null)
        => new(
            ConnectorManifest.SchemaV1, id, version, ConnectorManifest.SupportedContractVersion,
            publisher.KeyId, "Echo-Connector", transport,
            ComponentEntry, ComponentSignatureEntry,
            Payloads: [], Grants: grants, Platforms: platforms);

    public static IReadOnlyDictionary<string, byte[]> Files(byte[]? component = null)
        => new Dictionary<string, byte[]>
        {
            [ComponentEntry] = component ?? [0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00],
            [ComponentSignatureEntry] = new byte[64],
        };

    public static MemoryStream Valid(
        TestPublisher publisher,
        string id = "com.example.echo",
        string version = "1.0.0",
        ConnectorGrantRequest? grants = null,
        UpstreamTransportKind transport = UpstreamTransportKind.Wasi,
        IReadOnlyList<string>? platforms = null,
        byte[]? component = null)
        => new(ConnectorPackageBuilder.Build(
            Manifest(publisher, id, version, grants, transport, platforms),
            Files(component),
            publisher.Sign));

    /// <summary>
    /// Baut ein Archiv von Hand — für die Fälle, die ein korrekter Builder gar nicht erzeugen
    /// könnte: ein manipulierter Payload, ein blinder Passagier, ein Pfad nach draußen.
    /// </summary>
    public static MemoryStream Raw(
        ConnectorManifest manifest,
        Func<byte[], byte[]> sign,
        IReadOnlyDictionary<string, byte[]> entries)
    {
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, ConnectorPackageReader.ManifestEntry, manifestBytes);
            Write(archive, ConnectorPackageReader.SignatureEntry, sign(manifestBytes));
            foreach (var (path, content) in entries)
            {
                Write(archive, path, content);
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    public static ConnectorManifest WithPayloads(
        ConnectorManifest manifest, IReadOnlyDictionary<string, byte[]> files)
        => manifest with
        {
            Payloads =
            [
                .. files.OrderBy(f => f.Key, StringComparer.Ordinal)
                    .Select(f => new ConnectorPayload(
                        f.Key, Convert.ToHexStringLower(SHA256.HashData(f.Value)))),
            ],
        };

    private static void Write(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }
}
