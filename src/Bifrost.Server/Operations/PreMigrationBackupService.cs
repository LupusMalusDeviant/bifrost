using System.Globalization;

using Bifrost.Abstractions.Operations;
using Bifrost.Persistence;
using Bifrost.Persistence.Backup;
using Bifrost.Persistence.Startup;

namespace Bifrost.Server.Operations;

/// <summary>
/// Erfüllt ADR-0024 E7 für SQLite: <b>vor</b> einer schemaändernden Migration entsteht automatisch
/// eine Sicherung (WP2.7, Auftrag Punkt 3). Bis hierher war der Haken vorbereitet und unbesetzt —
/// der Start migrierte ohne Sicherung und schrieb eine Warnung.
/// <para>
/// Der Aufruf läuft <b>unter dem Migrationslock</b>: Niemand sonst migriert währenddessen, und die
/// SQLite-Online-Backup-API arbeitet neben laufenden Lesevorgängen. Deshalb ist hier kein eigener
/// Riegel nötig — und es wird ausdrücklich nicht migriert und nicht wiederhergestellt.
/// </para>
/// <para>
/// <b>Ein Vollbackup ist ein Geheimnis</b> (ADR-0024 E3): Es enthält den Key-Ring und damit den
/// Schlüssel zu allen gespeicherten Zugangsdaten. Es landet unter
/// <c>&lt;Datenverzeichnis&gt;/backups</c>, also im selben Schutzbereich wie die Datenbank und der
/// Key-Ring, aus denen es entsteht — es entsteht also kein neuer Ort, der zu schützen wäre. Wer es
/// trotzdem verschlüsselt haben will, setzt <c>BIFROST_BACKUP_PASSPHRASE</c>; ohne die Passphrase
/// ist die Sicherung dann allerdings wertlos, und das ist die Zusage, die man damit eingeht.
/// </para>
/// </summary>
public sealed partial class PreMigrationBackupService : IPreMigrationBackup
{
    /// <summary>Passphrase für die automatischen Sicherungen. Leer = unverschlüsselt.</summary>
    public const string PassphraseVariable = "BIFROST_BACKUP_PASSPHRASE";

    private readonly IBackupService _backup;
    private readonly BackupOptions _options;
    private readonly string? _passphrase;
    private readonly TimeProvider _time;
    private readonly ILogger<PreMigrationBackupService> _logger;

    public PreMigrationBackupService(
        IBackupService backup,
        BackupOptions options,
        IConfiguration configuration,
        TimeProvider time,
        ILogger<PreMigrationBackupService> logger)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _backup = backup;
        _options = options;
        _time = time;
        _logger = logger;
        var configured = configuration[PassphraseVariable];
        _passphrase = string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    public async Task<PreMigrationBackupOutcome> CreateAsync(
        PreMigrationBackupContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (BifrostDbOptions.IsPostgres(context.Provider))
        {
            // Kein Rückfall auf einen Zeilenexport (ADR-0024 E2). Der Dienst kann es nicht, also
            // sagt er das — statt eine Sicherung zu melden, die keine ist.
            return new PreMigrationBackupOutcome(
                false,
                null,
                "Für PostgreSQL gibt es in dieser Ausbaustufe keine Sicherung über den Backupdienst "
                + "(ADR-0024 E2, pg_dump ist nicht implementiert). Vor der Migration von Hand sichern.");
        }

        if (context.DatabaseFilePath is null)
        {
            // Datenbank im Arbeitsspeicher: Sie überlebt den Prozess nicht, also gibt es nichts zu
            // sichern — und eine erfundene Sicherung wäre schlimmer als keine.
            return new PreMigrationBackupOutcome(
                false, null, "Die Datenbank liegt im Arbeitsspeicher; es gibt keine Datei zu sichern.");
        }

        var target = Path.Combine(
            _options.PreBackupDirectory,
            string.Format(
                CultureInfo.InvariantCulture,
                "pre-migration-{0:yyyyMMdd-HHmmss}-{1}.zip",
                _time.GetUtcNow(),
                // Zwei Läufe in derselben Sekunde sind selten, aber ein Namenszusammenstoß würde die
                // Migration abbrechen — dieser Fall ist billiger zu verhindern als zu erklären.
                Guid.NewGuid().ToString("N")[..8]));

        Directory.CreateDirectory(_options.PreBackupDirectory);
        var result = await _backup
            .CreateAsync(new BackupRequest(target, BackupSections.All, _passphrase), ct)
            .ConfigureAwait(false);

        if (_passphrase is null)
        {
            Log.UnencryptedFullBackup(_logger, result.ArchivePath, PassphraseVariable);
        }

        return new PreMigrationBackupOutcome(true, result.ArchivePath);
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 2701,
            Level = LogLevel.Warning,
            Message = "Vor der Migration ist eine UNVERSCHLUESSELTE Vollsicherung entstanden: {ArchivePath}. "
                + "Sie enthaelt den DataProtection-Key-Ring und ist damit so schuetzenswert wie die "
                + "Instanz selbst (ADR-0024 E3). Verzeichnis restriktiv halten oder {Variable} setzen.")]
        public static partial void UnencryptedFullBackup(
            ILogger logger, string archivePath, string variable);
    }
}
