using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Abstractions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Bifrost.ConnectorKit;

/// <summary>
/// Der private Ed25519-Schlüssel eines Herausgebers — <b>auf dessen Rechner</b>.
/// <para>
/// <b>Warum das hier liegt und nicht in <c>Bifrost.Core</c>:</b> Das Gateway sieht nie einen
/// privaten Schlüssel (siehe <c>ConnectorPackageBuilder</c>). Ein Signierer im Kern wäre Code, den
/// der Server mitschleppt, ohne ihn je zu brauchen — und die Aussage „das Gateway kann nicht
/// signieren" wäre nicht mehr durch die Bauform gedeckt, sondern nur noch durch Disziplin.
/// </para>
/// <para>
/// Die Datei enthält den <b>Seed</b> (32 Byte, Base64) und den Kennwert. Der Seed erscheint in
/// keiner Ausgabe dieses Werkzeugs — weder in einer Erfolgs- noch in einer Fehlermeldung.
/// </para>
/// </summary>
internal sealed class PublisherKeyFile
{
    private const int SeedBytes = 32;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Ed25519PrivateKeyParameters _private;

    private PublisherKeyFile(Ed25519PrivateKeyParameters key, string label)
    {
        _private = key;
        Label = label;
        PublicKey = key.GeneratePublicKey().GetEncoded();
        KeyId = Convert.ToHexStringLower(SHA256.HashData(PublicKey));
    }

    /// <summary>SHA-256 des Public Keys — derselbe Wert, den der Trust-Store führt.</summary>
    public string KeyId { get; }

    public byte[] PublicKey { get; }

    public string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

    public string Label { get; }

    public static PublisherKeyFile Create(string label) =>
        new(new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(SeedBytes), 0), label);

    public static PublisherKeyFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConnectorKitException(
                $"Kein Herausgeberschlüssel unter '{path}'. Einen neuen legt 'bifrost-connector init' "
                + "an; ein vorhandener wird mit --key übergeben.",
                ConnectorKitExit.UsageError);
        }

        Stored stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json)
                ?? throw new ConnectorKitException($"'{path}' ist leer.", ConnectorKitExit.UsageError);
        }
        catch (JsonException exception)
        {
            throw new ConnectorKitException(
                $"'{path}' ist keine gültige Schlüsseldatei: {exception.Message}", ConnectorKitExit.UsageError);
        }

        var seed = Convert.FromBase64String(stored.Seed);
        if (seed.Length != SeedBytes)
        {
            throw new ConnectorKitException(
                $"Der Schlüssel in '{path}' ist {seed.Length} Byte lang; Ed25519 verlangt {SeedBytes}.",
                ConnectorKitExit.UsageError);
        }

        var key = new PublisherKeyFile(new Ed25519PrivateKeyParameters(seed, 0), stored.Label);
        if (!string.Equals(key.KeyId, stored.KeyId, StringComparison.OrdinalIgnoreCase))
        {
            // Der Kennwert in der Datei ist eine Bequemlichkeit; abgeleitet wird er aus dem Seed.
            // Weichen beide ab, wurde an der Datei gedreht — und dann ist Raten die falsche Antwort.
            throw new ConnectorKitException(
                $"Der Kennwert in '{path}' passt nicht zum Schlüssel.", ConnectorKitExit.UsageError);
        }

        return key;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            throw new ConnectorKitException(
                $"'{path}' gibt es bereits. Ein Schlüssel wird nicht überschrieben — jedes damit "
                + "signierte Paket würde sonst zu einem Herausgeber gehören, den es nicht mehr gibt.",
                ConnectorKitExit.UsageError);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(
            new Stored(KeyId, Label, Convert.ToBase64String(_private.GetEncoded())), Json));

        // Auf Unix: nur der Besitzer. Unter Windows regeln das die geerbten ACLs des Verzeichnisses;
        // ein Aufruf hier waere wirkungslos und wuerde Sicherheit vortaeuschen.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public byte[] Sign(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, _private);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    /// <summary>Der öffentliche Teil, wie ihn ein Betreiber in seinen Trust-Store einträgt.</summary>
    public PublisherKey AsPublisher(ConnectorTrustLevel level = ConnectorTrustLevel.ThirdParty) =>
        new(KeyId, PublicKeyBase64, Label, DateTimeOffset.UtcNow, null, level);

    private sealed record Stored(string KeyId, string Label, string Seed);
}
