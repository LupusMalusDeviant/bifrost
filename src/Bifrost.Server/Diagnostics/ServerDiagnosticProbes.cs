using Bifrost.Abstractions;
using Bifrost.Core.Diagnostics;
using Bifrost.Persistence;
using Bifrost.Persistence.Backup;

using Microsoft.EntityFrameworkCore;

namespace Bifrost.Server.Diagnostics;

/// <summary>
/// Die Datenbanksonde der Diagnose (WP2.7, Umsetzung von <see cref="IDatabaseDiagnosticProbe"/>).
/// <para>
/// <b>Warum nicht <see cref="DatabaseInitializer.InspectAsync"/>?</b> Das wurde zuerst geprüft, und
/// die Antwort ist: Der Initializer beantwortet eine andere Frage. Er liefert fertige
/// <see cref="Bifrost.Abstractions.Operations.DiagnosticCheck"/>s der <i>Startkoordination</i>
/// (BFR-DB-0100…0112: offenes Journal, unbekanntes Schema, ausstehende Migrationen) und sagt
/// nirgends, ob die Datenbank überhaupt erreichbar ist — er setzt das voraus und fliegt sonst.
/// Die Sonde hier liefert dagegen <b>Fakten</b> (erreichbar ja/nein, Namen der angewendeten und
/// ausstehenden Migrationen), aus denen BFR-DB-0002/0003/0004 ihre Befunde bilden.
/// </para>
/// <para>
/// Doppelt ist trotzdem nichts: Die Fakten kommen aus EF Core selbst
/// (<c>GetAppliedMigrations</c>/<c>GetPendingMigrations</c>), nicht aus einer nachgebauten Regel.
/// Und die Befunde des Initializers gehen nicht verloren — <see cref="ServerDiagnosticService"/>
/// hängt sie an denselben Bericht an, statt sie hier nachzubilden.
/// </para>
/// </summary>
public sealed class EfDatabaseDiagnosticProbe : IDatabaseDiagnosticProbe
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;

    public EfDatabaseDiagnosticProbe(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<DatabaseDiagnosticFacts> DescribeAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            if (!await db.Database.CanConnectAsync(ct).ConfigureAwait(false))
            {
                return new DatabaseDiagnosticFacts(
                    false, "Die Verbindung kam nicht zustande (kein Fehlertext vom Provider).");
            }

            var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
            var (version, major) = await ReadServerVersionAsync(db, ct).ConfigureAwait(false);
            return new DatabaseDiagnosticFacts(true, null, applied, pending, version, major);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Eine Sonde, die wirft, macht den ganzen Bericht kaputt; sie meldet.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Der Text ist Fremdtext und trägt bei Datenbankfehlern regelmäßig die
            // Verbindungszeichenfolge mit. Er läuft deshalb durch DiagnosticRedaction — das ist die
            // dokumentierte Aufgabe des Feldes 'Failure'.
            return new DatabaseDiagnosticFacts(false, exception.Message);
        }
    }

    /// <summary>
    /// Die Version, die der Server über sich selbst meldet — gefragt wird die offene Verbindung,
    /// nicht die Konfiguration. Für BFR-DB-0006 ist genau das der Punkt: Ob der vorhandene
    /// <c>pg_dump</c> reicht, entscheidet der Server, gegen den wirklich gearbeitet wird.
    /// <para>
    /// Ein Fehlschlag ist hier <b>kein</b> Fehler des Berichts: Die Antwort ist dann „nicht
    /// ermittelt", und der Check sagt das, statt eine Verträglichkeit zu behaupten.
    /// </para>
    /// </summary>
    private static async Task<(string? Version, int? Major)> ReadServerVersionAsync(
        BifrostDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                var version = db.Database.GetDbConnection().ServerVersion;

                // Ausgewertet wird HIER, mit dem Parser aus Bifrost.Persistence — derselbe, der auch
                // die Clientversion liest. Zwei Auswertungen derselben Schreibweise wären zwei
                // Gelegenheiten, sie verschieden zu verstehen.
                return (version, PostgresTools.ParseMajorVersion(version));
            }
            finally
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // "Nicht ermittelt" ist eine gültige Antwort; ein Absturz wäre keine.
        catch (Exception)
#pragma warning restore CA1031
        {
            return (null, null);
        }
    }
}

/// <summary>
/// Die Werkzeugsonde für BFR-DB-0006 (ADR-0024 E2): Sind <c>pg_dump</c>/<c>pg_restore</c> da, und
/// welche Hauptversion hat der gefundene Client?
/// <para>
/// Sie sucht und liest <b>nicht selbst</b>, sondern fragt <see cref="PostgresTools"/> — dieselbe
/// Stelle, die auch die Sicherung benutzt. Eine eigene Suche hier hieße: Die Diagnose beurteilt ein
/// anderes Programm als das, welches im Ernstfall läuft.
/// </para>
/// </summary>
public sealed class PostgresBackupToolProbe : IPostgresBackupToolProbe
{
    private readonly string? _binDirectory;

    /// <param name="binDirectory">
    /// Ein ausdrücklich konfiguriertes Verzeichnis (<c>BackupOptions.PostgresToolDirectory</c>);
    /// <c>null</c> heißt <c>BIFROST_POSTGRES_BIN</c>, sonst <c>PATH</c>.
    /// </param>
    public PostgresBackupToolProbe(string? binDirectory = null) => _binDirectory = binDirectory;

    public async Task<PostgresBackupToolFacts> DescribeAsync(CancellationToken ct)
    {
        if (!PostgresTools.TryLocate(_binDirectory, out var toolset) || toolset is null)
        {
            return new PostgresBackupToolFacts(false);
        }

        var major = await PostgresTools.ReadMajorVersionAsync(toolset.DumpPath, ct).ConfigureAwait(false);
        return new PostgresBackupToolFacts(true, toolset.DumpPath, major);
    }
}

/// <summary>
/// Die Upstream-Sonde (WP2.7, Umsetzung von <see cref="IUpstreamDiagnosticProbe"/>): der Zustand,
/// den der Supervisor ohnehin führt — keine zweite Zustandsmaschine.
/// </summary>
public sealed class SupervisorUpstreamDiagnosticProbe : IUpstreamDiagnosticProbe
{
    private readonly IUpstreamSupervisor _supervisor;

    public SupervisorUpstreamDiagnosticProbe(IUpstreamSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        _supervisor = supervisor;
    }

    public Task<IReadOnlyList<UpstreamDiagnosticFact>> DescribeAsync(CancellationToken ct)
    {
        IReadOnlyList<UpstreamDiagnosticFact> facts = _supervisor.Statuses
            // 'Stopped' ist eine Entscheidung, kein Befund: Wer einen Server abgeschaltet hat, will
            // dafür nicht bei jedem Diagnoselauf eine Warnung. Alles andere zählt mit.
            .Where(status => status.State is not UpstreamState.Stopped)
            .Select(status => new UpstreamDiagnosticFact(
                status.Slug,
                status.State.ToString(),
                status.State is UpstreamState.Healthy,
                status.LastError))
            .ToList();
        return Task.FromResult(facts);
    }
}
