using Bifrost.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Bifrost.Server.KeyRing;

/// <summary>
/// Zählt die Datensätze, die nur mit einem Key-Ring entstanden sein können.
/// <para>
/// <b>Warum das gebraucht wird:</b> Der Zeugeneintrag liegt im Datenverzeichnis und teilt damit
/// dessen Schicksal — verschwindet das Volume, verschwindet er mit. Genau in diesem Fall ist die
/// Datenbank aber oft noch da (PostgreSQL auf einem anderen Volume, oder eine zurückgespielte
/// SQLite-Datei), und ihr Geheimtext ist dann der einzige verbliebene Beweis dafür, dass es hier
/// einmal einen Ring gab.
/// </para>
/// </summary>
public interface IKeyRingCiphertextProbe
{
    /// <summary>
    /// Die Zahl der Datensätze mit Geheimtext, oder <c>null</c>, wenn die Frage nicht beantwortbar
    /// war. <b>Nicht beantwortbar ist ausdrücklich nicht dasselbe wie „keine".</b> Eine frische
    /// Datenbank hat die Tabellen noch nicht; das ist kein Beweis, sondern die Abwesenheit eines
    /// Beweises, und daraus darf kein „alles in Ordnung" werden.
    /// </summary>
    Task<long?> CountAsync(CancellationToken ct);
}

/// <summary>Die Zählung über EF Core.</summary>
public sealed class EfKeyRingCiphertextProbe : IKeyRingCiphertextProbe
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;

    public EfKeyRingCiphertextProbe(IDbContextFactory<BifrostDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public async Task<long?> CountAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // ── BFR-F-0047-Nachbar: BFR-F-0052 ───────────────────────────────────────────────
            // Diese Prüfung läuft VOR dem Schemaaufbau — beim Erststart gibt es die Tabellen also
            // noch nicht. Bis hierher wurde trotzdem gezählt und der Fehlschlag unten gefangen.
            // Zwei Folgen, beide unerwünscht:
            //
            //   1. Ein fehlerfreier Erststart schrieb zwei Zeilen auf Stufe 'Error' samt
            //      Ausnahmetext. Wer sein Log auf Fehler alarmiert, wird von der gesunden
            //      Erstinstallation geweckt — und Fehlermeldungen im Normalbetrieb entwerten die
            //      Stufe für den Fall, in dem sie etwas bedeutet.
            //   2. Schwerer: Der fehlgeschlagene Zugriff LEGTE BEI SQLITE DIE DATENBANKDATEI AN.
            //      Damit war die Existenzprüfung des DatabaseInitializer ("lag die Datenbank schon
            //      vorher da?") ab dem allerersten Start unbrauchbar — genau die Prüfung, an der
            //      er eine bestehende Alt-Installation von einer neuen unterscheidet.
            //
            // Existiert die Datenbank nicht, ist die Antwort ohne Abfrage bekannt: Es gibt keinen
            // Geheimtext, den ein verlorener Key-Ring unlesbar machen könnte.
            if (!await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(ct).ConfigureAwait(false))
            {
                return 0;
            }

            // Genau die drei Ablagen, deren Inhalt über IDataProtector geht: die
            // Upstream-Konfiguration (Zugangsdaten), die OAuth-Token und die Webhook-Secrets.
            long total = await db.ConfigVersions.LongCountAsync(ct).ConfigureAwait(false);
            total += await db.UpstreamOAuthTokens.LongCountAsync(ct).ConfigureAwait(false);
            total += await db.Webhooks.LongCountAsync(ct).ConfigureAwait(false);
            return total;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Jeder Fehlschlag heißt hier dasselbe: nicht beantwortbar.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
