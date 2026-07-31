using System.Reflection;

using Bifrost.Abstractions.Operations;

using Microsoft.EntityFrameworkCore.Migrations;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Die Migrationsstände, die <b>dieser Build</b> kennt — gelesen aus der Migrations-Assembly, ohne
/// Datenbankverbindung.
/// <para>
/// <b>Wofür:</b> Das Rückwärts-Tor aus ADR-0024 E6. Ein Restore muss vor dem Entpacken entscheiden
/// können, ob ein Archiv aus einer neueren Instanz stammt. Die Versionsangabe im Manifest taugt
/// dafür nicht — sie ist eine Behauptung des Archivs über sich selbst. Der Migrationsstand ist eine
/// Tatsache.
/// </para>
/// <para>
/// <b>Warum ohne Datenbank:</b> Der Restore läuft auch dort, wo noch keine Datenbank existiert —
/// das ist sogar sein Regelfall (ADR-0024 E5: Wiederherstellung auf eine leere Instanz). Eine
/// Prüfung, die eine Verbindung braucht, fiele genau dann aus, wenn sie gebraucht wird.
/// </para>
/// </summary>
public static class KnownMigrations
{
    /// <summary>
    /// Lädt die Migrations-Assembly des Anbieters und liest die Ids aus den
    /// <see cref="MigrationAttribute"/>-Angaben. Lässt sich die Assembly nicht laden, ist das
    /// Ergebnis <b>leer</b> — der Aufrufer meldet dann eine Warnung, statt ein ungeprüftes Archiv
    /// als geprüft auszugeben.
    /// </summary>
    public static IReadOnlySet<string> For(DatabaseProvider provider)
    {
        var assemblyName = provider is DatabaseProvider.Postgres
            ? BifrostDbOptions.PostgresMigrationsAssembly
            : BifrostDbOptions.SqliteMigrationsAssembly;

        try
        {
            return Read(Assembly.Load(assemblyName));
        }
        catch (Exception e) when (e is FileNotFoundException or BadImageFormatException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static IReadOnlySet<string> Read(Assembly migrationsAssembly)
    {
        ArgumentNullException.ThrowIfNull(migrationsAssembly);

        var ids = migrationsAssembly.GetTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!);

        return new HashSet<string>(ids, StringComparer.Ordinal);
    }
}
