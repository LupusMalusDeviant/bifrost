using Bifrost.Persistence;
using Bifrost.Server.KeyRing;

using Bifrost.Security.Tests.Infrastructure;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Bifrost.Security.Tests.KeyRing;

/// <summary>
/// Eine echte Instanz auf einem Wegwerf-Datenverzeichnis: echter DataProtection-Ring, echte
/// Zertifikate, echte Dateien.
/// <para>
/// <b>Warum nichts davon nachgebaut wird:</b> Die Frage dieses Pakets lautet, ob ein Ring
/// wiedergefunden, ein Verlust erkannt und ein falsches Zertifikat bemerkt wird. Ein Attrappen-Ring
/// wüsste per Konstruktion die richtige Antwort. Was hier geprüft wird, ist das Verhalten von
/// DataProtection selbst — inklusive seiner Neigung, bei einem unlesbaren Ring einfach einen neuen
/// anzulegen.
/// </para>
/// </summary>
internal sealed class KeyRingWorld : IDisposable
{
    public KeyRingWorld()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), $"bifrost-keyring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DataDirectory);
        SecretsDirectory = Path.Combine(DataDirectory, "..", $"secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(SecretsDirectory);
    }

    public string DataDirectory { get; }

    /// <summary>Bewusst NEBEN dem Datenverzeichnis: Ein Zertifikat darin läge in jedem Backup mit.</summary>
    public string SecretsDirectory { get; }

    public string KeyRingDirectory => Bifrost.Server.KeyRing.KeyRingDirectory.PathFor(DataDirectory);

    /// <summary>Die Konfiguration dieser „Instanz" — im Serverprozess wäre das IConfiguration.</summary>
    public Dictionary<string, string> Configuration { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Value(string name) => Configuration.GetValueOrDefault(name);

    public RecordingAuditSink Audit { get; } = new();

    /// <summary>Wie viele Schlüsseldateien gerade im Ring liegen.</summary>
    public int KeyFileCount => Bifrost.Server.KeyRing.KeyRingDirectory.Read(KeyRingDirectory).Count;

    public IReadOnlyList<string> KeyIds =>
        [.. Bifrost.Server.KeyRing.KeyRingDirectory.Read(KeyRingDirectory).Select(key => key.Id)];

    /// <summary>
    /// Baut den Dienstbaum genau so, wie <c>Program.cs</c> es tut: DataProtection mit dem
    /// unveränderlichen Anwendungsnamen, Ablage im Datenverzeichnis, danach
    /// <see cref="KeyRingRegistration.AddBifrostKeyRing"/>.
    /// </summary>
    public ServiceProvider BuildServices(long? ciphertextRows = 0)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var keyRing = services.AddDataProtection()
            .SetApplicationName(CryptographicNames.DataProtectionApplication)
            .PersistKeysToFileSystem(new DirectoryInfo(KeyRingDirectory));

        services.AddBifrostKeyRing(keyRing, BuildConfiguration(), DataDirectory);
        services.AddSingleton<IKeyRingCiphertextProbe>(new StubCiphertextProbe(ciphertextRows));
        services.AddSingleton<Bifrost.Abstractions.IAuditSink>(Audit);
        services.AddSingleton(TimeProvider.System);
        return services.BuildServiceProvider();
    }

    public IConfiguration BuildConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(
            Configuration.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value))).Build();

    /// <summary>Ein Startlauf: Urteil holen, ohne den Rest des Gateways.</summary>
    public async Task<KeyRingVerdict> StartAsync(long? ciphertextRows = 0)
    {
        using var services = BuildServices(ciphertextRows);
        var startup = new KeyRingStartup(
            services.GetRequiredService<KeyRingSettings>(),
            services.GetRequiredService<KeyRingPaths>(),
            services.GetRequiredService<IKeyRingWitnessStore>(),
            services.GetRequiredService<IKeyRingCiphertextProbe>(),
            services.GetRequiredService<IDataProtectionProvider>(),
            Audit,
            TimeProvider.System,
            NullLogger<KeyRingStartup>.Instance);

        return await startup.RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Erzeugt ein Zertifikat samt Passwortdatei im Secrets-Verzeichnis.</summary>
    public KeyRingCertificateCreation CreateCertificate(string name = "keyring")
        => KeyRingCertificates.Create(
            Path.Combine(SecretsDirectory, name + ".pfx"),
            Path.Combine(SecretsDirectory, name + ".pfx.password"),
            TimeProvider.System,
            $"CN=bifrost-{name}");

    /// <summary>Stellt die Betriebsart 'file-secret' her: Zertifikat plus Passwort aus einer Datei.</summary>
    public KeyRingCertificateCreation UseFileSecret(string name = "keyring")
    {
        var created = CreateCertificate(name);
        Configuration[KeyRingSwitch.Protection] = KeyRingSwitch.FileSecretValue;
        Configuration[KeyRingSwitch.CertificatePath] = created.CertificatePath;
        Configuration[KeyRingSwitch.CertificatePassword + FileSecret.Suffix] = created.PasswordPath;
        return created;
    }

    /// <summary>Löscht das Schlüsselverzeichnis — der verlorene Volume-Inhalt.</summary>
    public void LoseKeyRing()
    {
        if (Directory.Exists(KeyRingDirectory))
        {
            Directory.Delete(KeyRingDirectory, recursive: true);
        }
    }

    /// <summary>Sichert das Schlüsselverzeichnis nach ADR-0024-Art: eine Kopie der Dateien.</summary>
    public string BackupKeyRing()
    {
        var target = Path.Combine(SecretsDirectory, $"keyring-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(KeyRingDirectory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }

    public void RestoreKeyRing(string backup)
    {
        Directory.CreateDirectory(KeyRingDirectory);
        foreach (var file in Directory.GetFiles(backup))
        {
            File.Copy(file, Path.Combine(KeyRingDirectory, Path.GetFileName(file)), overwrite: true);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
            Directory.Delete(SecretsDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class StubCiphertextProbe : IKeyRingCiphertextProbe
    {
        private readonly long? _rows;

        public StubCiphertextProbe(long? rows) => _rows = rows;

        public Task<long?> CountAsync(CancellationToken ct) => Task.FromResult(_rows);
    }
}
