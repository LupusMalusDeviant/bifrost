using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Bifrost.Server.KeyRing;

/// <summary>Wie streng eine Datei mit Schlüsselmaterial abgeschirmt ist.</summary>
/// <param name="Restricted">
/// Ist sie nachweislich auf den Dienstbenutzer beschränkt? <c>false</c> heißt „nicht beschränkt
/// <b>oder</b> nicht beurteilbar" — welches von beidem, sagt <paramref name="Description"/>.
/// </param>
/// <param name="Description">Ein Satz für Menschen: <c>0600</c> beziehungsweise die ACL-Lage.</param>
public sealed record SecretFilePermissionState(bool Restricted, string Description);

/// <summary>
/// Rechte auf Dateien mit privatem Schlüsselmaterial — PFX und Passwortdatei.
/// <para>
/// <b>Warum das hier eigenen Code braucht:</b> Ein PFX mit privatem Schlüssel, das
/// <c>rw-r--r--</c> steht, ist auf einer Mehrbenutzermaschine kein Schutz, sondern eine Kopie für
/// jeden. Und der Fehler passiert nicht beim Nachdenken, sondern beim Erzeugen — <c>openssl
/// pkcs12 -export</c> legt die Datei mit der Standardmaske an. Deshalb setzt der Setup-Weg die
/// Rechte selbst und der Prüfweg sagt, was er vorfindet.
/// </para>
/// <para>
/// Beide Welten werden bedient und keine getürkt: Unter Unix sind es die Modusbits (0600), unter
/// Windows eine ACL ohne Vererbung mit genau einem Eintrag für den aktuellen Benutzer. Wo eine
/// Aussage nicht möglich ist, steht „nicht beurteilbar" — nie „in Ordnung".
/// </para>
/// </summary>
public static class SecretFilePermissions
{
    /// <summary>Legt die Datei an und schirmt sie sofort ab.</summary>
    /// <exception cref="IOException">Wenn die Datei bereits existiert.</exception>
    public static void WriteRestricted(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        // CreateNew statt Create: Ein Setup-Lauf, der ein vorhandenes Zertifikat überschreibt, hat
        // damit den Key-Ring der Instanz entwertet — bevor irgendjemand gefragt wurde.
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
        }

        Restrict(path);
    }

    /// <summary>
    /// Beschränkt eine bestehende Datei auf den aktuellen Benutzer. Wirft nicht: Ein fehlgeschlagener
    /// Rechteschritt ist ein <b>Befund</b>, den <see cref="Describe"/> anschließend meldet — und
    /// nicht der Grund, eine gerade erzeugte Datei wieder wegzuwerfen.
    /// </summary>
    public static void Restrict(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                RestrictWindows(path);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Was tatsächlich gilt.</summary>
    public static SecretFilePermissionState Describe(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new SecretFilePermissionState(false, "Datei nicht vorhanden.");
        }

        try
        {
            return OperatingSystem.IsWindows() ? DescribeWindows(path) : DescribeUnix(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or NotSupportedException or InvalidOperationException)
        {
            return new SecretFilePermissionState(
                false, $"Nicht beurteilbar ({exception.GetType().Name}).");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static SecretFilePermissionState DescribeUnix(string path)
    {
        var mode = File.GetUnixFileMode(path);
        var forOthers = mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        return forOthers is UnixFileMode.None
            ? new SecretFilePermissionState(true, $"Modus {Octal(mode)} — nur der Eigentümer.")
            : new SecretFilePermissionState(
                false,
                $"Modus {Octal(mode)} — auch Gruppe oder andere haben Zugriff. Erwartet wird 600.");
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();

        // Vererbung kappen und die geerbten Regeln NICHT übernehmen: Sie sind genau die, die
        // 'Benutzer' und 'Authentifizierte Benutzer' hereinlassen.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleSpecific(rule);
        }

        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Der aktuelle Benutzer hat keine SID.");
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete,
            AccessControlType.Allow));

        file.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static SecretFilePermissionState DescribeWindows(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

        // Nach SID und nicht nach Namen: 'Users' heißt auf einem deutschen Windows 'Benutzer', und
        // eine Prüfung, die an der Sprache des Betriebssystems scheitert, ist keine Prüfung.
        var broad = new List<string>();
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType is not AccessControlType.Allow
                || rule.IdentityReference is not SecurityIdentifier sid)
            {
                continue;
            }

            if (sid.IsWellKnown(WellKnownSidType.WorldSid)
                || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
                || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid)
                || sid.IsWellKnown(WellKnownSidType.InteractiveSid))
            {
                broad.Add(sid.Value);
            }
        }

        return broad.Count == 0
            ? new SecretFilePermissionState(
                true, "ACL ohne breit gefasste Zugriffsrechte (weder Jeder/Everyone noch Benutzer/Users).")
            : new SecretFilePermissionState(
                false,
                "Die ACL lässt breit gefasste Gruppen zu (" + string.Join(", ", broad)
                + "). Erwartet wird eine ACL ohne Vererbung, die nur den Dienstbenutzer nennt.");
    }

    private static string Octal(UnixFileMode mode)
    {
        var value = 0;
        if (mode.HasFlag(UnixFileMode.UserRead)) { value |= 0b100_000_000; }
        if (mode.HasFlag(UnixFileMode.UserWrite)) { value |= 0b010_000_000; }
        if (mode.HasFlag(UnixFileMode.UserExecute)) { value |= 0b001_000_000; }
        if (mode.HasFlag(UnixFileMode.GroupRead)) { value |= 0b000_100_000; }
        if (mode.HasFlag(UnixFileMode.GroupWrite)) { value |= 0b000_010_000; }
        if (mode.HasFlag(UnixFileMode.GroupExecute)) { value |= 0b000_001_000; }
        if (mode.HasFlag(UnixFileMode.OtherRead)) { value |= 0b000_000_100; }
        if (mode.HasFlag(UnixFileMode.OtherWrite)) { value |= 0b000_000_010; }
        if (mode.HasFlag(UnixFileMode.OtherExecute)) { value |= 0b000_000_001; }
        return Convert.ToString(value, 8).PadLeft(3, '0');
    }
}
