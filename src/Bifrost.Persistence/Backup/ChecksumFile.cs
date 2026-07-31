using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// <c>checksums.json</c>: Eintragsname → SHA-256 der Bytes, <b>wie sie im Archiv stehen</b>.
/// <para>
/// Bewusst über den gespeicherten (also bei Verschlüsselung: den geheimen) Text und nicht über den
/// Klartext: Sonst könnte ein Werkzeug ein Archiv ohne Passphrase nicht auf Unversehrtheit prüfen —
/// und genau das ist der Zweck von <c>backup verify</c>.
/// </para>
/// </summary>
internal sealed record ChecksumFile
{
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; init; } = BackupLayout.ChecksumAlgorithm;

    [JsonPropertyName("entries")]
    public Dictionary<string, string> Entries { get; init; } = [];

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    public static bool TryParse(ReadOnlySpan<byte> utf8, out ChecksumFile? file, out string? problem)
    {
        file = null;
        problem = null;
        try
        {
            file = JsonSerializer.Deserialize<ChecksumFile>(utf8, SerializerOptions);
        }
        catch (JsonException ex)
        {
            problem = $"checksums.json ist kein gültiges JSON: {ex.Message}";
            return false;
        }

        if (file is null)
        {
            problem = "checksums.json ist leer.";
            return false;
        }

        if (!string.Equals(file.Algorithm, BackupLayout.ChecksumAlgorithm, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"checksums.json nennt ein unbekanntes Prüfsummenverfahren: '{file.Algorithm}'.";
            file = null;
            return false;
        }

        return true;
    }

    public static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    public static bool Matches(string? expected, string actual)
        => expected is not null && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
