using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bifrost.Core.Execution;

/// <summary>
/// Der geschriebene Wert der Instanz. Ohne ihn wäre die Bestandsübernahme eine Annahme, die bei
/// jedem Start neu getroffen würde — und beim ersten Umbau der Upstreams stillschweigend anders
/// ausfiele (ADR-0025 E3, Punkt 2).
/// </summary>
/// <param name="Allowed">Der übernommene beziehungsweise festgelegte Zustand.</param>
/// <param name="Origin">
/// Warum er dasteht. Bleibt über Neustarts erhalten: Eine Übernahme, die sich beim zweiten Start in
/// ein reguläres „erlaubt" verwandelt, hätte die Warnung genau einmal gezeigt — und danach sähe die
/// Instanz aus wie eine, in der jemand bewusst entschieden hat.
/// </param>
/// <param name="WrittenAt">Wann.</param>
/// <param name="Upstreams">Wegen welcher Upstreams — namentlich.</param>
/// <param name="Note">Ein Satz, der den Eintrag ohne Codelektüre verständlich macht.</param>
public sealed record HostExecutionSettingRecord(
    bool Allowed,
    HostExecutionOrigin Origin,
    DateTimeOffset WrittenAt,
    IReadOnlyList<string> Upstreams,
    string Note);

/// <summary>Lesen und Schreiben des festgeschriebenen Wertes.</summary>
public interface IHostExecutionSettingStore
{
    /// <summary>Der gespeicherte Wert, oder <c>null</c>, wenn noch keiner geschrieben wurde.</summary>
    /// <exception cref="HostExecutionSettingException">
    /// Wenn ein Wert dasteht, aber nicht gelesen werden kann. Ausdrücklich kein <c>null</c>: „da
    /// steht nichts" und „da steht etwas Unlesbares" sind verschiedene Lagen, und nur die erste
    /// darf zu einer Übernahme führen.
    /// </exception>
    HostExecutionSettingRecord? Read();

    /// <summary>Schreibt den Wert. Bestehende Einträge werden ersetzt.</summary>
    void Write(HostExecutionSettingRecord record);

    /// <summary>Wo der Wert liegt — für Diagnose und Meldungen.</summary>
    string Location { get; }
}

/// <summary>Der gespeicherte Wert war vorhanden, aber nicht lesbar.</summary>
public sealed class HostExecutionSettingException : InvalidOperationException
{
    public HostExecutionSettingException()
        : base("Der gespeicherte Wert der Ausführungs-Policy ist nicht lesbar.")
    {
    }

    public HostExecutionSettingException(string message)
        : base(message)
    {
    }

    public HostExecutionSettingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Der Wert als Datei im Datenverzeichnis, nach dem Vorbild von <c>InstanceIdentityFile</c>.
/// <para>
/// <b>Warum eine Datei und keine Tabelle:</b> Der Wert wird gebraucht, bevor Upstreams starten, und
/// er muss auch dann lesbar sein, wenn die Datenbank gerade das Problem ist. Außerdem hätte eine
/// Tabelle eine Migration verlangt — und eine Migration, die einem Betreiber beim Upgrade
/// dazwischenkommt, ist genau das Risiko, das dieses ADR vermeiden will.
/// </para>
/// </summary>
public sealed class HostExecutionSettingFile : IHostExecutionSettingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public HostExecutionSettingFile(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, "config", "host-execution.json");
    }

    public string Location => _path;

    public HostExecutionSettingRecord? Read()
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
        catch (IOException exception)
        {
            throw new HostExecutionSettingException(
                $"Der gespeicherte Wert der Ausführungs-Policy in '{_path}' ist nicht lesbar.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new HostExecutionSettingException(
                $"Der gespeicherte Wert der Ausführungs-Policy in '{_path}' ist nicht lesbar.", exception);
        }

        try
        {
            return JsonSerializer.Deserialize<HostExecutionSettingRecord>(content, Json)
                ?? throw new HostExecutionSettingException(
                    $"Der gespeicherte Wert der Ausführungs-Policy in '{_path}' ist leer.");
        }
        catch (JsonException exception)
        {
            // Nicht „dann eben Vorgabe": Eine kaputte Datei sähe sonst aus wie eine frische Instanz,
            // und die Instanz stellte sich ohne Ansage um.
            throw new HostExecutionSettingException(
                $"Der gespeicherte Wert der Ausführungs-Policy in '{_path}' ist beschädigt.", exception);
        }
    }

    public void Write(HostExecutionSettingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        // Erst vollständig daneben schreiben, dann ersetzen. Ein Abbruch mitten im Schreiben ließe
        // sonst eine halbe Datei zurück — und die wäre beim nächsten Start „unlesbar", also nein.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
        File.Move(temporary, _path, overwrite: true);
    }
}
