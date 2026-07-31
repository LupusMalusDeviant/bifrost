using Bifrost.Server.KeyRing;

namespace Bifrost.Server.Bootstrap;

/// <summary>Der Ausgabeweg für das Token, wenn niemand an einer Konsole sitzt.</summary>
public interface IBootstrapHandover
{
    /// <summary>Wo die Übergabedatei liegt.</summary>
    string Location { get; }

    /// <summary>
    /// Schreibt das Token in die Übergabedatei und schirmt sie ab. Liefert die vorgefundene
    /// Rechtelage — <c>Restricted == false</c> heißt: geschrieben, aber nicht nachweislich
    /// abgeschirmt, und das gehört gesagt.
    /// </summary>
    SecretFilePermissionState Write(string token, DateTimeOffset expiresAt);

    /// <summary>Entfernt die Datei. Nach dem Einlösen und nach dem Ablauf.</summary>
    void Remove();
}

/// <summary>
/// Die Übergabedatei — <c>config/bootstrap-token.txt</c>, Rechte wie beim privaten Schlüssel aus
/// WP3.3 (Unix 0600, Windows ACL ohne Vererbung).
/// <para>
/// <b>Warum es sie gibt.</b> Ein Adminpasswort im Anwendungslog ist ein Geheimnis an genau dem Ort,
/// den man weitergibt, wenn etwas nicht funktioniert. Diese Datei ist der Gegenentwurf: Sie liegt
/// im Datenverzeichnis, sie ist auf den Dienstbenutzer beschränkt, sie taucht in keinem Logarchiv
/// auf — und sie <b>verschwindet</b>, sobald das Token eingelöst ist oder seine Frist abgelaufen
/// ist. Ein Logeintrag tut nichts davon.
/// </para>
/// <para>
/// <b>Was sie nicht ist.</b> Sie ist keine Ablage. Die dauerhafte Ablage ist
/// <see cref="BootstrapStateFile"/>, und dort steht nur der Hash. Diese Datei ist ein Zettel, der
/// nach Gebrauch weggeworfen wird — deshalb liest der Erstzugangsdienst sie auch nie wieder ein.
/// </para>
/// </summary>
public sealed class BootstrapHandoverFile : IBootstrapHandover
{
    private readonly string _path;

    public BootstrapHandoverFile(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = BootstrapLayout.HandoverPathFor(dataDirectory);
    }

    public string Location => _path;

    public SecretFilePermissionState Write(string token, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Erst die leere Datei anlegen und abschirmen, dann den Inhalt schreiben: Andersherum
        // stünde das Token für einen Wimpernschlag mit der Standardmaske auf der Platte, und genau
        // dieser Wimpernschlag ist der Fehler, den WP3.3 bei PFX-Dateien beschreibt.
        using (new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
        }

        SecretFilePermissions.Restrict(_path);
        File.WriteAllText(_path, Content(token, expiresAt));

        return SecretFilePermissions.Describe(_path);
    }

    public void Remove()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ein nicht löschbarer Zettel ist ein Betriebsmangel, kein Grund, den Start abzubrechen.
            // Das Token darin ist zu diesem Zeitpunkt bereits entwertet — die Datei ist Altpapier.
        }
    }

    private static string Content(string token, DateTimeOffset expiresAt) =>
        $"""
         B.I.F.R.O.S.T — Erstzugang

         Dieses Token richtet EINEN Administrator ein. Es gilt einmal und nur bis:
           {expiresAt:u}

         Token:
           {token}

         So einlösen: die Web-UI aufrufen und dem Hinweis "Erstzugang einrichten" folgen
         (/setup). Dort werden Benutzername und Passwort selbst gewählt.

         Diese Datei wird nach dem Einlösen automatisch entfernt. Wer sie vorher wegwirft,
         holt sich mit '--bootstrap-init' ein neues Token.

         """;
}
