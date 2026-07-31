namespace Bifrost.Server.Bootstrap;

/// <summary>Das Ergebnis eines Nachweisversuchs.</summary>
/// <param name="Proven">Ist der Nachweis erbracht?</param>
/// <param name="Description">Ein Satz für Meldungen — woran es lag beziehungsweise woran es hing.</param>
public sealed record BootstrapProofResult(bool Proven, string Description);

/// <summary>
/// Der <b>lokale Recovery-Nachweis</b>: die Bedingung, unter der eine Installation mit bestehenden
/// Zugängen ein zweites Mal ein Setup-Token bekommen darf.
/// </summary>
public interface IBootstrapRecoveryProof
{
    BootstrapProofResult Verify();
}

/// <summary>
/// Der Nachweis ist <b>Schreibzugriff auf das Datenverzeichnis dieser Installation</b>.
/// <para>
/// <b>Warum ausgerechnet das.</b> Es geht nicht darum, eine zusätzliche Hürde zu erfinden, sondern
/// die richtige zu benennen. Wer in das Datenverzeichnis schreiben kann, kann die Datenbank
/// austauschen, den Key-Ring löschen und den Dienst mit einem leeren Volume neu starten — dann
/// bekäme er ohnehin einen frischen Erstzugang. Ein weiteres „Geheimnis" davorzuhängen schützte
/// also niemanden; es täuschte nur Schutz vor. Was der Nachweis hingegen zuverlässig ausschließt,
/// ist der Weg, um den es geht: <b>über das Netz</b>. Ein Angreifer am HTTP-Endpunkt hat diesen
/// Zugriff nicht, und deshalb gibt es dort auch keinen Weg zu einem zweiten Token.
/// </para>
/// <para>
/// Geprüft wird durch Tun statt durch Fragen: Eine Probedatei wird angelegt, zurückgelesen und
/// wieder entfernt. <see cref="Directory.Exists"/> beantwortet die Frage nicht — ein
/// schreibgeschütztes Volume sieht genauso aus.
/// </para>
/// </summary>
public sealed class DataDirectoryRecoveryProof : IBootstrapRecoveryProof
{
    private readonly string _dataDirectory;

    public DataDirectoryRecoveryProof(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = dataDirectory;
    }

    public BootstrapProofResult Verify()
    {
        var probe = Path.Combine(
            _dataDirectory, "config", $".bootstrap-probe-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            var expected = Guid.NewGuid().ToString("N");
            File.WriteAllText(probe, expected);
            var actual = File.ReadAllText(probe);

            return string.Equals(actual, expected, StringComparison.Ordinal)
                ? new BootstrapProofResult(
                    true,
                    $"Schreibzugriff auf '{_dataDirectory}' nachgewiesen — das ist lokaler Zugriff "
                    + "auf diese Installation.")
                : new BootstrapProofResult(
                    false,
                    $"Die Probedatei in '{_dataDirectory}' liess sich schreiben, kam aber veraendert "
                    + "zurueck. Der Zustand des Datenverzeichnisses ist unklar.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new BootstrapProofResult(
                false,
                $"Kein Schreibzugriff auf '{_dataDirectory}': {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Eine liegengebliebene Probedatei ist Altpapier, kein Grund zum Abbruch.
            }
        }
    }
}
