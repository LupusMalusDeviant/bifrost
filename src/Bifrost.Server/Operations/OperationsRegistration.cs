using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Configuration;
using Bifrost.Core.Diagnostics;
using Bifrost.Core.Diagnostics.Upstreams;
using Bifrost.Persistence;
using Bifrost.Persistence.Backup;
using Bifrost.Persistence.Startup;
using Bifrost.Server.Diagnostics;

using Microsoft.Data.Sqlite;

namespace Bifrost.Server.Operations;

/// <summary>
/// Verdrahtung der Betriebsdienste aus M2 (WP2.7). Bis hierher waren sie gebaut, aber von keinem
/// Aufrufweg erreichbar: kein Backup, kein Restore, keine Diagnose, kein Konfigurationsexport.
/// </summary>
public static class OperationsRegistration
{
    /// <param name="dataDirectory"><c>BIFROST_DATA_DIR</c>.</param>
    /// <param name="databaseProvider"><c>BIFROST_DB_PROVIDER</c>.</param>
    /// <param name="connectionString">
    /// Die <b>tatsächlich</b> verwendete Verbindungszeichenfolge. Daraus kommt der Pfad der
    /// SQLite-Datei — eine aus dem Datenverzeichnis geratene Vorgabe hätte bei gesetztem
    /// <c>BIFROST_DB_CONNECTION</c> die falsche Datei gesichert.
    /// </param>
    public static IServiceCollection AddBifrostOperations(
        this IServiceCollection services,
        string dataDirectory,
        string databaseProvider,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        var provider = BifrostDbOptions.IsPostgres(databaseProvider)
            ? DatabaseProvider.Postgres
            : DatabaseProvider.Sqlite;
        var sqliteFile = provider is DatabaseProvider.Sqlite ? ResolveSqliteFile(connectionString) : null;

        var backupOptions = new BackupOptions
        {
            DataDirectory = dataDirectory,
            Provider = provider,
            SqliteFilePath = sqliteFile,

            // pg_dump und pg_restore sichern GENAU die Datenbank, gegen die der Server läuft — nicht
            // eine aus dem Datenverzeichnis geratene.
            PostgresConnectionString = provider is DatabaseProvider.Postgres ? connectionString : null,

            // ADR-0024 E6: Woran der Restore erkennt, dass ein Archiv aus einer neueren Instanz
            // stammt. Ohne diese Menge kann er es nicht erkennen und sagt das auch.
            KnownMigrationIds = KnownMigrations.For(provider),
        };

        services.AddSingleton(backupOptions);

        // Singleton ist hier Vertrag, nicht Geschmack: Der Restore-Plan trägt ein Handle, dessen
        // Zustand beim Dienst bleibt (M2-Vertrag, Nachtrag zum Handle). Zwei Instanzen hießen: Der
        // Plan aus dem einen Aufruf ist im nächsten unbekannt.
        services.AddSingleton<IBackupService>(_ => new BackupService(backupOptions));
        services.AddSingleton<IRestoreService>(sp => new RestoreService(
            backupOptions, sp.GetRequiredService<IBackupService>()));

        // ADR-0024 E7: Vor einer schemaändernden Migration entsteht automatisch eine Sicherung.
        // 'Always' heißt: ohne Sicherung keine Migration.
        services.AddSingleton<IPreMigrationBackup, PreMigrationBackupService>();
        services.AddSingleton(new MigrationSafetyOptions
        {
            PreMigrationBackup = RequirePreMigrationBackup(provider, sqliteFile)
                ? PreMigrationBackupRequirement.Always
                : PreMigrationBackupRequirement.WhenAvailable,
        });

        // ── Diagnose (WP2.4) mit den Sonden, die nur der Serverprozess bedienen kann ────────────
        services.AddSingleton<IDatabaseDiagnosticProbe, EfDatabaseDiagnosticProbe>();
        services.AddSingleton<IUpstreamDiagnosticProbe, SupervisorUpstreamDiagnosticProbe>();
        services.AddSingleton<ServerDiagnosticContextFactory>();
        services.AddSingleton<IDiagnosticService, ServerDiagnosticService>();

        // ── Upstream-Zeitlinie (WP4.6) ─────────────────────────────────────────────────────────
        // Der Verbindungstest bekommt Stufen mit eigenen Codes. Er baut nichts nach: Stufe 1 und 2
        // sind Validator und Torposten, die Stufen 5-7 laufen ueber den vorhandenen
        // IUpstreamConnectionTester — also ueber genau den Weg, den auch AddAsync geht.
        services.AddSingleton<IUpstreamNegotiationProbe, SupervisorNegotiationProbe>();
        services.AddSingleton<IUpstreamConnectionDiagnostics>(sp => new UpstreamConnectionDiagnostics(
            sp.GetRequiredService<IUpstreamConnectionTester>(),
            sp.GetRequiredService<IHostExecutionPolicy>(),
            negotiation: sp.GetRequiredService<IUpstreamNegotiationProbe>(),
            timeProvider: sp.GetRequiredService<TimeProvider>(),
            logger: sp.GetRequiredService<ILogger<UpstreamConnectionDiagnostics>>()));

        // ── Konfigurationsexport (WP2.5) ───────────────────────────────────────────────────────
        services.AddSingleton<ServerConfigurationPorts>();
        services.AddSingleton<IConfigurationSnapshotSource>(
            sp => sp.GetRequiredService<ServerConfigurationPorts>());
        services.AddSingleton<IConfigurationImportTarget>(
            sp => sp.GetRequiredService<ServerConfigurationPorts>());
        services.AddSingleton<IConfigurationExportService>(sp => new ConfigurationExportService(
            sp.GetRequiredService<IConfigurationSnapshotSource>(),
            sp.GetRequiredService<IConfigurationImportTarget>(),
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }

    /// <summary>
    /// Die Entscheidung zu ADR-0024 E7: Darf der Start auf einer Sicherung <b>bestehen</b>?
    /// <para>
    /// <b>SQLite:</b> ja, sobald eine Datei dahintersteht. Für eine Datenbank im Arbeitsspeicher
    /// gibt es nichts zu sichern.
    /// </para>
    /// <para>
    /// <b>PostgreSQL:</b> ja — <b>aber nur, wenn <c>pg_dump</c> und <c>pg_restore</c> wirklich
    /// erreichbar sind.</b> Das ist der eigentliche Punkt dieser Methode. 'Always' bedeutet „ohne
    /// Sicherung keine Migration"; auf einer Instanz ohne die Werkzeuge wäre das kein Schutz,
    /// sondern ein Startverbot: Der Server käme nach einem Upgrade nicht mehr hoch, und zwar aus
    /// einem Grund, der mit seinen Daten nichts zu tun hat. Deshalb wird hier <b>gemessen statt
    /// angenommen</b> — einmal beim Zusammenbau, mit Dateisystemzugriffen und ohne Prozessstart.
    /// </para>
    /// <para>
    /// Fehlen die Werkzeuge, bleibt es bei <c>WhenAvailable</c>: Der Start warnt und migriert. Das
    /// ist derselbe Zustand wie vorher — nur ist er jetzt behebbar, indem man das Clientpaket
    /// installiert, statt auf eine Ausbaustufe zu warten.
    /// </para>
    /// </summary>
    private static bool RequirePreMigrationBackup(DatabaseProvider provider, string? sqliteFile)
        => provider is DatabaseProvider.Postgres
            ? PostgresTools.TryLocate(out _)
            : sqliteFile is not null;

    /// <summary>
    /// Der Dateipfad hinter einer SQLite-Verbindungszeichenfolge; <c>null</c> für eine Datenbank im
    /// Arbeitsspeicher oder eine Angabe, die sich nicht lesen lässt.
    /// </summary>
    private static string? ResolveSqliteFile(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (builder.Mode is SqliteOpenMode.Memory
                || string.IsNullOrWhiteSpace(builder.DataSource)
                || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.GetFullPath(builder.DataSource);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
