using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bifrost.Core.Configuration;

/// <summary>
/// Der verschlüsselte Umschlag eines Credential-Exports.
/// <para>
/// <b>Der Kopf liegt im Klartext</b> — aus demselben Grund wie das Manifest eines Backups (ADR-0024
/// E1/E3): Ein Werkzeug muss sagen können, was es vor sich hat, <em>bevor</em> es eine Passphrase
/// verlangt. Er ist zugleich <c>Associated Data</c> der AEAD-Verschlüsselung; wer
/// <see cref="ContainsSecrets"/> im Nachhinein auf <c>false</c> dreht, bekommt beim Entschlüsseln
/// einen Fehler statt eines harmlos aussehenden Dokuments.
/// </para>
/// </summary>
public sealed record ConfigurationExportEnvelope(
    int FormatVersion,
    string ProductVersion,
    DateTimeOffset CreatedAt,
    bool ContainsSecrets,
    EnvelopeEncryption Encryption,
    string Ciphertext);

/// <param name="Iterations">
/// Bewusst hoch (<see cref="ConfigurationCrypto.Pbkdf2Iterations"/>). Eine Passphrase ist das
/// einzige, was zwischen dieser Datei und jedem Zugangsdatum der Instanz steht.
/// </param>
public sealed record EnvelopeEncryption(
    string Algorithm,
    string Kdf,
    int Iterations,
    string Salt,
    string Nonce,
    string Tag);

/// <summary>
/// Verschlüsselung des Credential-Exports. <b>Keine eigene Kryptografie</b> (ADR-0024 E3):
/// PBKDF2-SHA256 aus der Standardbibliothek für die Ableitung, AES-256-GCM für die Nutzlast.
/// </summary>
public static class ConfigurationCrypto
{
    public const string Algorithm = "aes-256-gcm";

    public const string Kdf = "pbkdf2-sha256";

    /// <summary>Wie im M2-Vertrag §2 für Backups festgelegt — derselbe Wert, dieselbe Begründung.</summary>
    public const int Pbkdf2Iterations = 600_000;

    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static ConfigurationExportEnvelope Encrypt(
        ConfigurationExportDocument document, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(passphrase, salt);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, ConfigurationExportJson.Options);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        var associatedData = AssociatedData(
            document.FormatVersion, document.ProductVersion, document.CreatedAt, document.ContainsSecrets);

        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);

        return new ConfigurationExportEnvelope(
            document.FormatVersion,
            document.ProductVersion,
            document.CreatedAt,
            document.ContainsSecrets,
            new EnvelopeEncryption(
                Algorithm,
                Kdf,
                Pbkdf2Iterations,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag)),
            Convert.ToBase64String(ciphertext));
    }

    public static ConfigurationExportDocument Decrypt(
        ConfigurationExportEnvelope envelope, string? passphrase)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.IsNullOrEmpty(passphrase))
        {
            throw new ConfigurationImportException(
                "Dieser Export ist verschlüsselt. Ohne Passphrase lässt er sich nicht lesen.");
        }

        var encryption = envelope.Encryption
            ?? throw new ConfigurationImportException("Dem Export fehlt die Angabe, wie er verschlüsselt ist.");

        if (!string.Equals(encryption.Algorithm, Algorithm, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(encryption.Kdf, Kdf, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationImportException(
                $"Unbekanntes Verschlüsselungsverfahren '{encryption.Algorithm}'/'{encryption.Kdf}'. "
                + "Dieser Export stammt vermutlich aus einer neueren Version.");
        }

        byte[] salt, nonce, tag, ciphertext;
        try
        {
            salt = Convert.FromBase64String(encryption.Salt);
            nonce = Convert.FromBase64String(encryption.Nonce);
            tag = Convert.FromBase64String(encryption.Tag);
            ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        }
        catch (FormatException ex)
        {
            throw new ConfigurationImportException("Der verschlüsselte Export ist beschädigt.", ex);
        }

        if (nonce.Length != NonceBytes || tag.Length != TagBytes || salt.Length == 0)
        {
            throw new ConfigurationImportException("Der verschlüsselte Export ist beschädigt.");
        }

        var key = DeriveKey(passphrase, salt);
        var plaintext = new byte[ciphertext.Length];
        var associatedData = AssociatedData(
            envelope.FormatVersion, envelope.ProductVersion, envelope.CreatedAt, envelope.ContainsSecrets);

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }
        catch (CryptographicException ex)
        {
            // Genau ein Satz für zwei Ursachen, und das ist Absicht: AES-GCM kann nicht
            // unterscheiden, ob die Passphrase falsch war oder jemand am Geheimtext gedreht hat.
            // Eine Meldung, die so täte, wäre geraten.
            throw new ConfigurationImportException(
                "Die Passphrase passt nicht zu diesem Export — oder der Export wurde nachträglich verändert. "
                + "Beides sieht für die Prüfsumme gleich aus.",
                ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            return JsonSerializer.Deserialize<ConfigurationExportDocument>(
                    plaintext, ConfigurationExportJson.Options)
                ?? throw new ConfigurationImportException("Der Export ist leer.");
        }
        catch (JsonException ex)
        {
            throw new ConfigurationImportException("Der entschlüsselte Export ist kein gültiges Dokument.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);

    private static byte[] AssociatedData(
        int formatVersion, string productVersion, DateTimeOffset createdAt, bool containsSecrets)
        => Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{formatVersion}|{productVersion}|{createdAt:O}|{containsSecrets}"));
}
