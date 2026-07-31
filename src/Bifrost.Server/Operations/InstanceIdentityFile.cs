using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Server.Operations;

/// <summary>
/// <c>config/instance.json</c> — die stabile Kennung dieser Installation (WP2.7, Auftrag Punkt 6).
/// <para>
/// Bis hierher gab es die Datei im Code nicht: Das Manifest eines Backups trug deshalb eine
/// <b>leere</b> Instanz-Id, und der Restore konnte nicht sagen, ob ein Archiv zu dieser Installation
/// gehört. Das Backup legt sie ausdrücklich nicht an — eine Sicherung verändert die Instanz nicht,
/// die sie sichert. Erzeuger ist der Serverstart.
/// </para>
/// <para>
/// Die Datei wird <b>nur angelegt, nie überschrieben</b>. Nach einem Restore steht darin die Id der
/// gesicherten Instanz, und genau das ist gewollt: Die wiederhergestellte Installation <i>ist</i>
/// die alte.
/// </para>
/// </summary>
public static class InstanceIdentityFile
{
    /// <summary>Pfad relativ zum Datenverzeichnis — dieselbe Stelle, die das Backup sichert.</summary>
    public static string PathFor(string dataDirectory)
        => Path.Combine(dataDirectory, "config", "instance.json");

    /// <summary>
    /// Liest die Instanz-Id, legt sie beim ersten Start an. Fehler beim Schreiben werden
    /// weitergereicht: Eine Instanz ohne stabile Kennung ist ein Betriebsmangel, den der Start
    /// benennen soll — aber sie sind selten genug, dass der Aufrufer entscheidet, ob er deshalb
    /// abbricht.
    /// </summary>
    public static string EnsureCreated(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var path = PathFor(dataDirectory);
        if (File.Exists(path))
        {
            // Fail-closed: Eine vorhandene, aber unlesbare Datei wird nicht durch eine neue Id
            // ersetzt. Die alte Id steht auf jedem bereits erzeugten Archiv dieser Instanz — sie
            // stillschweigend zu wechseln hieße, die Zugehörigkeit aller Sicherungen zu verlieren.
            return TryRead(path)
                ?? throw new InvalidOperationException(
                    $"'{path}' ist vorhanden, enthält aber keine lesbare 'instanceId'. Die Datei "
                    + "trägt die stabile Kennung dieser Installation; sie wird nicht überschrieben. "
                    + "Inhalt prüfen oder die Datei nach einer Sicherung entfernen.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = new InstanceDocument(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

        // Atomar über das Zielverzeichnis: Zwei gleichzeitig startende Instanzen auf demselben
        // Volume sollen nicht eine halb geschriebene Datei hinterlassen. Wer verliert, liest danach
        // die Id des Gewinners.
        var temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        try
        {
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException)
        {
            // Eine zweite Instanz war schneller. Ihre Id gilt — nicht die eigene, die nie auf der
            // Platte stand.
            File.Delete(temporary);
            return TryRead(path)
                ?? throw new InvalidOperationException(
                    $"'{path}' wurde nebenläufig angelegt, ist aber nicht lesbar.");
        }

        return document.InstanceId;
    }

    private static string? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("instanceId", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Eine unlesbare Datei wird NICHT ersetzt: Darin könnte die Id stehen, mit der die
            // vorhandenen Sicherungen dieser Instanz beschriftet sind.
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private sealed record InstanceDocument(
        [property: JsonPropertyName("instanceId")] string InstanceId,
        [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);
}
