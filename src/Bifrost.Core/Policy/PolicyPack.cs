using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Core.Packaging;

namespace Bifrost.Core.Policy;

/// <summary>
/// Ein Policy Pack: eine <b>Konfigurationsnutzlast mit Namen und Signatur</b> — kein zweiter
/// Importweg (M6-Vertrag §2).
/// <para>
/// Die Nutzlast ist genau das, was der Konfigurationsexport erzeugt (M2/WP2.5). Was hier dazukommt,
/// ist die Frage, <em>von wem</em> sie stammt.
/// </para>
/// </summary>
public sealed record PolicyPackManifest(
    int FormatVersion,
    string Id,
    string Version,
    string PublisherKeyId,
    string Description,
    DateTimeOffset CreatedAt)
{
    /// <summary>Die einzige Fassung, die dieser Gateway kennt.</summary>
    public const int SupportedFormatVersion = 1;
}

public sealed record PolicyPack(PolicyPackManifest Manifest, string Payload, string SignatureBase64);

/// <summary>
/// Wird geworfen, wenn ein Pack nicht angenommen werden kann. Der Grund steht in der Nachricht —
/// ein „ungültig" ohne Grund lässt einen Betreiber raten.
/// </summary>
public sealed class PolicyPackException(string message) : Exception(message);

/// <summary>
/// Liest und prüft Packs. <b>Signatur zuerst</b>: Alles danach wertet Daten aus, hinter denen ein
/// Herausgeber steht — dieselbe Reihenfolge wie beim Connector-Paket.
/// </summary>
public static class PolicyPackReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Die Bytes, über die signiert wird: Manifest **und** Nutzlast. Nur das Manifest zu signieren
    /// hieße, eine fremde Nutzlast unter einem echten Namen ausliefern zu können.
    /// </summary>
    public static byte[] SigningMaterial(PolicyPackManifest manifest, string payload) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json) + "\n" + payload);

    public static string Serialize(PolicyPack pack) => JsonSerializer.Serialize(pack, Json);

    public static PolicyPack Parse(string document)
    {
        try
        {
            return JsonSerializer.Deserialize<PolicyPack>(document, Json)
                ?? throw new PolicyPackException("Das Pack ist leer.");
        }
        catch (JsonException exception)
        {
            throw new PolicyPackException($"Das Pack ist kein gültiges JSON: {exception.Message}");
        }
    }

    /// <summary>
    /// Prüft Signatur, Herausgeber und Formatversion und gibt den Herausgeber zurück, dessen
    /// Signatur getragen hat.
    /// </summary>
    public static PublisherKey Verify(PolicyPack pack, IReadOnlyList<PublisherKey> publishers)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(publishers);

        // Zuerst die Formatfrage — aber NICHT, um ein neueres Pack zu „unterstützen": Ein Pack aus
        // einer neueren Generation kann Felder tragen, die dieser Gateway nicht kennt, und still
        // zu ignorieren wäre bei Rechten die schlechteste aller Antworten.
        if (pack.Manifest.FormatVersion != PolicyPackManifest.SupportedFormatVersion)
        {
            throw new PolicyPackException(
                $"Das Pack verlangt Format {pack.Manifest.FormatVersion}; dieser Gateway kennt "
                + $"{PolicyPackManifest.SupportedFormatVersion}. Ein Pack aus einer anderen "
                + "Generation wird nicht teilweise angewendet.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(pack.SignatureBase64);
        }
        catch (FormatException)
        {
            throw new PolicyPackException("Die Signatur ist kein gültiges Base64.");
        }

        var material = SigningMaterial(pack.Manifest, pack.Payload);
        var signer = publishers.FirstOrDefault(publisher =>
            publisher.IsActive
            && Ed25519Verifier.Verify(Convert.FromBase64String(publisher.PublicKeyBase64), material, signature));

        if (signer is null)
        {
            throw new PolicyPackException(
                "Keine aktive Herausgeberkennung passt zur Signatur dieses Packs.");
        }

        // Das Manifest nennt seinen Herausgeber selbst — stimmt das nicht mit dem überein, der
        // wirklich signiert hat, ist eine der beiden Angaben falsch, und raten hilft hier nicht.
        if (!string.Equals(pack.Manifest.PublisherKeyId, signer.KeyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new PolicyPackException(
                $"Das Manifest nennt den Herausgeber '{pack.Manifest.PublisherKeyId}', signiert hat "
                + $"aber '{signer.KeyId}'.");
        }

        return signer;
    }

    /// <summary>Baut ein Pack — für Werkzeuge und Tests; der Gateway signiert nie selbst.</summary>
    public static PolicyPack Build(PolicyPackManifest manifest, string payload, Func<byte[], byte[]> sign)
    {
        ArgumentNullException.ThrowIfNull(sign);
        return new PolicyPack(manifest, payload, Convert.ToBase64String(sign(SigningMaterial(manifest, payload))));
    }

    /// <summary>Der Fingerabdruck der Nutzlast — für Audit und Wiedererkennung.</summary>
    public static string PayloadHash(string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
