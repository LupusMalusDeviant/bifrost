using System.Security.Cryptography;
using System.Text;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Verschlüsselung der Nutzlast eines Archivs (ADR-0024 E3). Ausschließlich Bausteine der
/// Standardbibliothek: PBKDF2-SHA256 als KDF, AES-256-GCM als AEAD. <b>Keine eigene Kryptografie</b>
/// — kein selbstgebauter Modus, keine selbstgebaute Kettenbildung, keine eigene Integritätsprüfung.
/// <para>
/// Ein Eintrag wird als <c>nonce(12) ‖ ciphertext ‖ tag(16)</c> abgelegt. Der Eintragsname geht als
/// zusätzliche authentifizierte Angabe ein: Damit lässt sich ein Eintrag nicht gegen einen anderen
/// desselben Archivs austauschen, ohne dass die Prüfung fehlschlägt.
/// </para>
/// <para>
/// Das Salz steht im unverschlüsselten Manifest — es ist kein Geheimnis, und der Restore braucht es,
/// bevor er die Nutzlast anfasst. Abgeleitet wird <b>einmal pro Archiv</b>; jeder Eintrag bekommt
/// eine eigene, zufällige Nonce.
/// </para>
/// </summary>
internal sealed class ArchivePayloadCipher
{
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12; // AesGcm.NonceByteSizes.MaxSize
    private const int TagBytes = 16;   // AesGcm.TagByteSizes.MaxSize

    /// <summary>
    /// Obergrenze eines verschlüsselten Eintrags. AES-GCM ist in .NET ein Einmalvorgang über einen
    /// zusammenhängenden Puffer; ein größerer Eintrag bräuchte eine gestückelte Konstruktion — also
    /// eigene Kryptografie. Lieber eine klare Grenze als ein selbstgebautes Verfahren.
    /// </summary>
    public const long MaxEncryptedEntryBytes = 512L * 1024 * 1024;

    private readonly byte[] _key;

    private ArchivePayloadCipher(byte[] key, byte[] salt)
    {
        _key = key;
        Salt = salt;
    }

    public byte[] Salt { get; }

    public static ArchivePayloadCipher CreateNew(string passphrase)
        => FromSalt(passphrase, RandomNumberGenerator.GetBytes(SaltBytes), BackupLayout.KdfIterations);

    public static ArchivePayloadCipher FromSalt(string passphrase, byte[] salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length < SaltBytes)
        {
            throw new ArgumentException("Das Salz ist zu kurz.", nameof(salt));
        }

        if (iterations < 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations), iterations, "Zu wenige KDF-Runden für ein Vollbackup.");
        }

        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        return new ArchivePayloadCipher(key, salt);
    }

    public byte[] Encrypt(string entryName, ReadOnlySpan<byte> plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        if (plaintext.Length > MaxEncryptedEntryBytes)
        {
            throw new InvalidOperationException(
                $"'{entryName}' ist größer als {MaxEncryptedEntryBytes / (1024 * 1024)} MiB und lässt sich " +
                "nicht als ein verschlüsselter Eintrag ablegen. Unverschlüsselt sichern und das Ziel " +
                "verschlüsseln, oder den Bereich einzeln sichern.");
        }

        var output = new byte[NonceBytes + plaintext.Length + TagBytes];
        var nonce = output.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(_key, TagBytes);
        gcm.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(NonceBytes, plaintext.Length),
            output.AsSpan(NonceBytes + plaintext.Length, TagBytes),
            Encoding.UTF8.GetBytes(entryName));
        return output;
    }

    /// <summary>
    /// Entschlüsselt einen Eintrag. Schlägt die Prüfsumme des AEAD fehl, gibt es <b>kein</b>
    /// Teilergebnis: falsche Passphrase und manipulierter Geheimtext sind derselbe Fall, und beide
    /// liefern nichts.
    /// </summary>
    public bool TryDecrypt(string entryName, ReadOnlySpan<byte> stored, out byte[] plaintext, out string? problem)
    {
        plaintext = [];
        problem = null;
        if (stored.Length < NonceBytes + TagBytes)
        {
            problem = $"'{entryName}' ist zu kurz für einen verschlüsselten Eintrag.";
            return false;
        }

        var length = stored.Length - NonceBytes - TagBytes;
        var buffer = new byte[length];
        using var gcm = new AesGcm(_key, TagBytes);
        try
        {
            gcm.Decrypt(
                stored[..NonceBytes],
                stored.Slice(NonceBytes, length),
                stored.Slice(NonceBytes + length, TagBytes),
                buffer,
                Encoding.UTF8.GetBytes(entryName));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(buffer);
            problem = $"'{entryName}' ließ sich nicht entschlüsseln — falsche Passphrase oder verändertes Archiv.";
            return false;
        }

        plaintext = buffer;
        return true;
    }
}
