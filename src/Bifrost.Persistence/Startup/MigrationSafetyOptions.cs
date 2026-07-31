namespace Bifrost.Persistence.Startup;

/// <summary>Wann vor einer schemaändernden Migration eine Sicherung entstehen muss (ADR-0024 E7).</summary>
public enum PreMigrationBackupRequirement
{
    /// <summary>Nie. Nur für Testfälle und für Instanzen, die ihre Sicherung nachweislich anders fahren.</summary>
    Never = 0,

    /// <summary>
    /// Sichern, wenn ein Backupdienst verdrahtet ist; sonst warnen und weitermachen. Vorgabe, solange
    /// der Dienst aus WP2.1 nicht angeschlossen ist — die Zusage aus E7 ist damit <b>vorbereitet</b>,
    /// nicht erfüllt.
    /// </summary>
    WhenAvailable = 1,

    /// <summary>
    /// Ohne Sicherung keine Migration. Das ist der Zustand, den ADR-0024 E7 für SQLite verlangt,
    /// sobald der Backupdienst verdrahtet ist.
    /// </summary>
    Always = 2,
}

/// <summary>Punkte, an denen ein Test den Lauf unterbrechen darf. Im Produktivpfad ist das Delegate null.</summary>
public enum MigrationFailpoint
{
    /// <summary>Vor dem Erwerb des Locks.</summary>
    BeforeLock = 0,

    /// <summary>Nach dem Journaleintrag „begonnen", vor der eigentlichen Migration.</summary>
    BeforeMigrate = 1,

    /// <summary>Nach der Migration, vor dem Journaleintrag „abgeschlossen".</summary>
    AfterMigrate = 2,
}

/// <summary>
/// <b>Testinstrument.</b> Wird sie an einem Failpoint geworfen, verhält sich der Initializer, als wäre
/// der Prozess an dieser Stelle verschwunden: Das Journal bleibt offen stehen, nichts wird
/// aufgeräumt. Nur so lässt sich ein Abbruch mitten in der Migration nachstellen, ohne den
/// Testprozess wirklich zu töten. Im Produktivpfad tritt dieser Typ nie auf.
/// </summary>
public sealed class MigrationAbortSimulationException : Exception
{
    public MigrationAbortSimulationException()
        : base("Simulierter Prozessabbruch während der Migration.")
    {
    }

    public MigrationAbortSimulationException(string message)
        : base(message)
    {
    }

    public MigrationAbortSimulationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Betriebsparameter der Startkoordination. Bewusst getrennt von <see cref="PersistenceOptions"/>:
/// Diese Werte gelten für die wenigen Sekunden des Starts, nicht für den laufenden Betrieb.
/// </summary>
public sealed record MigrationSafetyOptions
{
    /// <summary>
    /// Wie lange eine Instanz auf den Migrationslock wartet, bevor sie mit
    /// <see cref="MigrationDiagnosticCodes.LockNotAcquired"/> abbricht. Eine Minute ist die Spanne, in
    /// der ein normaler Rolling-Restart durch ist; darüber hinaus zu warten würde einen echten
    /// Hänger nur verdecken.
    /// </summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Abstand zwischen zwei Versuchen, den Lock zu bekommen.</summary>
    public TimeSpan LockPollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Vorgabe: sichern, wenn ein Dienst da ist. Siehe <see cref="PreMigrationBackupRequirement"/>.</summary>
    public PreMigrationBackupRequirement PreMigrationBackup { get; init; } = PreMigrationBackupRequirement.WhenAvailable;

    /// <summary>Nur Tests. Siehe <see cref="MigrationFailpoint"/> und <see cref="MigrationAbortSimulationException"/>.</summary>
    public Func<MigrationFailpoint, CancellationToken, Task>? Failpoint { get; init; }
}
