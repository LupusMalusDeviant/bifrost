using System.Security.Cryptography;
using System.Text;

namespace Bifrost.Server.Bootstrap;

/// <summary>
/// Erzeugung und Prüfung des einmaligen Setup-Tokens (WP3.4).
/// <para>
/// <b>Warum SHA-256 und nicht PBKDF2 wie bei Passwörtern und API-Keys.</b> Ein Passwort ist
/// erratbar, deshalb kostet dort jeder Rateversuch absichtlich Rechenzeit. Dieses Token ist
/// <see cref="EntropyBytes">32 Byte</see> aus dem Systemzufall — es gibt keine Wortliste, gegen die
/// sich das durchprobieren ließe, und ein Angreifer, der 2^256 Möglichkeiten durchgeht, wird von
/// einer teuren Ableitung nicht aufgehalten. Umgekehrt kostet sie hier etwas: Der Einlösepfad ist
/// der einzige <b>unauthentifizierte</b> Schreibpfad dieser Anwendung. Eine Ableitung mit 600.000
/// Iterationen darauf wäre eine offene Einladung, den Dienst mit falschen Token lahmzulegen.
/// </para>
/// <para>
/// Verglichen wird trotzdem in konstanter Zeit: Der Hash steht in einer Datei, und wer sie lesen
/// kann, soll aus der Antwortzeit nichts weiter erfahren.
/// </para>
/// </summary>
public static class BootstrapToken
{
    /// <summary>Zufallsanteil des Tokens in Byte.</summary>
    public const int EntropyBytes = 32;

    /// <summary>
    /// Vorangestellt, damit ein Wert in einem Ticketanhang als das erkennbar ist, was er ist — und
    /// damit ein Leck-Scan nach ihm suchen kann.
    /// </summary>
    public const string Prefix = "bfsetup_";

    /// <summary>Ein frisches Token. Es existiert danach nur in der Rückgabe dieser Methode.</summary>
    public static string Create()
        => Prefix + Base64Url(RandomNumberGenerator.GetBytes(EntropyBytes));

    /// <summary>Der Hash, wie er in der Zustandsdatei steht (Hex, kleingeschrieben).</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>Stimmt das vorgelegte Token mit dem gespeicherten Hash überein?</summary>
    public static bool Matches(string? presented, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        byte[] stored;
        try
        {
            stored = Convert.FromHexString(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(actual, stored);
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
