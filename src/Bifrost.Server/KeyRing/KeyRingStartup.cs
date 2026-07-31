using System.Security.Cryptography.X509Certificates;

using Bifrost.Abstractions;
using Bifrost.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Bifrost.Server.KeyRing;

/// <summary>
/// Der Schritt, der beim Start entscheidet, ob dieser Instanz Schlüsselmaterial fehlt (WP3.3).
/// <para>
/// <b>Der Kern des Ganzen:</b> DataProtection legt bei einem fehlenden oder unlesbaren Key-Ring von
/// sich aus einen neuen an. Der Dienst kommt dann hoch, meldet „bereit" — und kann keine einzige
/// gespeicherte Zugangsdatei mehr entschlüsseln. Beim v0.11.0-Umstieg hat genau das zugeschlagen:
/// umbenanntes Volume, leere Ablage, fehlerfreier Start.
/// </para>
/// <para>
/// Dieser Schritt läuft, <b>bevor</b> irgendetwas den ersten Protector anfordert, und er bricht in
/// dieser Lage ab. Ein Abbruch mit Begründung ist der einzige Ausgang, der die Daten nicht
/// überschreibt: Sobald ein frischer Ring dasteht, ist der alte Geheimtext nicht mehr von
/// Zufallsbytes zu unterscheiden.
/// </para>
/// </summary>
public sealed partial class KeyRingStartup
{
    /// <summary>
    /// Der Rückgabewert des Prozesses, wenn der Key-Ring nicht benutzbar ist. 78 ist
    /// <c>EX_CONFIG</c> aus <c>sysexits.h</c> — eine Konfigurations-/Umgebungslage, kein Absturz.
    /// Ein Container-Neustart repariert sie nicht, und der Exit-Code sagt das.
    /// </summary>
    public const int UnusableExitCode = 78;

    private readonly KeyRingSettings _settings;
    private readonly string _keyRingDirectory;
    private readonly IKeyRingWitnessStore _witness;
    private readonly IKeyRingCiphertextProbe _ciphertext;
    private readonly IDataProtectionProvider _protection;
    private readonly IAuditSink _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<KeyRingStartup> _logger;

    public KeyRingStartup(
        KeyRingSettings settings,
        KeyRingPaths paths,
        IKeyRingWitnessStore witness,
        IKeyRingCiphertextProbe ciphertext,
        IDataProtectionProvider protection,
        IAuditSink audit,
        TimeProvider time,
        ILogger<KeyRingStartup> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(witness);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _keyRingDirectory = paths.KeyRingDirectory;
        _witness = witness;
        _ciphertext = ciphertext;
        _protection = protection;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    /// <summary>Das Urteil des letzten Laufs — für Diagnose und Oberfläche.</summary>
    public KeyRingVerdict? Verdict { get; private set; }

    /// <summary>
    /// Beurteilt die Lage, protokolliert sie und hält den Zeugeneintrag nach. Liefert das Urteil;
    /// <see cref="KeyRingVerdict.Blocks"/> heißt: nicht weiterstarten.
    /// </summary>
    public async Task<KeyRingVerdict> RunAsync(CancellationToken ct)
    {
        var keys = KeyRingDirectory.Read(_keyRingDirectory);

        KeyRingWitnessRecord? witness = null;
        var witnessUnreadable = false;
        try
        {
            witness = _witness.Read();
        }
        catch (KeyRingWitnessException exception)
        {
            witnessUnreadable = true;
            Log.WitnessUnreadable(_logger, exception.Message);
        }

        // Die Datenbank wird nur dann befragt, wenn das Dateisystem allein „frische Installation"
        // sagen würde. In jeder anderen Lage ist die Antwort schon gefallen, und eine Abfrage vor
        // der Schema-Erzeugung wäre nur ein zusätzlicher Weg, an dem etwas schiefgehen kann.
        long ciphertextRows = 0;
        var ciphertextKnown = false;
        if (keys.Count == 0 && witness is null && !witnessUnreadable)
        {
            var counted = await _ciphertext.CountAsync(ct).ConfigureAwait(false);
            ciphertextKnown = counted is not null;
            ciphertextRows = counted ?? 0;
        }

        var verdict = KeyRingJudgement.Judge(new KeyRingEvidence(
            keys.Count,
            [.. keys.Select(key => key.Id)],
            witness,
            witnessUnreadable,
            ciphertextRows,
            ciphertextKnown));

        // Lesbarkeit erst prüfen, wenn überhaupt etwas dasteht — und mit genau der Zertifikatslage,
        // mit der der Dienst gleich laufen würde. Das ist die Stelle, an der ein falsches Zertifikat
        // auffällt, BEVOR DataProtection daneben einen neuen Schlüssel anlegt.
        if (!verdict.Blocks && keys.Count > 0)
        {
            verdict = VerifyReadable(keys.Count) ?? verdict;
        }

        Verdict = verdict;
        Announce(verdict);

        if (verdict.Blocks)
        {
            return verdict;
        }

        RecordWitness(keys);
        return verdict;
    }

    /// <summary>
    /// Öffnet den vorhandenen Ring probehalber. <c>null</c> heißt „nichts einzuwenden"; sonst steht
    /// im Urteil, warum nicht.
    /// </summary>
    private KeyRingVerdict? VerifyReadable(int keyCount)
    {
        IReadOnlyList<X509Certificate2> certificates;
        try
        {
            certificates = _settings.IsProtected ? KeyRingCertificates.Load(_settings) : [];
        }
        catch (KeyRingConfigurationException exception)
        {
            return new KeyRingVerdict(
                KeyRingVerdictKind.Unreadable,
                $"Das konfigurierte Key-Ring-Zertifikat ist nicht benutzbar: {exception.Message}",
                "Der Start bricht ab. Er bricht ab, weil ein Weiterlaufen bedeutete, dass "
                + "DataProtection neben dem unlesbaren Ring einen frischen anlegt — ab da wäre "
                + "auch mit dem richtigen Zertifikat nichts mehr zu retten.");
        }

        try
        {
            var report = KeyRingProbe.Read(_keyRingDirectory, certificates);
            if (report.AllReadable)
            {
                return null;
            }

            return new KeyRingVerdict(
                KeyRingVerdictKind.Unreadable,
                $"Der vorhandene Key-Ring ({keyCount} Schlüssel) lässt sich mit der konfigurierten "
                + $"Zertifikatslage nicht öffnen: {report.Describe()}",
                _settings.IsProtected
                    ? "Zeigt " + KeyRingSwitch.CertificatePath + " auf dasselbe Zertifikat wie beim "
                        + "letzten Start? Bei einem Zertifikatswechsel muss das alte über "
                        + KeyRingSwitch.PreviousCertificatePath + " weiterhin angegeben sein — es "
                        + "verschlüsselt nichts mehr, aber ohne es bleibt das Altmaterial zu. "
                        + "Vor jedem Wechsel 'bifrost-server --keyring-rotate' laufen lassen."
                    : "Die Schlüsseldateien sind verschlüsselt, es ist aber kein Zertifikat "
                        + "konfiguriert. Wurde " + KeyRingSwitch.CertificatePath + " entfernt? "
                        + "Ohne das Zertifikat ist der Ring nicht zu öffnen.");
        }
        finally
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>
    /// Schreibt fest, was diese Instanz jetzt über ihren Ring weiß.
    /// <para>
    /// Bei einer frischen Instanz gibt es dafür noch nichts — der erste Schlüssel entsteht erst beim
    /// ersten Zugriff. Deshalb wird er hier ausdrücklich angefordert: Sonst hätte eine Instanz, die
    /// zwischen erstem Start und erstem Upstream ihr Volume verliert, keinen Zeugen — und der
    /// nächste Start sähe wieder aus wie eine Neuinstallation.
    /// </para>
    /// </summary>
    private void RecordWitness(IReadOnlyList<KeyRingKeyFile> keys)
    {
        try
        {
            if (keys.Count == 0)
            {
                // Der Aufruf, der den ersten Schlüssel entstehen lässt. Das Ergebnis wird verworfen —
                // es geht nur um das Anlegen. Erlaubt ist er hier, weil oben festgestellt wurde, dass
                // es nichts gibt, was er überschreiben könnte.
                _ = _protection
                    .CreateProtector(CryptographicNames.UpstreamConfigPurpose)
                    .Protect([0]);
                keys = KeyRingDirectory.Read(_keyRingDirectory);
            }

            if (keys.Count == 0)
            {
                Log.WitnessSkipped(_logger);
                return;
            }

            _witness.Write(new KeyRingWitnessRecord(
                KeyRingSwitch.Format(_settings.Mode),
                keys.Count,
                [.. keys.Select(key => key.Id)],
                _time.GetUtcNow(),
                "Womit diese Instanz zuletzt gestartet ist. Fehlt das Schlüsselverzeichnis beim "
                + "nächsten Start, ist dieser Eintrag der Beweis, dass es einmal eines gab — und "
                + "der Start bricht ab, statt einen leeren Ring anzulegen."));
        }
#pragma warning disable CA1031 // Ein misslungener Zeugeneintrag darf den Start nicht verhindern.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Bewusst nur eine Warnung: Der Eintrag ist eine Absicherung für den nächsten Start,
            // kein Betriebsmittel für diesen. Ihn zur Startbedingung zu machen hieße, eine Instanz
            // an einem schreibgeschützten Verzeichnis scheitern zu lassen, die sonst liefe.
            Log.WitnessNotWritten(_logger, exception.Message);
        }
    }

    private void Announce(KeyRingVerdict verdict)
    {
        var remediation = verdict.Remediation ?? string.Empty;
        var mode = KeyRingSwitch.Format(_settings.Mode);
        switch (verdict.Kind)
        {
            case KeyRingVerdictKind.Lost:
            case KeyRingVerdictKind.Unreadable:
                Log.Blocked(_logger, verdict.Kind, verdict.Summary, remediation);
                Record(verdict, "Start abgebrochen");
                break;

            case KeyRingVerdictKind.Replaced:
                Log.Replaced(_logger, verdict.Summary, remediation);
                Record(verdict, "Start fortgesetzt");
                break;

            case KeyRingVerdictKind.FreshInstance:
                Log.Fresh(_logger, mode);
                break;

            default:
                Log.Established(_logger, verdict.Summary, mode);
                break;
        }

        if (_settings.Mode is KeyRingProtectionMode.Undeclared && !verdict.Blocks)
        {
            Log.Undeclared(_logger, _keyRingDirectory, KeyRingSwitch.Protection, KeyRingSwitch.NoneValue);
        }
    }

    private void Record(KeyRingVerdict verdict, string outcome)
        => _audit.Record(new AuditEvent(
            _time.GetUtcNow(), Caller: null, CallOrigin.System, AuditEventKind.ConfigChanged,
            Server: null, Tool: null, Status: null, RedactedArguments: null,
            RequestBytes: null, ResponseBytes: null, Duration: null,
            Detail: $"Key-Ring: {verdict.Kind} — {verdict.Summary} ({outcome})."));

    private static partial class Log
    {
        [LoggerMessage(EventId = 3301, Level = LogLevel.Critical,
            Message = "Key-Ring nicht benutzbar ({Kind}). {Summary} {Remediation}")]
        public static partial void Blocked(
            ILogger logger, KeyRingVerdictKind kind, string summary, string remediation);

        [LoggerMessage(EventId = 3302, Level = LogLevel.Warning,
            Message = "Key-Ring ausgetauscht. {Summary} {Remediation}")]
        public static partial void Replaced(ILogger logger, string summary, string remediation);

        [LoggerMessage(EventId = 3303, Level = LogLevel.Information,
            Message = "Key-Ring angelegt (Betriebsart {Mode}). Diese Instanz war vorher leer.")]
        public static partial void Fresh(ILogger logger, string mode);

        [LoggerMessage(EventId = 3304, Level = LogLevel.Information,
            Message = "Key-Ring geprüft: {Summary} Betriebsart {Mode}.")]
        public static partial void Established(ILogger logger, string summary, string mode);

        [LoggerMessage(EventId = 3305, Level = LogLevel.Warning,
            Message = "Für den Key-Ring unter {Path} wurde keine Betriebsart erklärt; er liegt "
                + "unverschlüsselt und entschlüsselt sämtliche gespeicherten Upstream-Zugangsdaten. "
                + "Entweder ein Zertifikat einrichten oder den ungeschützten Betrieb ausdrücklich "
                + "wählen ({Setting}={Value}).")]
        public static partial void Undeclared(ILogger logger, string path, string setting, string value);

        [LoggerMessage(EventId = 3306, Level = LogLevel.Warning,
            Message = "Der Key-Ring-Zeugeneintrag ist vorhanden, aber nicht lesbar: {Reason}")]
        public static partial void WitnessUnreadable(ILogger logger, string reason);

        [LoggerMessage(EventId = 3307, Level = LogLevel.Warning,
            Message = "Der Key-Ring-Zeugeneintrag konnte nicht geschrieben werden: {Reason} Der "
                + "nächste Start kann einen Verlust dann nur noch am Geheimtext der Datenbank erkennen.")]
        public static partial void WitnessNotWritten(ILogger logger, string reason);

        [LoggerMessage(EventId = 3308, Level = LogLevel.Warning,
            Message = "Es entstand kein Schlüssel; der Key-Ring-Zeugeneintrag bleibt aus.")]
        public static partial void WitnessSkipped(ILogger logger);
    }
}

/// <summary>Die Orte, an denen der Key-Ring dieser Instanz liegt.</summary>
/// <param name="DataDirectory">Das Datenverzeichnis.</param>
/// <param name="KeyRingDirectory">Das Schlüsselverzeichnis darin.</param>
public sealed record KeyRingPaths(string DataDirectory, string KeyRingDirectory)
{
    public static KeyRingPaths For(string dataDirectory)
        => new(dataDirectory, KeyRing.KeyRingDirectory.PathFor(dataDirectory));
}
