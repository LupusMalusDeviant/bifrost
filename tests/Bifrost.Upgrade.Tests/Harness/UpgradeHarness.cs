using Bifrost.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Upgrade.Tests.Harness;

/// <summary>Dieselbe Fabrik, die auch der Host benutzt — nur mit Optionen aus dem Test.</summary>
internal sealed class UpgradeDbFactory : IDbContextFactory<BifrostDbContext>
{
    private readonly DbContextOptions<BifrostDbContext> _options;

    public UpgradeDbFactory(DbContextOptions<BifrostDbContext> options) => _options = options;

    public BifrostDbContext CreateDbContext() => new(_options);

    /// <summary>
    /// Ausdrücklich implementiert statt über die EF-Erweiterungsmethode: Die Typinferenz findet sie
    /// nur über den Schnittstellentyp, und der Harness reicht die Fabrik teils konkret weiter.
    /// </summary>
    public Task<BifrostDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>
/// Der Fixtureharness von WP2.6.
///
/// <para>
/// <b>Die Fixtures entstehen aus den vorhandenen EF-Migrationen</b> — ein Datenbankstand wird
/// erzeugt, indem gezielt <i>bis zu einer bestimmten Migration</i> hochgezogen wird
/// (<see cref="IMigrator.MigrateAsync(string, CancellationToken)"/>). Handgeschriebenes SQL, das
/// einen alten Stand nur nachahmt, waere hier wertlos: Es wuerde genau die Abweichung nicht zeigen,
/// wegen der man den Test schreibt.
/// </para>
///
/// <para>
/// <b>Was der Harness NICHT kann:</b> Er kennt nur Migrationsstaende, keine veroeffentlichten
/// Releases. Ein Upgrade ueber drei Releases hinweg braucht die Artefakte dieser Releases; die gibt
/// es nicht (M1 ist nicht abgenommen, es hat keinen Releaselauf gegeben). Siehe
/// <c>docs/upgrade-matrix.md</c>.
/// </para>
/// </summary>
internal static class UpgradeHarness
{
    /// <summary>
    /// Die veroeffentlichten Migrationsstaende eines Providers, in Anwendungsreihenfolge. Gelesen
    /// wird die Migrations-Assembly, nicht die Datenbank — der Aufruf oeffnet keine Verbindung und
    /// braucht deshalb auch keinen laufenden Server.
    /// </summary>
    public static IReadOnlyList<string> PublishedMigrations(string provider)
    {
        var connectionString = BifrostDbOptions.IsPostgres(provider)
            ? "Host=127.0.0.1;Port=1;Database=bifrost_katalog;Username=katalog;Password=katalog"
            : "Data Source=:memory:";

        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase(provider, connectionString)
            .Options;

        using var db = new BifrostDbContext(options);
        return [.. db.Database.GetMigrations()];
    }

    /// <summary>
    /// Zieht eine leere Datenbank auf genau den Stand <paramref name="targetMigration"/> hoch — das
    /// ist der Fixturestand, aus dem heraus das Upgrade geprueft wird.
    /// </summary>
    public static async Task CreateFixtureAsync(
        IDbContextFactory<BifrostDbContext> factory, string targetMigration, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.GetService<IMigrator>().MigrateAsync(targetMigration, ct);

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        if (applied.Count == 0 || applied[^1] != targetMigration)
        {
            throw new InvalidOperationException(
                $"Der Fixturestand '{targetMigration}' liess sich nicht herstellen; angewendet ist "
                + $"'{(applied.Count > 0 ? applied[^1] : "(nichts)")}'. Ein Fixture, das nicht auf dem "
                + "behaupteten Stand steht, prueft nichts.");
        }
    }

    /// <summary>Alle Migrationen bis einschliesslich <paramref name="targetMigration"/>.</summary>
    public static IReadOnlyList<string> Through(IReadOnlyList<string> published, string targetMigration)
    {
        var index = published.ToList().IndexOf(targetMigration);
        return index < 0
            ? throw new ArgumentOutOfRangeException(nameof(targetMigration), targetMigration, "Unbekannter Migrationsstand.")
            : published.Take(index + 1).ToList();
    }

    /// <summary>
    /// Ein DataProtection-Schluesselring auf der Platte. Bewusst kein
    /// <c>EphemeralDataProtectionProvider</c>: Beim Restore muss der Schluesselring durch das Archiv
    /// reisen, und ein Ring, den es nur im Arbeitsspeicher gibt, kann das nicht.
    /// </summary>
    public static IDataProtectionProvider KeyRing(string directory)
    {
        Directory.CreateDirectory(directory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(directory))
            // Fest gesetzt, weil der Anwendungsname in die Schluesselableitung eingeht: Quelle und
            // Ziel eines Restores muessen denselben tragen, sonst scheitert die Entschluesselung aus
            // einem Grund, der mit dem Upgrade nichts zu tun hat.
            .SetApplicationName("bifrost-upgrade-tests");

        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }
}
