using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Abstractions;
using Bifrost.Core.Packaging;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Bifrost.Core.Tests.Packaging;

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

    public const string SkillEntry = "skills/benutzung.md";

    public static ConnectorManifest Manifest(
        TestPublisher publisher,
        string id = "com.example.echo",
        string version = "1.0.0",
        ConnectorGrantRequest? grants = null,
        UpstreamTransportKind transport = UpstreamTransportKind.Wasi,
        IReadOnlyList<string>? platforms = null,
        IReadOnlyList<ConnectorSkill>? skills = null)
        => new(
            ConnectorManifest.SchemaV1, id, version, ConnectorManifest.SupportedContractVersion,
            publisher.KeyId, "Echo-Connector", transport,
            ComponentEntry, ComponentSignatureEntry,
            Payloads: [], Grants: grants, Platforms: platforms, Skills: skills);

    public static IReadOnlyDictionary<string, byte[]> Files(
        byte[]? component = null, string? skillText = null)
    {
        var files = new Dictionary<string, byte[]>
        {
            [ComponentEntry] = component ?? [0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00],
            [ComponentSignatureEntry] = new byte[64],
        };

        if (skillText is not null)
        {
            files[SkillEntry] = System.Text.Encoding.UTF8.GetBytes(skillText);
        }

        return files;
    }

    public static MemoryStream Valid(
        TestPublisher publisher,
        string id = "com.example.echo",
        string version = "1.0.0",
        ConnectorGrantRequest? grants = null,
        UpstreamTransportKind transport = UpstreamTransportKind.Wasi,
        IReadOnlyList<string>? platforms = null,
        byte[]? component = null,
        IReadOnlyList<ConnectorSkill>? skills = null,
        string? skillText = null)
        => new(ConnectorPackageBuilder.Build(
            Manifest(publisher, id, version, grants, transport, platforms, skills),
            Files(component, skillText),
            publisher.Sign));

    /// <summary>
    /// Ein Paket mit genau einem Skill — die Bequemlichkeitsform, weil fast jeder Test dieselbe
    /// Kombination braucht: Deklaration im Manifest plus Textdatei als Nutzdatei.
    /// </summary>
    public static MemoryStream WithSkill(
        TestPublisher publisher,
        string text,
        string skillName = "benutzung",
        string id = "com.example.echo",
        string version = "1.0.0",
        string? whenToUse = null,
        IReadOnlyList<string>? requiredTools = null)
        => Valid(
            publisher, id, version,
            skills: [new ConnectorSkill(
                skillName, SkillEntry, "Wie der Konnektor benutzt wird", whenToUse,
                References: null, RequiredTools: requiredTools)],
            skillText: text);

    /// <summary>Der Zustimmungseintrag, den ein Administrator für diesen Skill abgeben müsste.</summary>
    public static string ConsentFor(
        TestPublisher publisher, string text, string skillName = "benutzung",
        string id = "com.example.echo", string version = "1.0.0",
        string? whenToUse = null, IReadOnlyList<string>? requiredTools = null)
    {
        var skill = new ConnectorSkill(
            skillName, SkillEntry, "Wie der Konnektor benutzt wird", whenToUse,
            References: null, RequiredTools: requiredTools);
        var manifest = WithPayloads(
            Manifest(publisher, id, version, skills: [skill]), Files(skillText: text));
        return manifest.SkillConsentToken(skill);
    }

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

internal sealed class StaticTrustStore : IPublisherTrustStore
{
    private readonly IReadOnlyList<PublisherKey> _keys;

    public StaticTrustStore(IReadOnlyList<PublisherKey> keys) => _keys = keys;

    public IReadOnlyList<PublisherKey> All => _keys;

    public IReadOnlyList<string> ActivePublicKeys =>
        [.. _keys.Where(k => k.IsActive).Select(k => k.PublicKeyBase64)];

    public event EventHandler<PublisherRevokedEventArgs>? Revoked
    {
        add { }
        remove { }
    }

    public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<PublisherKey> PinAsync(string publicKeyBase64, string label, CancellationToken ct)
        => Task.FromResult(_keys[0]);

    public Task RevokeAsync(string keyId, CancellationToken ct) => Task.CompletedTask;

    public Task ReinstateAsync(string keyId, CancellationToken ct) => Task.CompletedTask;

    public Task SetTrustLevelAsync(string keyId, ConnectorTrustLevel level, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>
/// Store ohne Datenbank. Die EF-Variante ist an ihrem eigenen Ort getestet; hier geht es um den
/// Ablauf, nicht um die Persistenz.
/// </summary>
internal sealed class InMemoryPackageStore : IConnectorPackageStore
{
    private readonly List<InstalledConnectorPackage> _packages = [];

    public Task<IReadOnlyList<InstalledConnectorPackage>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InstalledConnectorPackage>>([.. _packages]);

    public Task<InstalledConnectorPackage?> GetActiveAsync(string packageId, CancellationToken ct)
        => Task.FromResult(_packages.FirstOrDefault(
            p => p.PackageId == packageId && p.State is PackageState.Active));

    public Task<IReadOnlyList<InstalledConnectorPackage>> GetVersionsAsync(
        string packageId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<InstalledConnectorPackage>>(
            [.. _packages.Where(p => p.PackageId == packageId)]);

    public Task UpsertAsync(InstalledConnectorPackage package, CancellationToken ct)
    {
        _packages.RemoveAll(p => p.PackageId == package.PackageId && p.Version == package.Version);
        _packages.Add(package);
        return Task.CompletedTask;
    }

    public Task ActivateAsync(
        string packageId, string version, DateTimeOffset at, CancellationToken ct)
    {
        for (var i = 0; i < _packages.Count; i++)
        {
            if (_packages[i].PackageId != packageId)
            {
                continue;
            }

            _packages[i] = _packages[i].Version == version
                ? _packages[i] with
                {
                    State = PackageState.Active, ActivatedAt = at, FailureReason = null,
                }
                : _packages[i] with
                {
                    State = _packages[i].State is PackageState.Active
                        ? PackageState.Superseded
                        : _packages[i].State,
                };
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string packageId, string version, CancellationToken ct)
    {
        _packages.RemoveAll(p => p.PackageId == packageId && p.Version == version);
        return Task.CompletedTask;
    }
}
