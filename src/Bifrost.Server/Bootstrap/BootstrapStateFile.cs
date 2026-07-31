using System.Text.Json;
using System.Text.Json.Serialization;

using Bifrost.Server.KeyRing;

namespace Bifrost.Server.Bootstrap;

/// <summary>In welchem Zustand der Erstzugang dieser Installation ist.</summary>
public enum BootstrapPhase
{
    /// <summary>Es steht ein Token aus. Genau hier — und nur hier — ist ein Einlösen möglich.</summary>
    Pending = 0,

    /// <summary>Das Token wurde eingelöst. Der Erstzugang ist erledigt.</summary>
    Redeemed = 1,

    /// <summary>
    /// Die Installation hatte bereits einen Zugang, als dieser Weg eingeführt wurde (oder hat ihn
    /// auf anderem Weg bekommen). Es gab nie ein Token, und es wird auch keines geben.
    /// </summary>
    Established = 2,

    /// <summary>
    /// Es ist noch nichts geschehen: keine Ablage, kein Token, kein Zugang. Dieser Wert wird
    /// <b>nie geschrieben</b> — er beschreibt das Fehlen des Eintrags und existiert nur, damit eine
    /// Auskunft nicht „ausstehend" sagen muss, wo nichts aussteht.
    /// </summary>
    Fresh = 3,
}

/// <summary>
/// Der dauerhafte Eintrag zum Erstzugang. Er enthält <b>nie</b> das Token, nur seinen Hash — und
/// auch den nur, solange <see cref="Phase"/> auf <see cref="BootstrapPhase.Pending"/> steht.
/// </summary>
/// <param name="Phase">Der Zustand.</param>
/// <param name="TokenHash">SHA-256 des ausstehenden Tokens (Hex). Sonst <c>null</c>.</param>
/// <param name="IssuedAt">Wann das Token ausgestellt wurde.</param>
/// <param name="ExpiresAt">Wann es verfällt.</param>
/// <param name="SettledAt">Wann eingelöst beziehungsweise als bestehend erkannt.</param>
/// <param name="Note">Ein Satz, der den Eintrag ohne Codelektüre verständlich macht.</param>
public sealed record BootstrapRecord(
    BootstrapPhase Phase,
    string? TokenHash,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? SettledAt,
    string Note);

/// <summary>Der Eintrag war vorhanden, aber nicht lesbar.</summary>
public sealed class BootstrapStateException : InvalidOperationException
{
    public BootstrapStateException()
        : base("Der Erstzugangs-Eintrag ist nicht lesbar.")
    {
    }

    public BootstrapStateException(string message)
        : base(message)
    {
    }

    public BootstrapStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Lesen, Schreiben und — für das Einlösen entscheidend — <b>austauschen</b>.</summary>
public interface IBootstrapStateStore
{
    /// <summary>Wo der Eintrag liegt.</summary>
    string Location { get; }

    /// <summary>Der Eintrag, oder <c>null</c>, wenn noch keiner geschrieben wurde.</summary>
    /// <exception cref="BootstrapStateException">Wenn einer dasteht, aber nicht lesbar ist.</exception>
    BootstrapRecord? Read();

    void Write(BootstrapRecord record);

    /// <summary>
    /// Lesen, entscheiden, schreiben — <b>unter Ausschluss aller anderen</b>. Liefert <c>true</c>,
    /// wenn <paramref name="transform"/> einen neuen Eintrag geliefert und dieser die Ablage
    /// erreicht hat.
    /// <para>
    /// Das ist die Stelle, an der die Einmaligkeit des Tokens entsteht. Zwei gleichzeitige
    /// Einlösungen sehen dieselbe Datei; nur eine von beiden sieht sie <i>noch</i> im Zustand
    /// „ausstehend", weil die andere sie zwischenzeitlich verändert hat. Ohne diesen Ausschluss
    /// gewönnen beide.
    /// </para>
    /// </summary>
    bool Exchange(Func<BootstrapRecord?, BootstrapRecord?> transform);
}

/// <summary>
/// Der Eintrag als Datei im Datenverzeichnis, nach dem Vorbild von <c>KeyRingWitnessFile</c> und
/// <c>InstanceIdentityFile</c>.
/// <para>
/// <b>Warum eine Datei und keine Tabelle:</b> Eine Tabelle hätte eine EF-Migration verlangt, und
/// eine Migration für einen einzeiligen Zustand ist ein hoher Preis. Wichtiger noch: Der Eintrag
/// gehört zur <i>Installation</i>, nicht zu ihren Daten — dieselbe Zuordnung wie bei der Instanz-Id
/// und beim Key-Ring-Zeugen. Wird ein Datenverzeichnis weggeworfen, ist auch der Erstzugang wieder
/// offen, und genau das ist richtig.
/// </para>
/// <para>
/// Die Datei bekommt trotzdem die restriktiven Rechte aus WP3.3. Sie trägt kein Geheimnis, aber wer
/// sie <b>schreiben</b> kann, kann den Zustand auf „ausstehend" zurückdrehen.
/// </para>
/// </summary>
public sealed class BootstrapStateFile : IBootstrapStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Der Sperrkonflikt zweier Prozesse dauert einen Schreibvorgang lang. Kurz warten und erneut
    // versuchen ist hier richtig; aufgeben nach einer Sekunde ebenfalls — ein Halten darüber hinaus
    // ist kein Konflikt mehr, sondern eine hängende Sperre, und die soll auffallen.
    private const int LockAttempts = 40;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly string _path;
    private readonly Lock _inProcess = new();

    public BootstrapStateFile(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = BootstrapLayout.StatePathFor(dataDirectory);
    }

    public string Location => _path;

    public BootstrapRecord? Read()
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
            throw new BootstrapStateException(
                $"Der Erstzugangs-Eintrag in '{_path}' ist nicht lesbar.", exception);
        }

        // Eine leere Datei ist kein beschädigter Eintrag, sondern gar keiner: Sie entsteht, wenn
        // ein Austausch die Datei exklusiv geöffnet, aber nichts zu schreiben gefunden hat.
        return string.IsNullOrWhiteSpace(content) ? null : Parse(content);
    }

    public void Write(BootstrapRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_inProcess)
        {
            EnsureDirectory();
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
            File.Move(temporary, _path, overwrite: true);
            SecretFilePermissions.Restrict(_path);
        }
    }

    public bool Exchange(Func<BootstrapRecord?, BootstrapRecord?> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        // Zwei Ebenen, weil es zwei Wettläufe gibt: zwei Anfragen im selben Prozess (der
        // Regelfall) und der Serverprozess gegen ein nebenher laufendes Kommando. Das Schloss
        // erledigt den ersten ohne Dateizugriff, die exklusive Datei den zweiten.
        lock (_inProcess)
        {
            EnsureDirectory();
            using var stream = OpenExclusive();

            using var reader = new StreamReader(stream, leaveOpen: true);
            var content = reader.ReadToEnd();
            var current = string.IsNullOrWhiteSpace(content) ? null : Parse(content);

            var next = transform(current);
            if (next is null)
            {
                return false;
            }

            stream.SetLength(0);
            stream.Position = 0;
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write(JsonSerializer.Serialize(next, Json));
            }

            stream.Flush(flushToDisk: true);
            SecretFilePermissions.Restrict(_path);
            return true;
        }
    }

    private FileStream OpenExclusive()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(
                    _path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < LockAttempts)
            {
                Thread.Sleep(LockRetryDelay);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new BootstrapStateException(
                    $"Der Erstzugangs-Eintrag in '{_path}' liess sich nicht exklusiv oeffnen.", exception);
            }
        }
    }

    private void EnsureDirectory() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

    private BootstrapRecord Parse(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<BootstrapRecord>(content, Json)
                ?? throw new BootstrapStateException(
                    $"Der Erstzugangs-Eintrag in '{_path}' ist leer.");
        }
        catch (JsonException exception)
        {
            // Nicht „dann eben frische Instanz": Eine beschädigte Datei sähe sonst aus wie eine
            // Neuinstallation — und die Neuinstallation ist genau der Weg, der ein neues Token
            // ausgibt. Auf einer Installation mit bestehenden Admins wäre das ein Zweitschlüssel
            // aus einem Lesefehler.
            throw new BootstrapStateException(
                $"Der Erstzugangs-Eintrag in '{_path}' ist beschaedigt.", exception);
        }
    }
}
