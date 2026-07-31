using System.Globalization;
using System.Security.Cryptography.X509Certificates;

using Bifrost.Persistence;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace Bifrost.Server.KeyRing;

/// <summary>Ergebnis einer Leseprobe auf den Key-Ring.</summary>
/// <param name="Total">Wie viele Schlüssel im Ring lagen.</param>
/// <param name="Readable">Wie viele davon sich mit der geprüften Zertifikatslage öffnen ließen.</param>
/// <param name="UnreadableIds">Die Ids der übrigen.</param>
/// <param name="CreatedKeys">
/// Ist während der Probe eine <b>neue</b> Schlüsseldatei entstanden? Das darf nie passieren und wird
/// deshalb nicht angenommen, sondern nachgezählt: Genau dieses Verhalten — ein unlesbarer Ring wird
/// klammheimlich durch einen frischen ersetzt — ist der Ausfall, gegen den dieses Paket antritt.
/// </param>
/// <param name="Failure">Der Grund, wenn die Probe selbst nicht durchführbar war.</param>
public sealed record KeyRingReadReport(
    int Total, int Readable, IReadOnlyList<string> UnreadableIds, bool CreatedKeys, string? Failure)
{
    /// <summary>Ließ sich <b>jeder</b> vorhandene Schlüssel öffnen?</summary>
    public bool AllReadable => Failure is null && !CreatedKeys && UnreadableIds.Count == 0;

    public string Describe() => Failure is not null
        ? Failure
        : CreatedKeys
            ? "Die Probe hat einen neuen Schlüssel entstehen lassen — der vorhandene Ring war für "
                + "diese Zertifikatslage nicht lesbar."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Readable} von {Total} Schlüssel(n) lesbar{Tail}");

    private string Tail => UnreadableIds.Count == 0
        ? "."
        : $"; nicht lesbar: {string.Join(", ", UnreadableIds)}.";
}

/// <summary>
/// Die Leseprobe: Lässt sich der <b>vorhandene</b> Key-Ring mit einer gegebenen Zertifikatslage
/// öffnen? (WP3.3, Auftrag 4)
/// <para>
/// <b>Warum auf einer Kopie:</b> DataProtection legt bei einem Ring, den es nicht lesen kann, von
/// sich aus einen neuen Schlüssel an — das ist sein normales Verhalten und aus seiner Sicht richtig.
/// Für uns ist es der Schaden selbst. Eine Probe, die auf dem echten Verzeichnis liefe, könnte
/// deshalb genau das anrichten, wovor sie warnen soll. Auf der Kopie ist das folgenlos, und ob es
/// passiert ist, wird anschließend abgezählt.
/// </para>
/// <para>
/// Das ist der Schritt, der <b>vor</b> einem Zertifikatswechsel läuft. Ein Wechsel, der erst im
/// Betrieb auffällt, hat die Instanz bereits unlesbar gemacht.
/// </para>
/// </summary>
public static class KeyRingProbe
{
    /// <summary>
    /// Prüft, ob der Ring unter <paramref name="keyRingDirectory"/> mit
    /// <paramref name="certificates"/> lesbar ist. Das erste Zertifikat verschlüsselt neue
    /// Schlüssel, alle entschlüsseln — genau die Zusammensetzung, die der Serverprozess herstellt.
    /// </summary>
    public static KeyRingReadReport Read(
        string keyRingDirectory, IReadOnlyList<X509Certificate2> certificates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyRingDirectory);
        ArgumentNullException.ThrowIfNull(certificates);

        var existing = KeyRingDirectory.Read(keyRingDirectory);
        if (existing.Count == 0)
        {
            return new KeyRingReadReport(0, 0, [], false, null);
        }

        var work = Directory.CreateTempSubdirectory("bifrost-keyring-probe-");
        try
        {
            foreach (var key in existing)
            {
                File.Copy(key.Path, Path.Combine(work.FullName, Path.GetFileName(key.Path)));
            }

            var before = Directory.GetFiles(work.FullName, KeyRingDirectory.KeyFilePattern).Length;

            var services = new ServiceCollection();
            services.AddLogging();
            var builder = services.AddDataProtection()
                .SetApplicationName(CryptographicNames.DataProtectionApplication)
                .PersistKeysToFileSystem(work);
            if (certificates.Count > 0)
            {
                builder.ProtectKeysWithCertificate(certificates[0])
                    .UnprotectKeysWithAnyCertificate([.. certificates]);
            }

            using var provider = services.BuildServiceProvider();
            var manager = provider.GetRequiredService<IKeyManager>();

            var unreadable = new List<string>();
            var readable = 0;
            foreach (var key in manager.GetAllKeys())
            {
                try
                {
                    // Der Zugriff auf den Deskriptor ist der Schritt, der ihn entschlüsselt. Ohne ihn
                    // wäre 'GetAllKeys' nur ein Verzeichnislisting mit mehr Schritten.
                    _ = key.Descriptor;
                    readable++;
                }
#pragma warning disable CA1031 // Jede Ausnahme heißt hier dasselbe: dieser Schlüssel ist nicht lesbar.
                catch (Exception)
#pragma warning restore CA1031
                {
                    unreadable.Add(key.KeyId.ToString());
                }
            }

            var after = Directory.GetFiles(work.FullName, KeyRingDirectory.KeyFilePattern).Length;
            return new KeyRingReadReport(existing.Count, readable, unreadable, after > before, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new KeyRingReadReport(
                existing.Count, 0, [], false,
                $"Die Leseprobe war nicht durchführbar ({exception.GetType().Name}): {exception.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(work.FullName, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Aufräumen ist Höflichkeit, nicht Vertrag.
            }
        }
    }
}
