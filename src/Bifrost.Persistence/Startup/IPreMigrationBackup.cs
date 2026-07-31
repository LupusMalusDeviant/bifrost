namespace Bifrost.Persistence.Startup;

/// <summary>
/// Was der Backupdienst wissen muss, um vor einer Migration zu sichern. Bewusst nur Angaben, die
/// ohnehin im Manifest landen — keine Verbindungszeichenfolge, kein Passwort.
/// </summary>
/// <param name="Provider">
/// <c>sqlite</c> oder <c>postgres</c> (Werte aus <see cref="BifrostDbOptions"/>). Der Dienst wählt
/// danach das Verfahren: Online-Backup-API bzw. <c>pg_dump</c> (ADR-0024 E2).
/// </param>
/// <param name="DatabaseFilePath">Bei SQLite die Datei, sonst null.</param>
/// <param name="CurrentMigrationId">Zuletzt angewendete Migration, null bei leerer Datenbank.</param>
/// <param name="PendingMigrationIds">Was gleich angewendet werden soll — nie leer, sonst gäbe es nichts zu sichern.</param>
public sealed record PreMigrationBackupContext(
    string Provider,
    string? DatabaseFilePath,
    string? CurrentMigrationId,
    IReadOnlyList<string> PendingMigrationIds);

/// <param name="Created">
/// <c>false</c> heißt: keine Sicherung entstanden. Ob das den Start abbricht, entscheidet
/// <see cref="MigrationSafetyOptions.PreMigrationBackup"/> — der Dienst trifft diese Entscheidung
/// nicht, er meldet nur.
/// </param>
/// <param name="ArchivePath">Pfad des erzeugten Archivs; wandert in das Migrationsjournal.</param>
/// <param name="SkipReason">Warum nicht gesichert wurde. Bei <see cref="Created"/> = <c>false</c> Pflicht.</param>
public sealed record PreMigrationBackupOutcome(
    bool Created,
    string? ArchivePath = null,
    string? SkipReason = null);

/// <summary>
/// Der Haken, an dem WP2.1 sein Backup einhängt (ADR-0024 E7). Der Initializer kennt bewusst
/// <b>keinen</b> Backupdienst: Er ruft dieses Interface, wenn eines registriert ist, und arbeitet
/// ohne es weiter, wenn nicht.
/// <para>
/// <b>Verdrahtung durch den Lead:</b> eine Implementierung als
/// <c>services.AddSingleton&lt;IPreMigrationBackup, …&gt;()</c> registrieren. Der
/// <see cref="DatabaseInitializer"/> nimmt sie über einen optionalen Konstruktorparameter entgegen;
/// ohne Registrierung bleibt er null. Zusätzlich sollte dann
/// <see cref="MigrationSafetyOptions.PreMigrationBackup"/> auf
/// <see cref="PreMigrationBackupRequirement.Always"/> gesetzt werden — erst damit gilt die Zusage
/// aus E7, dass vor einer schemaändernden Migration eine Sicherung <b>existiert</b>.
/// </para>
/// <para>
/// Der Aufruf erfolgt <b>unter dem Migrationslock</b> und nur, wenn tatsächlich Migrationen
/// ausstehen. Eine Implementierung darf also davon ausgehen, dass niemand sonst gleichzeitig
/// migriert; sie darf aber nicht selbst migrieren oder den Initializer erneut aufrufen.
/// </para>
/// </summary>
public interface IPreMigrationBackup
{
    Task<PreMigrationBackupOutcome> CreateAsync(PreMigrationBackupContext context, CancellationToken ct);
}
