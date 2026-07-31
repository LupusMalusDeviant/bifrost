using System.Security.Cryptography;
using System.Text;

namespace Bifrost.Persistence;

/// <summary>
/// Geteiltes PBKDF2-SHA256-Hashing für API-Key-Secrets (WP3.3) und UI-Passwörter (WP6.1).
/// Format: <c>{iterations}.{saltB64}.{hashB64}</c> — die Iterationszahl steckt im Hash, daher bleiben
/// mit älteren Parametern erzeugte Hashes nach einer Erhöhung weiterhin verifizierbar (v1.1: 100k → 600k
/// gemäß OWASP-Empfehlung für PBKDF2-SHA256).
/// </summary>
internal static class Pbkdf2Hasher
{
    /// <summary>Iterationen für NEUE Hashes (OWASP-Empfehlung für PBKDF2-SHA256).</summary>
    private const int Iterations = 600_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string secret, string stored)
    {
        var parts = stored.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
