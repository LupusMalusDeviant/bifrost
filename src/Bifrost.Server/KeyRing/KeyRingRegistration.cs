using Microsoft.AspNetCore.DataProtection;

namespace Bifrost.Server.KeyRing;

/// <summary>
/// Verdrahtung des Key-Ring-Schutzes (WP3.3).
/// <para>
/// <b>Was hier bewusst NICHT steht:</b> <c>SetApplicationName</c> und
/// <c>PersistKeysToFileSystem</c>. Beide bleiben in <c>Program.cs</c>, zusammen mit dem
/// Warnhinweis daneben — der Anwendungsname geht in die Schlüsselableitung ein, und ein Hinweis,
/// der von der Zeile weg wandert, auf die er sich bezieht, ist bald keiner mehr.
/// </para>
/// </summary>
public static class KeyRingRegistration
{
    /// <summary>
    /// Löst die Betriebsart auf, hängt gegebenenfalls die Zertifikate ein und meldet die Dienste an,
    /// die den Start absichern.
    /// </summary>
    /// <param name="services">Der Container.</param>
    /// <param name="keyRing">Der bereits begonnene DataProtection-Aufbau aus <c>Program.cs</c>.</param>
    /// <param name="configuration">Die Konfiguration dieser Instanz.</param>
    /// <param name="dataDirectory">Das Datenverzeichnis.</param>
    /// <returns>Die aufgelöste Betriebsart — für Meldungen beim Start.</returns>
    /// <exception cref="KeyRingConfigurationException">
    /// Wenn die Angaben sich widersprechen oder ein konfiguriertes Zertifikat unbrauchbar ist. Der
    /// Start bricht dann ab. Das ist Absicht: Ein Gateway, der mit einem unbrauchbaren Zertifikat
    /// hochkommt, legt beim ersten Zugriff einen neuen Schlüssel an — und ab da ist der alte
    /// Geheimtext auch mit dem richtigen Zertifikat nicht mehr zu öffnen.
    /// </exception>
    public static KeyRingSettings AddBifrostKeyRing(
        this IServiceCollection services,
        IDataProtectionBuilder keyRing,
        IConfiguration configuration,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var settings = KeyRingSettings.Resolve(name => configuration[name]);

        if (settings.IsProtected)
        {
            // Geladen wird HIER und nicht beim ersten Zugriff: Ein fehlendes oder falsches
            // Zertifikat soll den Start kosten, nicht den ersten Tool-Aufruf.
            var certificates = KeyRingCertificates.Load(settings);
            keyRing.ProtectKeysWithCertificate(certificates[0])
                // Für Zertifikatswechsel: mit dem alten Zertifikat verschlüsselte Schlüssel bleiben
                // lesbar, solange es über BIFROST_KEYRING_CERT_PATH_PREVIOUS weiterhin dabei ist.
                .UnprotectKeysWithAnyCertificate([.. certificates]);
        }

        services.AddSingleton(settings);
        services.AddSingleton(KeyRingPaths.For(dataDirectory));
        services.AddSingleton<IKeyRingWitnessStore>(_ => new KeyRingWitnessFile(dataDirectory));
        services.AddSingleton<IKeyRingCiphertextProbe, EfKeyRingCiphertextProbe>();
        services.AddSingleton<KeyRingStartup>();

        return settings;
    }

    /// <summary>
    /// Der Startschritt aus <c>Program.cs</c>: Lage beurteilen, protokollieren, Zeugen nachführen.
    /// Liefert <c>null</c>, wenn weitergestartet werden darf, sonst den Rückgabewert des Prozesses.
    /// </summary>
    public static async Task<int?> EnsureKeyRingUsableAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var startup = app.Services.GetRequiredService<KeyRingStartup>();
        var verdict = await startup.RunAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
        return verdict.Blocks ? KeyRingStartup.UnusableExitCode : null;
    }
}
