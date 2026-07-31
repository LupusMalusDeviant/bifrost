using System.Text.Json;
using System.Text.Json.Serialization;

using Bifrost.Core.Diagnostics.Checks;

namespace Bifrost.Server.KeyRing;

/// <summary>
/// Was diese Instanz beim letzten erfolgreichen Start über ihren Key-Ring wusste.
/// <para>
/// <b>Wozu:</b> Ohne diesen Eintrag sind „frische Installation" und „Datenverzeichnis verloren"
/// von außen nicht zu unterscheiden — beide zeigen ein leeres Schlüsselverzeichnis. Genau diese
/// Verwechslung ist beim v0.11.0-Umstieg passiert: umbenanntes Volume, leerer Ring, Meldung
/// „bereit".
/// </para>
/// </summary>
/// <param name="Mode">Der Betriebsmodus, unter dem zuletzt gestartet wurde.</param>
/// <param name="KeyCount">Wie viele Schlüsseldateien vorhanden waren.</param>
/// <param name="KeyIds">Welche — namentlich. Daran fällt ein <b>ausgetauschter</b> Ring auf.</param>
/// <param name="WrittenAt">Wann.</param>
/// <param name="Note">Ein Satz, der den Eintrag ohne Codelektüre verständlich macht.</param>
public sealed record KeyRingWitnessRecord(
    string Mode,
    int KeyCount,
    IReadOnlyList<string> KeyIds,
    DateTimeOffset WrittenAt,
    string Note);

/// <summary>Lesen und Schreiben des Zeugeneintrags.</summary>
public interface IKeyRingWitnessStore
{
    /// <summary>Der gespeicherte Eintrag, oder <c>null</c>, wenn noch keiner geschrieben wurde.</summary>
    /// <exception cref="KeyRingWitnessException">
    /// Wenn einer dasteht, aber nicht gelesen werden kann. Ausdrücklich kein <c>null</c>: „da steht
    /// nichts" heißt frische Instanz, „da steht etwas Unlesbares" heißt <b>nicht</b> frische
    /// Instanz — und nur die erste Lage darf zu einem neuen Ring führen.
    /// </exception>
    KeyRingWitnessRecord? Read();

    void Write(KeyRingWitnessRecord record);

    /// <summary>Wo der Eintrag liegt — für Meldungen und Diagnose.</summary>
    string Location { get; }
}

/// <summary>Der Zeugeneintrag war vorhanden, aber nicht lesbar.</summary>
public sealed class KeyRingWitnessException : InvalidOperationException
{
    public KeyRingWitnessException()
        : base("Der Key-Ring-Zeugeneintrag ist nicht lesbar.")
    {
    }

    public KeyRingWitnessException(string message)
        : base(message)
    {
    }

    public KeyRingWitnessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Der Eintrag als Datei im Datenverzeichnis, nach dem Vorbild von <c>HostExecutionSettingFile</c>
/// und <c>InstanceIdentityFile</c>.
/// <para>
/// <b>Warum eine Datei und keine Tabelle:</b> Der Eintrag wird gebraucht, <i>bevor</i> irgendetwas
/// entschlüsselt wird, und er muss auch dann lesbar sein, wenn die Datenbank gerade das Problem ist.
/// Außerdem hätte eine Tabelle eine EF-Migration verlangt — und die Datei liegt im selben Volume wie
/// der Ring, teilt also sein Schicksal. Genau das ist gewollt: Verschwindet das Volume, verschwindet
/// der Zeuge <b>mit</b> dem Ring, und dann trägt die Datenbank die Beweislast (siehe
/// <see cref="KeyRingVerdict"/>).
/// </para>
/// </summary>
public sealed class KeyRingWitnessFile : IKeyRingWitnessStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public KeyRingWitnessFile(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = KeyRingLayout.WitnessPathFor(dataDirectory);
    }

    public string Location => _path;

    public KeyRingWitnessRecord? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new KeyRingWitnessException(
                $"Der Key-Ring-Zeugeneintrag in '{_path}' ist nicht lesbar.", exception);
        }

        try
        {
            return JsonSerializer.Deserialize<KeyRingWitnessRecord>(content, Json)
                ?? throw new KeyRingWitnessException(
                    $"Der Key-Ring-Zeugeneintrag in '{_path}' ist leer.");
        }
        catch (JsonException exception)
        {
            // Nicht „dann eben frische Instanz": Eine beschädigte Datei sähe sonst aus wie eine
            // Neuinstallation — und die Neuinstallation ist genau der Weg, der einen neuen Ring
            // anlegt und den alten Geheimtext unlesbar zurücklässt.
            throw new KeyRingWitnessException(
                $"Der Key-Ring-Zeugeneintrag in '{_path}' ist beschädigt.", exception);
        }
    }

    public void Write(KeyRingWitnessRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Erst vollständig daneben schreiben, dann ersetzen. Eine halb geschriebene Datei wäre beim
        // nächsten Start „beschädigt" — also ein Startabbruch aus reiner Schreibunterbrechung.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
        File.Move(temporary, _path, overwrite: true);
    }
}
