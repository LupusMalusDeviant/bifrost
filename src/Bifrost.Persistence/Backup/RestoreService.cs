using System.Buffers;
using System.Collections.Concurrent;
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
/// <b>Warum der Plan Archivpfad und Passphrase nicht trägt:</b> Eine Passphrase, die durch eine
/// API-Antwort läuft, steht danach in jedem Log. Der Plan trägt stattdessen ein Handle
/// (<see cref="RestorePlan.Token"/>); der Zustand bleibt hier. Plan und Anwendung müssen dieselbe
/// Dienstinstanz sehen — über eine HTTP-Schnittstelle ist das der Server, und der Plan überlebt den
/// Weg als JSON. Ein unbekanntes oder abgelaufenes Handle wird abgewiesen statt geraten.
/// </para>
/// </summary>
public sealed class RestoreService : IRestoreService
{
    private readonly BackupOptions _options;
    private readonly IBackupService _backupService;
    private readonly ConcurrentDictionary<string, PlanContext> _contexts = new(StringComparer.Ordinal);

    public RestoreService(BackupOptions options, IBackupService backupService)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backupService);
        _options = options;
        _backupService = backupService;
    }

    private sealed record PlanContext(string ArchivePath, string? Passphrase, DateTimeOffset CreatedAt);

    /// <summary>
    /// Die zweite und tragende Hälfte des Rückwärts-Tors aus ADR-0024 E6.
    /// <para>
    /// Der Versionsvergleich darüber hat allein nicht gehalten: Die Mindestversion im Manifest ist
    /// eine Angabe des Archivs über sich selbst, und sie stand für jedes Archiv auf demselben Wert.
    /// Ein Archiv aus einer neueren Instanz kam damit durch und wurde eingespielt; aufgehalten hat
    /// es erst der nächste Start — also nachdem geschrieben wurde, was E6 verhindern sollte.
    /// Gefunden hat das WP2.6, indem er gegen die Zusage getestet hat statt für sie.
    /// </para>
    /// </summary>
    private void CheckMigrationIsKnown(
        BackupManifestDocument manifest, List<string> blockers, List<string> warnings)
    {
        var migration = manifest.Database.Migration;
        if (string.IsNullOrWhiteSpace(migration))
        {
            // Leere Datenbank zum Sicherungszeitpunkt — es gibt keinen Stand, der zu neu sein könnte.
            return;
        }

        if (_options.KnownMigrationIds.Count == 0)
        {
            warnings.Add(
                "Der Migrationsstand des Archivs konnte nicht geprüft werden, weil dieser Aufruf die "
                + "bekannten Migrationen nicht mitgegeben hat. Ein Archiv aus einer neueren Version "
                + "würde hier nicht auffallen.");
            return;
        }

        if (!_options.KnownMigrationIds.Contains(migration))
        {
            blockers.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Das Archiv trägt den Migrationsstand '{0}', den diese Installation nicht kennt. Es "
                + "stammt damit aus einer neueren Version. Ein Rückspielen wird abgelehnt, nicht "
                + "versucht — ein neueres Schema mit älteren Regeln zu bedienen fällt später und "
                + "woanders auf.",
                migration));
        }
    }

    /// <summary>
    /// Räumt abgelaufene Vormerkungen weg. Sie enthalten Passphrasen — ein Plan, den niemand mehr
    /// anwenden wird, darf sie nicht bis zum Prozessende festhalten.
    /// </summary>
    private void DropExpiredContexts()
    {
        var deadline = DateTimeOffset.UtcNow - PlanTokens.Lifetime;
        foreach (var (token, context) in _contexts)
        {
            if (context.CreatedAt < deadline)
            {
                _contexts.TryRemove(token, out _);
            }
        }
    }

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

        var targetIsEmpty = await IsTargetEmptyAsync(ct).ConfigureAwait(false);
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
            preBackupPath,
            Token: PlanTokens.New());

        _contexts[plan.Token!] = new PlanContext(
            archivePath, request.Passphrase, DateTimeOffset.UtcNow);
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

        // ADR-0024 E6, erste Hälfte. Sie allein hält nicht — siehe CheckMigrationIsKnown.
        if (!ProductVersionOrder.IsAtLeast(_options.ProductVersion, manifest.MinimumRestoreVersion, out var problem))
        {
            blockers.Add(problem ?? string.Format(
                CultureInfo.InvariantCulture,
                "Das Archiv verlangt mindestens Version {0}, diese Installation ist {1}. " +
                "Ein Rückspielen in eine ältere Version wird abgelehnt, nicht versucht.",
                manifest.MinimumRestoreVersion,
                _options.ProductVersion));
        }

        CheckMigrationIsKnown(manifest, blockers, warnings);

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
        else if (provider == DatabaseProvider.Postgres
            && manifest.Sections.Any(s => string.Equals(s, "database", StringComparison.OrdinalIgnoreCase))
            && !PostgresTools.TryLocate(_options.PostgresToolDirectory, out _))
        {
            // ADR-0024 E2: Fehlt das Werkzeug, ist das ein Blocker mit Meldung — geprüft in der
            // VORprüfung, also bevor irgendetwas entpackt wurde.
            //
            // Nur bei einem Archiv MIT Datenbankbereich: Ein Archiv, das nur den Key-Ring oder die
            // Pakete trägt, braucht kein pg_restore. Es daran scheitern zu lassen wäre eine Hürde
            // ohne Grund — und ausgerechnet in der Lage, in der jemand einen Schlüsselring
            // zurückholen will.
            blockers.Add(PostgresTools.MissingMessage);
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
    private async Task<bool> IsTargetEmptyAsync(CancellationToken ct)
    {
        if (_options.Provider is DatabaseProvider.Postgres)
        {
            // Bei PostgreSQL liegt die Datenbank nicht im Datenverzeichnis. „Leer" heißt hier: keine
            // Tabellen außerhalb der Systemschemata.
            if (!await PostgresBackup
                    .IsEmptyAsync(_options.RequiredPostgresConnectionString, ct)
                    .ConfigureAwait(false))
            {
                return false;
            }
        }
        else
        {
            var database = _options.ResolvedSqliteFile;
            if (File.Exists(database) && new FileInfo(database).Length > 0)
            {
                return false;
            }
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
        DropExpiredContexts();
        if (plan.Token is null || !_contexts.TryGetValue(plan.Token, out var context))
        {
            throw new InvalidOperationException(
                "Zu diesem Plan ist kein Archiv bekannt. PlanAsync muss vorausgehen, auf derselben "
                + $"Dienstinstanz und innerhalb von {PlanTokens.Lifetime.TotalMinutes:0} Minuten.");
        }

        if (!plan.CanApply)
        {
            throw new InvalidOperationException(
                "Ein Plan mit Blockern wird nicht angewendet: " + string.Join(" | ", plan.Blockers));
        }

        // Einmalig: Ein Handle, das nach der Anwendung noch gilt, lässt sich wiederverwenden — und
        // der zweite Lauf träfe eine Instanz, die der Plan nie geprüft hat.
        _contexts.TryRemove(plan.Token, out _);

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

            await SwitchOverAsync(staging, sections, plan, notes, ct).ConfigureAwait(false);
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
    private async Task SwitchOverAsync(
        string staging,
        BackupSections sections,
        RestorePlan plan,
        List<string> notes,
        CancellationToken ct)
    {
        var parked = Path.Combine(staging, ".replaced");
        Directory.CreateDirectory(parked);
        var undo = new Stack<Action>();

        try
        {
            // Die Dateizonen zuerst, die Datenbank zuletzt. Grund: Ein Fehlschlag in den Dateizonen
            // lässt sich rückgängig machen, ein eingespielter Datenbankstand nicht — der hat als
            // Ausweg nur die Vorsicherung. Also wird das Unumkehrbare als Letztes getan.
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

            if (sections.HasFlag(BackupSections.Database))
            {
                await RestoreDatabaseAsync(staging, plan, parked, undo, notes, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Der Datenbankteil des Umschaltens.
    /// <para>
    /// Bei SQLite ist es ein Umbenennen und damit rückbaubar. Bei PostgreSQL ist es ein
    /// <c>pg_restore</c> — und der lässt sich nicht durch ein Umbenennen zurücknehmen. Er läuft
    /// deshalb unter <c>--single-transaction</c>: Entweder er ist ganz durch, oder die Datenbank ist
    /// unberührt (ADR-0024 E5). Der Ausweg <em>nach</em> einem erfolgreichen Einspielen ist die
    /// Vorsicherung, die bei <c>Replace</c> vorher entstanden ist — nicht der Rückbaustapel.
    /// </para>
    /// </summary>
    private async Task RestoreDatabaseAsync(
        string staging,
        RestorePlan plan,
        string parked,
        Stack<Action> undo,
        List<string> notes,
        CancellationToken ct)
    {
        if (_options.Provider is DatabaseProvider.Postgres)
        {
            var dump = Path.Combine(staging, "database", "bifrost.dump");
            if (!File.Exists(dump))
            {
                return;
            }

            // '--clean' nur, wenn im Ziel wirklich etwas steht: Auf ein leeres Ziel wäre es die
            // Aufforderung, Objekte zu löschen, die es nicht gibt.
            await PostgresBackup
                .RestoreAsync(
                    _options.RequiredPostgresConnectionString,
                    dump,
                    clean: !plan.TargetIsEmpty,
                    _options.PostgresToolDirectory,
                    ct)
                .ConfigureAwait(false);

            notes.Add(plan.TargetIsEmpty
                ? "Datenbank aus dem pg_dump eingespielt (leeres Ziel, eine Transaktion)."
                : "Datenbank aus dem pg_dump eingespielt; vorhandene Objekte wurden zuvor entfernt "
                  + "(eine Transaktion). Der Rückweg ist die Vorsicherung, nicht ein Rückbau.");
            return;
        }

        var target = _options.ResolvedSqliteFile;
        MoveFileIntoPlace(Path.Combine(staging, "database", "bifrost.db"), target, parked, undo);
        SqliteSnapshot.RemoveSidecars(target);
        notes.Add("Datenbank ersetzt; liegengebliebene WAL-Begleiter entfernt.");
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
