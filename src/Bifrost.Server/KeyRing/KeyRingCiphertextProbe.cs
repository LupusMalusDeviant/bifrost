using Bifrost.Persistence;

using Microsoft.EntityFrameworkCore;

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
