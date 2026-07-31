using Bifrost.Abstractions;
using Bifrost.Core.Rbac;
using Bifrost.Persistence;
using Bifrost.Security.Tests.Infrastructure;
using Bifrost.Server.Bootstrap;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Bifrost.Security.Tests.Bootstrap;

/// <summary>Eine Uhr, die stillsteht, bis der Test sie stellt — für die Frist des Setup-Tokens.</summary>
internal sealed class SteppingTime : TimeProvider
{
    public SteppingTime(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Ein Nachweis, den der Test an- und ausschalten kann.</summary>
internal sealed class SwitchableProof : IBootstrapRecoveryProof
{
    public bool Proven { get; set; }

    public BootstrapProofResult Verify()
        => new(Proven, Proven ? "Nachweis erbracht (Testschalter)." : "Kein lokaler Zugriff (Testschalter).");
}

/// <summary>
/// Eine echte Installation auf einem Wegwerf-Datenverzeichnis: echte SQLite-Datenbank mit dem
/// Migrationspfad des Hosts, echte Zustands- und Übergabedatei, echter Erstzugangsdienst.
/// <para>
/// <b>Warum nichts davon nachgebaut wird:</b> Die Fragen dieses Pakets lauten „gilt ein Token
/// wirklich nur einmal", „gewinnt bei zwei gleichzeitigen Einlösungen genau eine" und „behält eine
/// bestehende Installation ihren Administrator". Alle drei hängen an Dateizugriff, Sperren und
/// Datenbankzeilen. Eine Attrappe wüsste die Antwort per Konstruktion.
/// </para>
/// </summary>
internal sealed class BootstrapWorld : IDisposable
{
    private readonly TestDbFactory _factory;

    public BootstrapWorld()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), $"bifrost-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DataDirectory);

        var options = new DbContextOptionsBuilder<BifrostDbContext>()
            .UseBifrostDatabase("sqlite", $"Data Source={Path.Combine(DataDirectory, "bifrost.db")}")
            .Options;
        _factory = new TestDbFactory(options);

        Time = new SteppingTime(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        Proof = new SwitchableProof();
        State = new BootstrapStateFile(DataDirectory);
        Handover = new BootstrapHandoverFile(DataDirectory);
        UiUsers = new UiUserService(_factory, Time);
        Rbac = new PersistentRbacStore(_factory, new InMemoryRbacDirectory());
        Audit = new RecordingAuditSink();
    }

    public string DataDirectory { get; }

    public SteppingTime Time { get; }

    public SwitchableProof Proof { get; }

    public BootstrapStateFile State { get; }

    public BootstrapHandoverFile Handover { get; }

    public UiUserService UiUsers { get; }

    public PersistentRbacStore Rbac { get; }

    public RecordingAuditSink Audit { get; }

    public BootstrapOptions Options { get; set; } = BootstrapOptions.Default;

    public async Task InitializeAsync()
        => await new DatabaseInitializer(_factory).InitializeAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Ein frischer Dienst auf demselben Datenverzeichnis. Bewusst je Aufruf neu: Der Zustand liegt
    /// in der Datei und in der Datenbank, nicht im Objekt — und genau das soll geprüft werden.
    /// </summary>
    public BootstrapService Service() => new(
        State, Handover, Proof, UiUsers, Rbac, Audit, Time, Options,
        NullLogger<BootstrapService>.Instance);

    /// <summary>Der Zustand, wie ihn ein Neustart vorfände.</summary>
    public BootstrapRecord? Record() => State.Read();

    public string HandoverPath => BootstrapLayout.HandoverPathFor(DataDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class TestDbFactory : IDbContextFactory<BifrostDbContext>
    {
        private readonly DbContextOptions<BifrostDbContext> _options;

        public TestDbFactory(DbContextOptions<BifrostDbContext> options) => _options = options;

        public BifrostDbContext CreateDbContext() => new(_options);
    }
}
