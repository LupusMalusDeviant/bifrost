using System.Collections.Concurrent;
using McpMcp.Abstractions;
using Microsoft.Extensions.Logging;

namespace McpMcp.Core.Packaging;

/// <summary>Womit die Probe arbeitet: das geprüfte Manifest und die ausgepackten Dateien.</summary>
public sealed record ConnectorProbeContext(
    ConnectorManifest Manifest,
    string Directory,
    string EntryPointPath,
    string SignaturePath,
    IReadOnlyList<string> GrantedCapabilities);

/// <summary>
/// Startet den Connector aus der Quarantäne und prüft, dass er antwortet. Wirft, wenn nicht —
/// dann wird die Version nie aktiv.
/// </summary>
public delegate Task ConnectorProbe(ConnectorProbeContext context, CancellationToken ct);

/// <summary>Was der Administrator beim Installieren mitgibt.</summary>
public sealed record ConnectorInstallOptions(
    IReadOnlyList<string>? AcceptedGrants = null,
    bool AllowUntrusted = false);

/// <summary>
/// Ergebnis einer Installation: das Paket <b>und</b> was es an Skills eingespielt hat.
/// <para>
/// Die Skills stehen hier und nicht nur im Log, weil einer davon eine lokal angepasste Fassung
/// abgelöst haben kann. Das muss derjenige erfahren, der gerade installiert hat — hinterher fällt
/// es niemandem mehr auf.
/// </para>
/// </summary>
public sealed record ConnectorInstallResult(
    InstalledConnectorPackage Package,
    IReadOnlyList<SkillPublication> Skills)
{
    public IReadOnlyList<SkillPublication> ReplacedLocalEdits
        => [.. Skills.Where(s => s.ReplacedLocalEdit)];
}

/// <summary>
/// Installiert, aktualisiert und rollt Connector-Pakete zurück (ADR-0016).
/// <para>
/// Der Ablauf ist bewusst in dieser Reihenfolge: <b>prüfen → auspacken in Quarantäne → proben →
/// atomar aktivieren</b>. Eine Version, die die Probe nicht besteht, hat nie in Betrieb gestanden;
/// die vorherige bleibt liegen, damit ein Rollback ohne erneuten Download geht.
/// </para>
/// <para>
/// Der Installer kennt weder WASI noch einen Prozess — die Probe kommt als Delegat herein. Sonst
/// hinge die Paketverwaltung an genau einer Laufzeit, und der nächste Connector-Typ müsste sie
/// aufbrechen.
/// </para>
/// </summary>
public sealed partial class ConnectorPackageInstaller
{
    private readonly IConnectorPackageStore _store;
    private readonly IPublisherTrustStore _trust;
    private readonly ConnectorProbe _probe;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectorPackageInstaller>? _log;
    private readonly IAuditSink? _audit;
    private readonly IAssetStore? _assets;

    public ConnectorPackageInstaller(
        string rootDirectory,
        IConnectorPackageStore store,
        IPublisherTrustStore trust,
        ConnectorProbe probe,
        TimeProvider time,
        IAuditSink? audit = null,
        ILogger<ConnectorPackageInstaller>? log = null,
        IAssetStore? assets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(time);

        RootDirectory = Path.GetFullPath(rootDirectory);
        _store = store;
        _trust = trust;
        _probe = probe;
        _time = time;
        _audit = audit;
        _log = log;
        _assets = assets;
    }

    public string RootDirectory { get; }

    private string QuarantineRoot => Path.Combine(RootDirectory, ".quarantine");

    /// <summary>
    /// Installiert ein Paket und macht es zur aktiven Version. Eine bereits vorhandene Version
    /// desselben Pakets bleibt als <see cref="PackageState.Superseded"/> liegen.
    /// </summary>
    public async Task<ConnectorInstallResult> InstallAsync(
        Stream package, ConnectorInstallOptions options, IdentityId? caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        options ??= new ConnectorInstallOptions();

        var verified = ConnectorPackageReader.Verify(package, _trust.All);
        var manifest = verified.Manifest;
        var granted = ConnectorTrustPolicy.Evaluate(
            manifest, verified.TrustLevel, options.AcceptedGrants, options.AllowUntrusted);
        EnsurePlatformSupported(manifest);

        var existing = await _store.GetVersionsAsync(manifest.Id, ct).ConfigureAwait(false);
        if (existing.FirstOrDefault(v => v.Version == manifest.Version) is { } duplicate
            && duplicate.State is not PackageState.Failed)
        {
            throw new ConnectorPackageException(
                $"'{manifest.Id}' {manifest.Version} ist bereits installiert (Zustand {duplicate.State}). "
                + "Zwei Pakete gleicher Version wären nicht unterscheidbar — eine Änderung braucht "
                + "eine neue Versionsnummer.");
        }

        var quarantine = Path.Combine(QuarantineRoot, Guid.NewGuid().ToString("N"));
        var target = VersionDirectory(manifest.Id, manifest.Version);
        var record = new InstalledConnectorPackage(
            manifest.Id, manifest.Version, manifest.DisplayName, manifest.Transport,
            verified.Publisher.KeyId, verified.TrustLevel, verified.ManifestSha256,
            target, PackageState.Quarantined, _time.GetUtcNow(), null, granted);

        try
        {
            ConnectorPackageReader.Extract(package, manifest, quarantine);

            // Die Probe läuft auf den Dateien in der Quarantäne — also genau auf dem, was gleich
            // aktiv wird, nicht auf einer Kopie davon.
            await _probe(
                new ConnectorProbeContext(
                    manifest, quarantine,
                    Path.Combine(quarantine, manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar)),
                    Path.Combine(quarantine, manifest.SignaturePath.Replace('/', Path.DirectorySeparatorChar)),
                    granted),
                ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SafeDelete(quarantine);

            // Der Fehlschlag wird festgehalten, nicht verschwiegen: Sonst sieht ein Administrator
            // nur, dass nichts passiert ist, und probiert dieselbe Datei erneut.
            var failed = record with { State = PackageState.Failed, FailureReason = exception.Message };
            await _store.UpsertAsync(failed, ct).ConfigureAwait(false);
            Audit("Paket abgewiesen", manifest.Id, manifest.Version,
                $"{verified.TrustLevel}: {exception.Message}", caller);
            if (_log is not null)
            {
                Log.PackageRejected(_log, exception, manifest.Id, manifest.Version);
            }
            throw;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        SafeDelete(target);
        Directory.Move(quarantine, target);

        await _store.UpsertAsync(record, ct).ConfigureAwait(false);
        await _store.ActivateAsync(manifest.Id, manifest.Version, _time.GetUtcNow(), ct).ConfigureAwait(false);
        Audit("Paket installiert", manifest.Id, manifest.Version,
            $"Herausgeber {verified.Publisher.KeyId}, Stufe {verified.TrustLevel}, Grants: "
            + (granted.Count == 0 ? "keine" : string.Join(", ", granted)), caller);

        // Erst NACH der Aktivierung. Ein Paket, dessen Probe scheitert, darf keine Anweisungen
        // hinterlassen — die wären dann in Umlauf, ohne dass der Konnektor je gelaufen ist.
        var skills = await PublishSkillsAsync(manifest, target, caller, ct).ConfigureAwait(false);

        return new ConnectorInstallResult(
            (await _store.GetActiveAsync(manifest.Id, ct).ConfigureAwait(false))!, skills);
    }

    /// <summary>
    /// Spielt die mitgelieferten Skills ein (Material 0021-EM, Option B). Der Name bekommt das
    /// Paketpräfix, damit ein Paket keinen handgeschriebenen Skill überschatten kann.
    /// </summary>
    private async Task<IReadOnlyList<SkillPublication>> PublishSkillsAsync(
        ConnectorManifest manifest, string directory, IdentityId? caller, CancellationToken ct)
    {
        if (manifest.SkillsOrEmpty.Count == 0)
        {
            return [];
        }

        if (_assets is null)
        {
            // Kein stiller Verlust: Wer ein Paket mit Skills in eine Zusammenstellung ohne
            // Skill-Ablage installiert, bekäme sonst den Konnektor und wüsste nie, dass die
            // Anleitung dazu unter den Tisch gefallen ist.
            throw new ConnectorPackageException(
                $"'{manifest.Id}' bringt {manifest.SkillsOrEmpty.Count} Skill(s) mit, aber in dieser "
                + "Zusammenstellung ist keine Skill-Ablage eingebunden.");
        }

        var source = new SkillSource(manifest.Id, manifest.Version);
        var published = new List<SkillPublication>();
        foreach (var skill in manifest.SkillsOrEmpty)
        {
            var path = Path.Combine(directory, skill.Path.Replace('/', Path.DirectorySeparatorChar));
            var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var metadata = new SkillMetadata(skill.WhenToUse, skill.References, skill.RequiredTools);
            var result = await _assets.PublishFromPackageAsync(
                $"{manifest.Id}/{skill.Name}", skill.Description, content,
                metadata.IsEmpty ? null : metadata, source, ct).ConfigureAwait(false);
            published.Add(result);

            Audit(
                result.ReplacedLocalEdit ? "Skill aus Paket - lokale Fassung abgeloest" : "Skill aus Paket",
                manifest.Id, manifest.Version,
                $"{result.Name} Version {result.Version.Value}", caller);
        }

        return published;
    }

    /// <summary>
    /// Schaltet auf die zuletzt abgelöste Version zurück. Sie liegt noch auf der Platte und wurde
    /// bei ihrer Installation schon geprobt — deshalb ist ein Rollback ein Schalter, kein Neubau.
    /// </summary>
    public async Task<InstalledConnectorPackage> RollbackAsync(
        string packageId, IdentityId? caller, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var versions = await _store.GetVersionsAsync(packageId, ct).ConfigureAwait(false);
        var previous = versions
            .Where(v => v.State is PackageState.Superseded)
            .OrderByDescending(v => v.ActivatedAt ?? v.InstalledAt)
            .FirstOrDefault()
            ?? throw new ConnectorPackageException(
                $"Für '{packageId}' gibt es keine abgelöste Version, auf die zurückzuschalten wäre.");

        if (!Directory.Exists(previous.Directory))
        {
            throw new ConnectorPackageException(
                $"Die Dateien von '{packageId}' {previous.Version} fehlen unter '{previous.Directory}'. "
                + "Ein Rollback auf einen leeren Ordner wäre ein stiller Ausfall.");
        }

        await _store.ActivateAsync(packageId, previous.Version, _time.GetUtcNow(), ct).ConfigureAwait(false);
        Audit("Paket zurueckgeschaltet", packageId, previous.Version, null, caller);
        return (await _store.GetActiveAsync(packageId, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Entfernt eine einzelne Version samt Dateien. Die <b>aktive</b> Version wird abgelehnt: Wer
    /// sie loswerden will, schaltet erst zurück oder entfernt das ganze Paket — sonst zeigte eine
    /// Upstream-Konfiguration ins Leere, ohne dass es jemand bemerkt.
    /// </summary>
    public async Task RemoveVersionAsync(
        string packageId, string version, IdentityId? caller, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var versions = await _store.GetVersionsAsync(packageId, ct).ConfigureAwait(false);
        var entry = versions.FirstOrDefault(v => v.Version == version)
            ?? throw new ConnectorPackageException($"'{packageId}' {version} ist nicht installiert.");
        if (entry.State is PackageState.Active)
        {
            throw new ConnectorPackageException(
                $"'{packageId}' {version} ist die aktive Version. Erst zurückschalten oder das ganze "
                + "Paket entfernen.");
        }

        await _store.RemoveAsync(packageId, version, ct).ConfigureAwait(false);
        SafeDelete(entry.Directory);
        Audit("Paketversion entfernt", packageId, version, null, caller);
    }

    /// <summary>
    /// Was das Entfernen dieses Pakets an Skills mitnehmen würde (ADR-0021, F5). Die Auflage aus dem
    /// ADR: Es wird vorher gesagt, und eine <b>lokal angepasste</b> Fassung wird besonders genannt —
    /// erkennbar daran, dass die neueste Version keine Paketherkunft mehr trägt.
    /// </summary>
    public async Task<IReadOnlyList<AssetInfo>> PreviewRemovalAsync(
        string packageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        return _assets is null
            ? []
            : await _assets.ListFromPackageAsync(packageId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Entfernt alle Versionen eines Pakets, aktive eingeschlossen — <b>samt</b> der Skills, die es
    /// mitgebracht hat (ADR-0021, F5). Liefert deren Namen zurück, damit der Aufrufer sie nennen
    /// kann.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemovePackageAsync(
        string packageId, IdentityId? caller, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var versions = await _store.GetVersionsAsync(packageId, ct).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            throw new ConnectorPackageException($"'{packageId}' ist nicht installiert.");
        }

        foreach (var version in versions)
        {
            await _store.RemoveAsync(packageId, version.Version, ct).ConfigureAwait(false);
            SafeDelete(version.Directory);
        }

        SafeDelete(Path.Combine(RootDirectory, packageId));

        // Erst die Dateien, dann die Skills: Bleibt eine Anleitung ohne ihren Konnektor stehen, ist
        // das der Zustand, den F5 gerade abschafft — umgekehrt wäre eine gelöschte Anleitung zu
        // einem noch laufenden Konnektor der Schaden.
        var removedSkills = _assets is null
            ? []
            : await _assets.DeleteFromPackageAsync(packageId, ct).ConfigureAwait(false);

        Audit("Paket entfernt", packageId, "alle Versionen",
            removedSkills.Count == 0 ? null : $"Skills mitentfernt: {string.Join(", ", removedSkills)}",
            caller);
        return removedSkills;
    }

    private string VersionDirectory(string packageId, string version)
        => Path.Combine(RootDirectory, packageId, version);

    private static void EnsurePlatformSupported(ConnectorManifest manifest)
    {
        if (manifest.Platforms is not { Count: > 0 } platforms)
        {
            // Keine Angabe heißt „überall" — bei WASI ist genau das der Normalfall, und eine
            // Pflichtangabe würde portable Pakete ohne Not aussperren.
            return;
        }

        var current = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var os = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows) ? "win"
            : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.OSX) ? "osx" : "linux";

        foreach (var platform in platforms)
        {
            if (platform is "any"
                || platform.Equals(current, StringComparison.OrdinalIgnoreCase)
                || platform.StartsWith(os, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new ConnectorPackageException(
            $"'{manifest.Id}' nennt die Plattformen {string.Join(", ", platforms)}; hier läuft "
            + $"{current}.");
    }

    private static void SafeDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ein nicht löschbares Verzeichnis darf die Installation nicht scheitern lassen; es
            // bleibt liegen und fällt beim nächsten Blick in den Ordner auf.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Paketwechsel sind Konfigurationsänderungen und gehören ins Audit — mit Herausgeber, Stufe
    /// und erteilten Zugriffen. Ohne diese Zeile ließe sich hinterher nicht sagen, wem das Gateway
    /// wann was erlaubt hat.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Connector-Paket {PackageId} {Version} abgewiesen — die Version wird nicht aktiv.")]
        public static partial void PackageRejected(
            ILogger logger, Exception exception, string packageId, string version);
    }

    private void Audit(string action, string packageId, string version, string? detail, IdentityId? caller)
        => _audit?.Record(new AuditEvent(
            _time.GetUtcNow(), caller, CallOrigin.System, AuditEventKind.ConfigChanged,
            Server: null, Tool: null, Status: null, RedactedArguments: null,
            RequestBytes: null, ResponseBytes: null, Duration: null,
            Detail: $"{action}: {packageId}@{version}"
                + (detail is null ? string.Empty : $" — {detail}")));
}

/// <summary>
/// Löst Paket-Ids in die Dateien der aktiven Version auf. Hält einen Schnappschuss, weil die
/// Auflösung bei jedem Upstream-Start passiert und die Installation selten.
/// </summary>
public sealed class ConnectorPackageResolver : IConnectorPackageResolver
{
    private static readonly System.Text.Json.JsonSerializerOptions ManifestJson =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

    private readonly IConnectorPackageStore _store;
    private readonly ConcurrentDictionary<string, (string EntryPoint, string SignaturePath)> _active =
        new(StringComparer.Ordinal);

    public ConnectorPackageResolver(IConnectorPackageStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public (string EntryPoint, string SignaturePath)? ResolveActive(string packageId)
        => _active.TryGetValue(packageId, out var paths) ? paths : null;

    /// <summary>Liest den aktiven Stand neu ein. Nach jeder Installation und beim Start.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var all = await _store.ListAsync(ct).ConfigureAwait(false);
        _active.Clear();
        foreach (var package in all.Where(p => p.State is PackageState.Active))
        {
            var manifestPath = Path.Combine(package.Directory, ConnectorPackageReader.ManifestEntry);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var manifest = System.Text.Json.JsonSerializer.Deserialize<ConnectorManifest>(
                File.ReadAllBytes(manifestPath), ManifestJson);
            if (manifest is null)
            {
                continue;
            }

            _active[package.PackageId] = (
                Path.Combine(package.Directory, manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(package.Directory, manifest.SignaturePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
