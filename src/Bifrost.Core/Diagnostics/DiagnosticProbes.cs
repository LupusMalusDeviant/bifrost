using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Bifrost.Core.Diagnostics;

/// <summary>
/// Die Berührungspunkte der Diagnose mit der Außenwelt. Sie stehen hier als Schnittstellen, damit
/// jeder Check gegen erfundene Zustände prüfbar ist — ein „Verzeichnis nicht beschreibbar" lässt
/// sich auf einem Entwicklerrechner sonst nicht herstellen, und ein Test, der Docker startet, ist
/// kein Test.
/// </summary>
public interface IFileProbe
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>Dateien eines Verzeichnisses; leer, wenn es nicht existiert oder nicht lesbar ist.</summary>
    IReadOnlyList<string> ListFiles(string path, string searchPattern);

    /// <summary>
    /// <c>null</c>, wenn geschrieben werden darf, sonst der Grund.
    /// <para>
    /// Es gibt keinen verlässlichen rein lesenden Weg, das zu beantworten — Rechte, ACLs und
    /// Mount-Optionen widersprechen sich. Die Umsetzung legt deshalb eine Datei an und löscht sie
    /// sofort wieder. Das ist der einzige Seiteneffekt der gesamten Diagnose und er ist auf das
    /// Datenverzeichnis beschränkt.
    /// </para>
    /// </summary>
    string? ProbeWritable(string path);
}

/// <summary>Ist der konfigurierte Port frei?</summary>
public interface IPortProbe
{
    PortState Inspect(int port);
}

public enum PortState
{
    Free,
    Occupied,

    /// <summary>Nicht beantwortbar (fehlende Rechte, kein Netzstack) — ausdrücklich kein „frei".</summary>
    Unknown,
}

/// <summary>Ein externes Programm einmal aufrufen und seine Ausgabe lesen.</summary>
public interface IProcessProbe
{
    Task<ProcessProbeResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <param name="Started">Falsch, wenn das Programm gar nicht erst gefunden oder gestartet wurde.</param>
public sealed record ProcessProbeResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string? Failure);

/// <summary>
/// Was nur jemand mit Datenbankzugang beantworten kann. <b>Bifrost.Core kennt kein EF Core</b> —
/// die Umsetzung liegt beim Server (WP2.7). Ohne sie melden die BFR-DB-Checks
/// <see cref="Bifrost.Abstractions.Operations.CheckStatus.Skipped"/> mit Begründung, statt still zu
/// bestehen.
/// </summary>
public interface IDatabaseDiagnosticProbe
{
    Task<DatabaseDiagnosticFacts> DescribeAsync(CancellationToken ct);
}

/// <param name="Failure">
/// Fremdtext. Er läuft durch die Redaktion, weil Datenbankausnahmen die Verbindungszeichenfolge
/// mitführen.
/// </param>
/// <param name="ServerVersion">
/// Die Version, die der Server über sich meldet (<c>17.10</c>). <c>null</c> heißt „nicht ermittelt"
/// — nie „egal".
/// </param>
/// <param name="ServerMajorVersion">
/// Dieselbe Angabe als Hauptversion. Sie kommt <b>ausgewertet</b> von der Sonde und wird hier nicht
/// noch einmal aus dem Text geschnitten: Zwei Auswertungen derselben Zeichenkette sind zwei
/// Gelegenheiten, sie verschieden zu verstehen.
/// </param>
public sealed record DatabaseDiagnosticFacts(
    bool CanConnect,
    string? Failure = null,
    IReadOnlyList<string>? AppliedMigrations = null,
    IReadOnlyList<string>? PendingMigrations = null,
    string? ServerVersion = null,
    int? ServerMajorVersion = null);

/// <summary>
/// Die Lage der PostgreSQL-Sicherungswerkzeuge auf <b>diesem</b> Rechner (ADR-0024 E2).
/// <para>
/// <b>Warum eine eigene Sonde:</b> Bifrost.Core kennt <c>PostgresTools</c> nicht — das Suchen der
/// Programme und das Lesen ihrer Version stehen in Bifrost.Persistence, und dort sollen sie auch
/// bleiben. Der Check hier vergleicht nur noch zwei Zahlen; eine zweite Suchlogik in Core wäre eine
/// zweite Wahrheit darüber, welches <c>pg_dump</c> überhaupt gemeint ist.
/// </para>
/// </summary>
public interface IPostgresBackupToolProbe
{
    Task<PostgresBackupToolFacts> DescribeAsync(CancellationToken ct);
}

/// <param name="Located">Sind <c>pg_dump</c> und <c>pg_restore</c> beide erreichbar?</param>
/// <param name="DumpPath">Der Pfad des gefundenen <c>pg_dump</c>; <c>null</c>, wenn keines da ist.</param>
/// <param name="ClientMajorVersion">
/// Die Hauptversion des gefundenen Clients. <c>null</c> heißt „nicht lesbar" — der Check meldet das
/// dann und behauptet keine Verträglichkeit.
/// </param>
public sealed record PostgresBackupToolFacts(
    bool Located,
    string? DumpPath = null,
    int? ClientMajorVersion = null);

/// <summary>Zustände der Upstreams — kennt nur der laufende Serverprozess (WP2.7).</summary>
public interface IUpstreamDiagnosticProbe
{
    Task<IReadOnlyList<UpstreamDiagnosticFact>> DescribeAsync(CancellationToken ct);
}

public sealed record UpstreamDiagnosticFact(string Slug, string State, bool Healthy, string? Failure = null);

// ── Umsetzungen gegen das echte System ──────────────────────────────────────────────────────────

/// <summary>Dateisystem-Zugriff des laufenden Prozesses.</summary>
public sealed class SystemFileProbe : IFileProbe
{
    public static SystemFileProbe Instance { get; } = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> ListFiles(string path, string searchPattern)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetFiles(path, searchPattern) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? ProbeWritable(string path)
    {
        var probeFile = Path.Combine(path, $".bifrost-doctor-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write))
            {
                stream.WriteByte(0);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return exception.Message;
        }
        finally
        {
            try
            {
                File.Delete(probeFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Konnte die Probe nicht angelegt werden, gibt es auch nichts zu löschen. Ein
                // Fehler beim Aufräumen darf die Diagnose nicht zum Absturz bringen.
            }
        }
    }
}

/// <summary>
/// Belegtprüfung über einen Bindeversuch auf der Loopback-Adresse. Bewusst kein Verbindungsversuch:
/// Ein <c>connect</c> auf einen fremden Dienst wäre ein Zugriff nach außen, ein <c>bind</c> ist nur
/// eine Frage an den eigenen Netzstack.
/// </summary>
public sealed class SystemPortProbe : IPortProbe
{
    public static SystemPortProbe Instance { get; } = new();

    public PortState Inspect(int port)
    {
        if (port is <= 0 or > 65535)
        {
            return PortState.Unknown;
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return PortState.Free;
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            return PortState.Occupied;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            return PortState.Unknown;
        }
    }
}

/// <summary>Startet ein Programm, liest stdout und beendet es hart, wenn die Frist abläuft.</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public static SystemProcessProbe Instance { get; } = new();

    public async Task<ProcessProbeResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return new ProcessProbeResult(false, -1, string.Empty, $"'{fileName}' ließ sich nicht starten.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var output = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return new ProcessProbeResult(true, process.ExitCode, output, null);
        }
        catch (OperationCanceledException)
        {
            return new ProcessProbeResult(
                true, -1, string.Empty,
                $"'{fileName}' hat binnen {timeout.TotalSeconds:0.#} s nicht geantwortet.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return new ProcessProbeResult(false, -1, string.Empty, exception.Message);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    // Schon beendet oder nicht mehr erreichbar — beides ist hier in Ordnung.
                }

                process.Dispose();
            }
        }
    }
}

/// <summary>Kleine Hilfe für Zahlenausgaben in Details, damit sie überall gleich aussehen.</summary>
internal static class DetailFormat
{
    public static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string YesNo(bool value) => value ? "ja" : "nein";
}
