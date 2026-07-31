using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bifrost.Abstractions;

namespace Bifrost.Core.Approvals;

/// <summary>
/// Bindet eine Freigabe an genau einen Aufruf (ADR-0012): <c>(Identität, Tool, Argumente)</c>.
/// Wer <c>delete_file{path:/tmp/x}</c> freigibt, gibt nicht <c>delete_file{path:/etc/passwd}</c>
/// frei.
///
/// Über die <b>redigierten</b> Argumente gehasht — dieselbe Redaction wie im Audit —, damit die
/// Queue keine Secrets im Klartext hält und der Fingerprint trotzdem stabil ist.
/// </summary>
public static class ApprovalFingerprint
{
    public static string Compute(IdentityId caller, NamespacedToolName tool, JsonElement redactedArguments)
    {
        var argsText = redactedArguments.ValueKind is JsonValueKind.Undefined
            ? "∅"
            : Canonicalize(redactedArguments);

        var material = $"{caller.Value:N}{tool.Value}{argsText}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Kanonische Form: Objekt-Eigenschaften sortiert, damit unterschiedliche Schlüsselreihenfolge
    /// denselben Fingerprint ergibt — sonst würde ein umsortiertes, aber identisches Argument-Objekt
    /// die Freigabe verfehlen.
    /// </summary>
    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(element, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(prop.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
