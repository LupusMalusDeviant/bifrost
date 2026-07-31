using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;

using Bifrost.Abstractions.Operations;

namespace Bifrost.Persistence.Backup;

/// <summary>
/// Wiederherstellung aus einem Sicherungsarchiv (WP2.2, ADR-0024 E5/E6).
/// <para>
/// <see cref="PlanAsync"/> prüft und schreibt nichts. <see cref="ApplyAsync"/> entpackt in ein
/// Staging-Verzeichnis, prüft dort erneut und schaltet erst danach um. Ein Restore, der beim
/// Schreiben merkt, dass er nicht passt, hat bereits geschrieben — deshalb die Trennung.
/// </para>
/// <para>
/// <b>Vertragslücke, gemeldet statt umgangen:</b> <c>ApplyAsync</c> bekommt laut
/// <c>Operations.cs</c> nur den Plan, der weder Archivpfad noch Passphrase trägt. Diese Klasse merkt
/// sich beides zum jeweiligen Plan (schwache Referenz, kein Zustandsleck). Folge: Plan und Anwendung
/// müssen dieselbe Dienstinstanz sehen; ein fremder Plan wird abgewiesen statt geraten.
/// </para>
/// </summary>
public sealed class RestoreService : IRestoreService
{
    private readonly BackupOptions _options;
    private readonly IBackupService _backupService;
    private readonly ConditionalWeakTable<RestorePlan, PlanContext> _contexts = [];

    public RestoreService(BackupOptions options, IBackupService backupService)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backupService);
        _options = options;
        _backupService = backupService;
    }

    private sealed record PlanContext(string ArchivePath, string? Passphrase);

    // ── Vorprüfung ──────────────────────────────────────────────────────────────────────────────

    public async Task<RestorePlan> PlanAsync(RestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArchivePath);

        var blockers = new List<string>();
        var warnings = new List<string>();

        var archivePath = Path.GetFullPath(request.ArchivePath);
        var inspected = await ArchiveInspector
            .InspectAsync(archivePath, request.Passphrase, ct)
            .ConfigureAwait(false);
        blockers.AddRange(inspected.Problems);

        var manifest = inspected.Manifest;
        if (manifest is not null)
        {
            CheckManifest(manifest, request, blockers, warnings);
        }

        var targetIsEmpty = IsTargetEmpty();
        if (!targetIsEmpty && request.Mode == RestoreMode.EmptyTargetOnly)
        {
            blockers.Add(
                "Die Zielinstanz ist nicht leer. Ein Restore läuft nach Vorgabe nur auf eine leere " +
                "Installation; zum Überschreiben braucht es die ausdrückliche Bestätigung (--replace).");
        }

        CheckDiskSpace(inspected.TotalUncompressedBytes, blockers, warnings);

        var canApply = blockers.Count == 0 && manifest is not null;
        var preBackupPath = canApply && request.Mode == RestoreMode.Replace && !targetIsEmpty
            ? PlannedPreBackupPath()
            : null;

        var plan = new RestorePlan(
            canApply,
            manifest?.ToContract(),
            request.Mode,
            targetIsEmpty,
            blockers.AsReadOnly(),
            warnings.AsReadOnly(),
            preBackupPath);

        _contexts.AddOrUpdate(plan, new PlanContext(archivePath, request.Passphrase));
        return plan;
    }

    private void CheckManifest(
        BackupManifestDocument manifest,
        RestoreRequest request,
        List<string> blockers,
        List<string> warnings)
    {
        if (manifest.FormatVersion > BackupLayout.FormatVersion)
        {
            blockers.Add(
                $"Das Archiv hat Formatversion {manifest.FormatVersion}; diese Installation kennt " +
                $"höchstens {BackupLayout.FormatVersion}.");
        }

        // ADR-0024 E6: vorwärts ja, rückwärts nein.
        if (!ProductVersionOrder.IsAtLeast(_options.ProductVersion, manifest.MinimumRestoreVersion, out var problem))
        {
            blockers.Add(problem ?? string.Format(
                CultureInfo.InvariantCulture,
                "Das Archiv verlangt mindestens Version {0}, diese Installation ist {1}. " +
                "Ein Rückspielen in eine ältere Version wird abgelehnt, nicht versucht.",
                manifest.MinimumRestoreVersion,
                _options.ProductVersion));
        }

        if (!BackupManifestDocument.TryParseProvider(manifest.Database.Provider, out var provider))
        {
            blockers.Add($"Unbekannter Datenbankanbieter im Manifest: '{manifest.Database.Provider}'.");
        }
        else if (provider != _options.Provider)
        {
            blockers.Add(
                $"Das Archiv stammt von '{manifest.Database.Provider}', diese Instanz läuft auf " +
                $"'{BackupManifestDocument.ProviderName(_options.Provider)}'. Ein Anbieterwechsel ist " +
                "kein Restore.");
        }
        else if (provider == DatabaseProvider.Postgres)
        {
            blockers.Add(PostgresBackup.NotImplementedMessage);
        }

        if (manifest.IsEncrypted && string.IsNullOrEmpty(request.Passphrase))
        {
            blockers.Add("Das Archiv ist verschlüsselt — ohne Passphrase ist die Nutzlast nicht lesbar.");
        }

        if (string.IsNullOrEmpty(manifest.InstanceId))
        {
            warnings.Add(
                "Das Archiv trägt keine Instanz-Id (beim Sichern fehlte config/instance.json).");
        }

        if (ProductVersionOrder.TryParse(manifest.ProductVersion, out var archiveVersion)
            && ProductVersionOrder.TryParse(_options.ProductVersion, out var own)
            && archiveVersion > own)
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Das Archiv stammt aus Version {0}, diese Installation ist {1}.",
                manifest.ProductVersion,
                _options.ProductVersion));
        }
    }

    private void CheckDiskSpace(long neededBytes, List<string> blockers, List<string> warnings)
    {
        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_options.DataDirectory));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            warnings.Add("Der freie Speicherplatz ließ sich nicht ermitteln.");
            return;
        }

        if (free < neededBytes)
        {
            blockers.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Für die Wiederherstellung werden mindestens {0} Bytes gebraucht, frei sind {1}.",
                neededBytes,
                free));
        }
        else if (free < neededBytes * 3)
        {
            warnings.Add(
                "Der freie Speicherplatz reicht knapp: Staging und Sicherung des Altzustands " +
                "brauchen zusätzlich Platz.");
        }
    }

    /// <summary>
    /// Leer heißt: keine Datenbank, kein Key-Ring, keine Pakete. <c>config/instance.json</c> zählt
    /// bewusst nicht mit — die Datei entsteht beim ersten Start und wäre sonst der Grund, warum ein
    /// frisch aufgesetztes Ziel als „nicht leer" gälte.
    /// </summary>
    private bool IsTargetEmpty()
    {
        var database = _options.ResolvedSqliteFile;
        if (File.Exists(database) && new FileInfo(database).Length > 0)
        {
            return false;
        }

        return !HasAnyFile(_options.ResolvedKeyRingDirectory) && !HasAnyFile(_options.ResolvedPackagesDirectory);
    }

    private static bool HasAnyFile(string directory)
        => Directory.Exists(directory)
            && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();

    private string PlannedPreBackupPath() => Path.Combine(
        _options.PreBackupDirectory,
        string.Format(
            CultureInfo.InvariantCulture,
            "pre-restore-{0:yyyyMMdd-HHmmss}.zip",
            DateTimeOffset.UtcNow));

    // ── Anwendung ───────────────────────────────────────────────────────────────────────────────

    public async Task<RestoreResult> ApplyAsync(RestorePlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_contexts.TryGetValue(plan, out var context))
        {
            throw new InvalidOperationException(
                "Zu diesem Plan ist kein Archiv bekannt. PlanAsync muss unmittelbar vorausgehen, und " +
                "zwar auf derselben Dienstinstanz.");
        }

        if (!plan.CanApply)
        {
            throw new InvalidOperationException(
                "Ein Plan mit Blockern wird nicht angewendet: " + string.Join(" | ", plan.Blockers));
        }

        var notes = new List<string>();

        // Zwischen Prüfung und Anwendung kann die Datei getauscht worden sein. Die Prüfung ist
        // billig gegenüber dem Schaden, den ein ungeprüftes Archiv anrichtet.
        var inspected = await ArchiveInspector
            .InspectAsync(context.ArchivePath, context.Passphrase, ct)
            .ConfigureAwait(false);
        if (!inspected.Valid || inspected.Manifest is null)
        {
            notes.Add("Das Archiv hat sich seit der Prüfung verändert oder ist nicht mehr lesbar.");
            notes.AddRange(inspected.Problems);
            return new RestoreResult(false, BackupSections.None, null, notes.AsReadOnly());
        }

        Directory.CreateDirectory(_options.DataDirectory);
        var staging = Path.Combine(_options.DataDirectory, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            var sections = await ExtractAsync(context, inspected.Manifest, staging, ct).ConfigureAwait(false);

            string? preBackup = null;
            if (plan.Mode == RestoreMode.Replace && !plan.TargetIsEmpty)
            {
                // ADR-0024 E5: Ohne Ausweg kein Überschreiben.
                preBackup = await CreatePreBackupAsync(context.Passphrase, ct).ConfigureAwait(false);
                notes.Add($"Der bisherige Zustand liegt in '{preBackup}'.");
            }

            SwitchOver(staging, sections, notes);
            return new RestoreResult(true, sections, preBackup, notes.AsReadOnly());
        }
        finally
        {
            BackupService.TryDeleteDirectory(staging);
        }
    }

    private async Task<string> CreatePreBackupAsync(string? passphrase, CancellationToken ct)
    {
        Directory.CreateDirectory(_options.PreBackupDirectory);
        var target = PlannedPreBackupPath();
        // Dieselbe Passphrase wie der Restore: Wer das Archiv entschlüsseln darf, darf auch die
        // Sicherung des Altzustands lesen — und ein zweites Geheimnis, das niemand notiert hat, wäre
        // beim Ernstfall wertlos.
        var result = await _backupService
            .CreateAsync(new BackupRequest(target, BackupSections.All, passphrase), ct)
            .ConfigureAwait(false);
        return result.ArchivePath;
    }

    /// <summary>
    /// Entpackt in das Staging-Verzeichnis. Jeder Eintrag wird erneut gegen das Staging verankert —
    /// die Prüfung aus der Vorprüfung wird nicht „mitgenommen", sondern wiederholt, weil hier zum
    /// ersten Mal wirklich geschrieben wird.
    /// </summary>
    private static async Task<BackupSections> ExtractAsync(
        PlanContext context, BackupManifestDocument manifest, string staging, CancellationToken ct)
    {
        ArchivePayloadCipher? cipher = null;
        if (manifest.IsEncrypted)
        {
            if (!ArchiveInspector.TryCreateCipher(manifest, context.Passphrase!, out cipher, out var problem))
            {
                throw new InvalidOperationException(problem);
            }
        }

        var sections = BackupSections.None;
        using var stream = new FileStream(context.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName is BackupLayout.ManifestEntry or BackupLayout.ChecksumEntry
                || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            if (ArchiveEntryGuard.IsSymbolicLink(entry))
            {
                throw new InvalidOperationException(
                    $"Eintrag '{entry.FullName}' ist ein symbolischer Verweis und wird abgelehnt.");
            }

            if (!ArchiveEntryGuard.TryResolve(entry.FullName, staging, out var destination, out var problem))
            {
                throw new InvalidOperationException(
                    problem ?? $"Eintrag '{entry.FullName}' ist beim Entpacken abgewiesen worden.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (cipher is null)
            {
                await CopyGuardedAsync(entry, destination, ct).ConfigureAwait(false);
            }
            else
            {
                var stored = await ArchiveInspector.ReadAllAsync(entry, ct).ConfigureAwait(false);
                if (!cipher.TryDecrypt(entry.FullName, stored, out var plaintext, out var cryptoProblem))
                {
                    throw new InvalidOperationException(cryptoProblem);
                }

                await File.WriteAllBytesAsync(destination, plaintext, ct).ConfigureAwait(false);
            }

            sections |= SectionOf(entry.FullName);
        }

        return sections;
    }

    /// <summary>
    /// Kopiert einen Eintrag und zählt dabei mit: Die Längenangabe im Archiv ist eine Behauptung,
    /// die eine Bombe schlicht falsch macht.
    /// </summary>
    private static async Task CopyGuardedAsync(ZipArchiveEntry entry, string destination, CancellationToken ct)
    {
        using var source = entry.Open();
        var target = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await using (target.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > BackupLayout.MaxEntryUncompressedBytes)
                    {
                        throw new InvalidOperationException(
                            $"Eintrag '{entry.FullName}' entpackt sich über die zulässige Größe hinaus.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static BackupSections SectionOf(string entryName)
    {
        if (entryName.StartsWith(BackupLayout.DatabaseZone, StringComparison.Ordinal))
        {
            return BackupSections.Database;
        }

        if (entryName.StartsWith(BackupLayout.KeyRingZone, StringComparison.Ordinal))
        {
            return BackupSections.KeyRing;
        }

        if (entryName.StartsWith(BackupLayout.PackagesZone, StringComparison.Ordinal))
        {
            return BackupSections.Packages;
        }

        return BackupSections.Config;
    }

    // ── Umschalten ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Der eigentliche Tausch. Alles Bestehende wandert zuerst beiseite (in das Staging, also auf
    /// dasselbe Dateisystem — ein Move dorthin ist ein Umbenennen und kein Kopieren), dann wird das
    /// Neue eingehängt. Scheitert ein Schritt, laufen die vorigen rückwärts zurück.
    /// </summary>
    private void SwitchOver(string staging, BackupSections sections, List<string> notes)
    {
        var parked = Path.Combine(staging, ".replaced");
        Directory.CreateDirectory(parked);
        var undo = new Stack<Action>();

        try
        {
            if (sections.HasFlag(BackupSections.Database))
            {
                var target = _options.ResolvedSqliteFile;
                MoveFileIntoPlace(Path.Combine(staging, "database", "bifrost.db"), target, parked, undo);
                SqliteSnapshot.RemoveSidecars(target);
                notes.Add("Datenbank ersetzt; liegengebliebene WAL-Begleiter entfernt.");
            }

            if (sections.HasFlag(BackupSections.KeyRing))
            {
                MoveDirectoryIntoPlace(
                    Path.Combine(staging, "keyring"), _options.ResolvedKeyRingDirectory, parked, undo);
                notes.Add("Key-Ring ersetzt.");
            }

            if (sections.HasFlag(BackupSections.Packages))
            {
                MoveDirectoryIntoPlace(
                    Path.Combine(staging, "packages"), _options.ResolvedPackagesDirectory, parked, undo);
                notes.Add("Connector-Pakete ersetzt.");
            }

            if (sections.HasFlag(BackupSections.Config))
            {
                MoveFileIntoPlace(
                    Path.Combine(staging, "config", "instance.json"),
                    _options.ResolvedInstanceConfigPath,
                    parked,
                    undo);
                notes.Add("Instanzkonfiguration ersetzt.");
            }
        }
        catch
        {
            while (undo.Count > 0)
            {
                var step = undo.Pop();
                try
                {
                    step();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Der ursprüngliche Fehler ist wichtiger als ein gescheiterter Rückbauschritt.
                }
            }

            throw;
        }
    }

    private static void MoveFileIntoPlace(string staged, string target, string parked, Stack<Action> undo)
    {
        if (!File.Exists(staged))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            var aside = Path.Combine(parked, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(target));
            File.Move(target, aside);
            undo.Push(() =>
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(aside, target);
            });
        }

        File.Move(staged, target);
        undo.Push(() => BackupService.TryDelete(target));
    }

    private static void MoveDirectoryIntoPlace(string staged, string target, string parked, Stack<Action> undo)
    {
        if (!Directory.Exists(staged))
        {
            return;
        }

        if (Directory.Exists(target))
        {
            var aside = Path.Combine(parked, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(target));
            Directory.Move(target, aside);
            undo.Push(() =>
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                Directory.Move(aside, target);
            });
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target.TrimEnd(Path.DirectorySeparatorChar))!);
        }

        Directory.Move(staged, target);
        undo.Push(() => BackupService.TryDeleteDirectory(target));
    }
}
