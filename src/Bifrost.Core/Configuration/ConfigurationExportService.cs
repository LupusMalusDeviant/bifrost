using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Execution;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Execution;

namespace Bifrost.Core.Configuration;

/// <summary>
/// Konfigurationsexport und -import (ADR-0024 E8, WP2.5).
/// <para>
/// <b>Der Standardexport enthält keine Zugangsdaten</b>, sondern an jeder Stelle, an der eines
/// gebraucht wird, eine Referenz. Das ist der Zweck dieses Dienstes: Jemand exportiert „die
/// Konfiguration" und legt sie in ein Git-Repository — genau dann darf dort nichts stehen, was
/// Zugriff gewährt.
/// </para>
/// <para>
/// <b>Der Import ist zweistufig und ausschließlich additiv.</b>
/// <see cref="PlanImportAsync"/> sagt vorher, was entstünde, was kollidiert und was fehlt;
/// <see cref="ApplyImportAsync"/> wendet an oder nimmt zurück. Nichts wird überschrieben, nichts
/// wird gelöscht — auch nicht Objekte, die im Export fehlen. Ein Import, der aufräumt, wäre ein
/// Restore, und dafür gibt es einen anderen Weg (ADR-0024 E5).
/// </para>
/// </summary>
public sealed class ConfigurationExportService : IConfigurationExportService
{
    /// <summary>Version des Exportformats — nicht die des Produkts.</summary>
    public const int FormatVersion = 1;

    private readonly IConfigurationSnapshotSource _source;
    private readonly IConfigurationImportTarget _target;
    private readonly TimeProvider _time;
    private readonly string _productVersion;
    private readonly IHostExecutionPolicy _hostExecution;

    /// <summary>
    /// Die Vorbereitung zu einem ausgegebenen Plan.
    /// <para>
    /// <b>Warum eine Seitentabelle:</b> Die Nutzlast bleibt hier, der Plan trägt nur ein Handle
    /// (<see cref="ConfigurationImportPlan.Token"/>). Bei einem Vollimport stehen in dieser Tabelle
    /// entschlüsselte Zugangsdaten — sie gehören nicht in eine Antwort, die durch Logs und
    /// Zwischenspeicher läuft. Ein unbekanntes oder abgelaufenes Handle führt zu einer klaren
    /// Absage statt zu einem Import auf geratenen Daten.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, PreparedImport> _prepared = new(StringComparer.Ordinal);

    /// <param name="hostExecution">
    /// Die Ausführungs-Policy (ADR-0025 E4). Der Konfigurationsimport ist ein Erzeugungsweg wie das
    /// Formular — nur ohne jemanden, der die Werte gesehen hat.
    /// </param>
    public ConfigurationExportService(
        IConfigurationSnapshotSource source,
        IConfigurationImportTarget target,
        TimeProvider? timeProvider = null,
        string? productVersion = null,
        IHostExecutionPolicy? hostExecution = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        _hostExecution = hostExecution ?? HostExecutionPolicy.Unresolved;
        _source = source;
        _target = target;
        _time = timeProvider ?? TimeProvider.System;
        _productVersion = productVersion ?? BifrostProductInfo.Version;
    }

    public async Task<ConfigurationExport> ExportAsync(
        ConfigurationExportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ADR-0024 E8: „Ein vollständiger Export MIT Geheimnissen ist möglich, aber verschlüsselt."
        // Kein Schalter, der den Klartext-Vollexport erlaubt — anders als beim Backup, wo ein
        // unverschlüsseltes Archiv auf ein bereits verschlüsseltes Ziel ein realer Fall ist. Ein
        // Konfigurationsexport geht seinem Zweck nach in ein Repository.
        if (request.IncludeSecrets && string.IsNullOrWhiteSpace(request.Passphrase))
        {
            throw new ArgumentException(
                "Ein Export mit Zugangsdaten entsteht nur verschlüsselt. Ohne Passphrase gibt es "
                + "keinen Credential-Export — für einen Export ohne Zugangsdaten IncludeSecrets weglassen.",
                nameof(request));
        }

        var snapshot = await _source.ReadAsync(ct).ConfigureAwait(false);
        var createdAt = _time.GetUtcNow();
        var document = ConfigurationDocumentBuilder.Build(
            snapshot, request.IncludeSecrets, _productVersion, createdAt);

        var payload = string.IsNullOrWhiteSpace(request.Passphrase)
            ? JsonSerializer.Serialize(document, ConfigurationExportJson.Options)
            : JsonSerializer.Serialize(
                ConfigurationCrypto.Encrypt(document, request.Passphrase), ConfigurationExportJson.Options);

        return new ConfigurationExport(
            FormatVersion, _productVersion, createdAt, document.ContainsSecrets, payload);
    }

    [HostExecutionChecked(Note = "ueber PlanUpstreams")]
    public async Task<ConfigurationImportPlan> PlanImportAsync(
        string payload, string? passphrase, CancellationToken ct)
    {
        var document = ParsePayload(payload, passphrase);
        var current = await _source.ReadAsync(ct).ConfigureAwait(false);

        // Gleiches gegen Gleiches: Ist die Nutzlast bereinigt, wird auch der Ist-Zustand bereinigt
        // verglichen. Sonst wäre jeder Upstream mit Zugangsdaten immer „anders" und damit immer
        // ein Konflikt.
        var currentDocument = ConfigurationDocumentBuilder.Build(
            current, document.ContainsSecrets, _productVersion, _time.GetUtcNow());

        var additions = new List<string>();
        var unchanged = new List<string>();
        var conflicts = new List<string>();
        var missing = new List<string>();

        var upstreams = PlanUpstreams(
            document, currentDocument, additions, unchanged, conflicts, _hostExecution);
        var roles = PlanRoles(document, currentDocument, additions, unchanged, conflicts);
        var profiles = PlanProfiles(document, currentDocument, additions, unchanged, conflicts);
        var guardRules = PlanGuardRules(document, currentDocument, additions, unchanged, conflicts);
        var approvals = PlanApprovals(document, currentDocument, additions, unchanged, conflicts);
        var skills = PlanSkills(document, currentDocument, additions, unchanged, conflicts);
        var settings = PlanSettings(document, currentDocument, additions, unchanged, conflicts);

        CheckDependencies(document, currentDocument, missing);

        var plan = new ConfigurationImportPlan(
            CanApply: conflicts.Count == 0 && missing.Count == 0,
            Additions: additions,
            Conflicts: conflicts,
            MissingDependencies: missing,
            Unchanged: unchanged,
            Token: PlanTokens.New());

        _prepared[plan.Token!] = new PreparedImport(
            upstreams, roles, profiles, guardRules, approvals, skills, settings, current.Settings,
            _time.GetUtcNow());

        return plan;
    }

    /// <summary>
    /// Wendet an — oder nimmt zurück, was schon angewendet war.
    /// <para>
    /// <b>Warum Kompensation und keine Transaktion:</b> Upstreams, RBAC, Guard-Regeln, Freigaben und
    /// Skills liegen in verschiedenen Ablagen, über die es keine gemeinsame Klammer gibt. Tragfähig
    /// ist die Kompensation hier deshalb, weil der Import <b>nur anlegt</b>: Jede Rücknahme entfernt
    /// genau das Objekt, das dieser Vorgang Sekunden zuvor angelegt hat, und kann damit nichts
    /// treffen, was vorher da war.
    /// </para>
    /// </summary>
    public async Task ApplyImportAsync(ConfigurationImportPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        DropExpiredImports();
        if (plan.Token is null || !_prepared.TryGetValue(plan.Token, out var prepared))
        {
            throw new ConfigurationImportException(
                "Zu diesem Plan liegt keine Vorbereitung vor. PlanImportAsync muss vorausgehen, auf "
                + "derselben Dienstinstanz und innerhalb von "
                + $"{PlanTokens.Lifetime.TotalMinutes:0} Minuten.");
        }

        // Einmalig: siehe RestoreService — ein noch gültiges Handle lädt zum zweiten Lauf gegen
        // eine Instanz ein, die der Plan nie geprüft hat.
        _prepared.TryRemove(plan.Token, out _);

        if (!plan.CanApply)
        {
            throw new ConfigurationImportException(
                "Der Plan ist nicht anwendbar: "
                + string.Create(CultureInfo.InvariantCulture,
                    $"{plan.Conflicts.Count} Konflikt(e), {plan.MissingDependencies.Count} fehlende Abhängigkeit(en).")
                + " Es wurde nichts geschrieben.");
        }

        var undo = new List<Func<CancellationToken, Task>>();
        try
        {
            foreach (var upstream in prepared.Upstreams)
            {
                await _target.AddUpstreamAsync(upstream.Id, upstream.Config, ct).ConfigureAwait(false);
                undo.Add(token => _target.RemoveUpstreamAsync(upstream.Id, token));
            }

            foreach (var role in prepared.Roles)
            {
                await _target.AddRoleAsync(role, ct).ConfigureAwait(false);
                undo.Add(token => _target.RemoveRoleAsync(role.Id, token));
            }

            foreach (var profile in prepared.Profiles)
            {
                await _target.AddProfileAsync(profile, ct).ConfigureAwait(false);
                undo.Add(token => _target.RemoveProfileAsync(profile.Id, token));
            }

            foreach (var rule in prepared.GuardRules)
            {
                await _target.AddGuardRuleAsync(rule, ct).ConfigureAwait(false);
                undo.Add(token => _target.RemoveGuardRuleAsync(rule.Id, token));
            }

            foreach (var approval in prepared.Approvals)
            {
                await _target.SetApprovalAsync(approval.Tool, approval.Enforcement, ct).ConfigureAwait(false);
                undo.Add(token => _target.SetApprovalAsync(approval.Tool, null, token));
            }

            foreach (var skill in prepared.Skills)
            {
                await _target.AddSkillAsync(skill, ct).ConfigureAwait(false);
                undo.Add(token => _target.RemoveSkillAsync(skill.Id, token));
            }

            if (prepared.Settings is { } settings)
            {
                await _target.ApplySettingsAsync(settings, ct).ConfigureAwait(false);
                undo.Add(token => _target.ApplySettingsAsync(prepared.PreviousSettings, token));
            }
        }
        catch (Exception ex)
        {
            var failures = await RollbackAsync(undo).ConfigureAwait(false);
            var message = failures.Count == 0
                ? "Der Import ist gescheitert und wurde vollständig zurückgenommen. Die Instanz steht "
                    + "wie vorher."
                : "Der Import ist gescheitert. Die Rücknahme war unvollständig — diese Objekte konnten "
                    + "nicht entfernt werden und müssen von Hand geprüft werden: "
                    + string.Join("; ", failures);

            throw new ConfigurationImportException(message, ex);
        }
    }

    /// <summary>
    /// Nimmt in umgekehrter Reihenfolge zurück. Ein Fehler beim Zurücknehmen bricht die Rücknahme
    /// <b>nicht</b> ab: Der Rest ist genauso zu entfernen, und der Rückstand wird gemeldet statt
    /// verschluckt.
    /// </summary>
    private static async Task<IReadOnlyList<string>> RollbackAsync(List<Func<CancellationToken, Task>> undo)
    {
        var failures = new List<string>();
        for (var i = undo.Count - 1; i >= 0; i--)
        {
            try
            {
                // Bewusst ohne den Abbruch-Token des Aufrufers: Eine abgebrochene Rücknahme ist
                // genau der halb angewendete Zustand, den diese Methode verhindern soll.
                await undo[i](CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }
        }

        return failures;
    }

    private static ConfigurationExportDocument ParsePayload(string payload, string? passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationImportException("Die Nutzlast ist kein gültiges JSON.", ex);
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new ConfigurationImportException("Die Nutzlast ist kein Exportdokument.");
            }

            // Verschlüsselt oder nicht steht im Klartext — genau deshalb (ADR-0024 E1).
            var encrypted = parsed.RootElement.TryGetProperty("ciphertext", out _);
            var document = encrypted
                ? DecryptEnvelope(payload, passphrase)
                : Deserialize(payload);

            EnsureSupportedFormat(document.FormatVersion);
            return Normalize(document);
        }
    }

    /// <summary>
    /// Fehlende Abschnitte sind leer, nicht <c>null</c> — sonst müsste jede Auswertung unten die
    /// Frage erneut stellen. Fehlt dagegen der Einstellungsblock, ist das kein Formatdetail, sondern
    /// ein unvollständiges Dokument: fail-closed statt Vorgabewerte erfinden (M2-Vertrag §6.3).
    /// </summary>
    private static ConfigurationExportDocument Normalize(ConfigurationExportDocument document)
    {
        if (document.Settings is null)
        {
            throw new ConfigurationImportException(
                "Dem Export fehlt der Abschnitt 'settings'. Vorgabewerte werden nicht erfunden.");
        }

        return document with
        {
            Upstreams = document.Upstreams ?? [],
            Roles = document.Roles ?? [],
            Profiles = document.Profiles ?? [],
            GuardRules = document.GuardRules ?? [],
            Approvals = document.Approvals ?? [],
            Skills = document.Skills ?? [],
            SecretReferences = document.SecretReferences ?? [],
            NotExportable = document.NotExportable ?? [],
        };
    }

    private static ConfigurationExportDocument DecryptEnvelope(string payload, string? passphrase)
    {
        ConfigurationExportEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ConfigurationExportEnvelope>(
                payload, ConfigurationExportJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationImportException("Der verschlüsselte Export ist beschädigt.", ex);
        }

        if (envelope is null)
        {
            throw new ConfigurationImportException("Der verschlüsselte Export ist leer.");
        }

        EnsureSupportedFormat(envelope.FormatVersion);
        return ConfigurationCrypto.Decrypt(envelope, passphrase);
    }

    private static ConfigurationExportDocument Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ConfigurationExportDocument>(
                    payload, ConfigurationExportJson.Options)
                ?? throw new ConfigurationImportException("Der Export ist leer.");
        }
        catch (JsonException ex)
        {
            throw new ConfigurationImportException("Der Export ist kein gültiges Dokument.", ex);
        }
    }

    private static void EnsureSupportedFormat(int formatVersion)
    {
        if (formatVersion == FormatVersion)
        {
            return;
        }

        // Fail-closed (M2-Vertrag §6.3): Ein unbekanntes Format wird nicht „so gut es geht" gelesen.
        throw new ConfigurationImportException(string.Create(
            CultureInfo.InvariantCulture,
            $"Formatversion {formatVersion} wird nicht unterstützt; diese Instanz liest Version {FormatVersion}."));
    }

    // ── Planung je Objektart ────────────────────────────────────────────────────────────────────

    [HostExecutionChecked]
    private static List<UpstreamSnapshot> PlanUpstreams(
        ConfigurationExportDocument document,
        ConfigurationExportDocument current,
        List<string> additions,
        List<string> unchanged,
        List<string> conflicts,
        IHostExecutionPolicy? hostExecution)
    {
        var bySlug = Index(current.Upstreams, u => u.Slug);
        var byId = current.Upstreams.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<UpstreamSnapshot>();

        foreach (var upstream in document.Upstreams)
        {
            if (byId.TryGetValue(upstream.Id, out var existingById))
            {
                if (Canonical(existingById) == Canonical(upstream))
                {
                    unchanged.Add($"Upstream '{upstream.Slug}': unverändert vorhanden, wird nicht erneut angelegt.");
                }
                else
                {
                    conflicts.Add(
                        $"Upstream '{upstream.Slug}': Auf der Zielinstanz gibt es bereits einen Server mit "
                        + $"derselben Id (Slug '{existingById.Slug}') mit anderem Inhalt. Er wird nicht überschrieben.");
                }

                continue;
            }

            if (bySlug.TryGetValue(upstream.Slug, out _))
            {
                conflicts.Add(
                    $"Upstream '{upstream.Slug}': Der Slug ist auf der Zielinstanz bereits vergeben. "
                    + "Er wird nicht überschrieben — der Slug ist die Namespacing-Basis der Werkzeugnamen (FR-03).");
                continue;
            }

            // ADR-0025 E4: Ein Konfigurationsimport bringt Upstreams mit, die niemand in dieser
            // Instanz eingetippt hat. Der betroffene Upstream wird übersprungen statt der ganze
            // Import abgebrochen — der Rest der Datei ist deshalb nicht falsch, und der Betreiber
            // sieht in der Vorschau genau, was fehlen wird und warum.
            var policy = HostExecutionGuard.Evaluate(hostExecution, upstream.Config);
            if (!policy.Allowed)
            {
                conflicts.Add(
                    $"Upstream '{upstream.Slug}': {policy.Summary} [{policy.ReasonCode}] "
                    + (policy.Remediation ?? string.Empty));
                continue;
            }

            var unresolved = ConfigurationSecretScrubber.FindUnresolvedReferences(upstream.Config);
            var config = upstream.Config;
            if (unresolved.Count > 0)
            {
                // Ein Upstream mit einem Platzhalter als Passwort würde starten, scheitern und einen
                // Fehler melden, der wie ein Netzproblem aussieht. Abgeschaltet anlegen ist die
                // ehrliche Variante — und die Ansage dazu steht in den Zugängen.
                config = config with { Enabled = false };
                additions.Add(
                    $"Upstream '{upstream.Slug}' wird angelegt, aber **abgeschaltet**: "
                    + string.Create(CultureInfo.InvariantCulture, $"{unresolved.Count} ")
                    + "Zugangsdatum/-daten fehlen und müssen nachgetragen werden ("
                    + string.Join(", ", unresolved.Select(r => r.Location)) + ").");
            }
            else
            {
                additions.Add($"Upstream '{upstream.Slug}' wird angelegt.");
            }

            result.Add(new UpstreamSnapshot(new ServerId(upstream.Id), config));
        }

        return result;
    }

    private static List<Role> PlanRoles(
        ConfigurationExportDocument document,
        ConfigurationExportDocument current,
        List<string> additions,
        List<string> unchanged,
        List<string> conflicts)
    {
        var byName = Index(current.Roles, r => r.Name);
        var byId = current.Roles.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<Role>();

        foreach (var role in document.Roles)
        {
            if (byId.TryGetValue(role.Id, out var existingById))
            {
                if (Canonical(existingById) == Canonical(role))
                {
                    unchanged.Add($"Rolle '{role.Name}': unverändert vorhanden, wird nicht erneut angelegt.");
                }
                else
                {
                    conflicts.Add(
                        $"Rolle '{role.Name}': Auf der Zielinstanz gibt es bereits eine Rolle mit derselben "
                        + $"Id (Name '{existingById.Name}') mit anderem Inhalt. Sie wird nicht überschrieben.");
                }

                continue;
            }

            if (byName.ContainsKey(role.Name))
            {
                conflicts.Add(
                    $"Rolle '{role.Name}': Der Name ist auf der Zielinstanz bereits vergeben. "
                    + "Sie wird nicht überschrieben.");
                continue;
            }

            result.Add(ConfigurationDocumentBuilder.ToDomain(role));
            additions.Add($"Rolle '{role.Name}' wird angelegt.");
        }

        return result;
    }

    private static List<ToolProfile> PlanProfiles(
        ConfigurationExportDocument document,
        ConfigurationExportDocument current,
        List<string> additions,
        List<string> unchanged,
        List<string> conflicts)
    {
        var byName = Index(current.Profiles, p => p.Name);
        var byId = current.Profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<ToolProfile>();

        foreach (var profile in document.Profiles)
        {
            if (byId.TryGetValue(profile.Id, out var existingById))
            {
                if (Canonical(existingById) == Canonical(profile))
                {
                    unchanged.Add($"Profil '{profile.Name}': unverändert vorhanden, wird nicht erneut angelegt.");
                }
                else
                {
                    conflicts.Add(
                        $"Profil '{profile.Name}': Auf der Zielinstanz gibt es bereits ein Profil mit derselben "
                        + $"Id (Name '{existingById.Name}') mit anderem Inhalt. Es wird nicht überschrieben.");
                }

                continue;
            }

            if (byName.ContainsKey(profile.Name))
            {
                conflicts.Add(
                    $"Profil '{profile.Name}': Der Name ist auf der Zielinstanz bereits vergeben. "
                    + "Es wird nicht überschrieben.");
                continue;
            }

            result.Add(ConfigurationDocumentBuilder.ToDomain(profile));
            additions.Add($"Profil '{profile.Name}' wird angelegt.");
        }

        return result;
    }

    private static List<GuardRule> PlanGuardRules(
        ConfigurationExportDocument document,
        ConfigurationExportDocument current,
        List<string> additions,
        List<string> unchanged,
        List<string> conflicts)
    {
        var byId = Index(current.GuardRules, r => r.Id);
        var result = new List<GuardRule>();

        foreach (var rule in document.GuardRules)
        {
            if (byId.TryGetValue(rule.Id, out var existing))
            {
                // Der kuratierte Regelsatz ist auf jeder Instanz derselbe. Ihn als Konflikt zu melden
                // hieße, dass kein Export je anwendbar wäre.
                if (Canonical(existing) == Canonical(rule))
                {
                    unchanged.Add($"Guard-Regel '{rule.Id}': unverändert vorhanden, wird nicht erneut angelegt.");
                }
                else
                {
                    conflicts.Add(
                        $"Guard-Regel '{rule.Id}': Auf der Zielinstanz gibt es bereits eine Regel dieser Id "
                        + "mit anderem Inhalt. Sie wird nicht überschrieben.");
                }

                continue;
            }

            result.Add(ConfigurationDocumentBuilder.ToDomain(rule));
            additions.Add($"Guard-Regel '{rule.Id}' wird angelegt.");
        }

        return result;
    }

    private static List<ApprovalSnapshot> PlanApprovals(
        ConfigurationExportDocument document,
        ConfigurationExportDocument current,
        List<string> additions,
        List<string> unchanged,
        List<string> conflicts)
    {
        var byTool = Index(current.Approvals, a => a.Tool);
        var result = new List<ApprovalSnapshot>();

        foreach (var approval in document.Approvals)
        {
            if (byTool.TryGetValue(approval.Tool, out var existing))
            {
                if (existing.Enforcement == approval.Enforcement)
                {
                    unchanged.Add($"Freigabe-Markierung '{approval.Tool}': unverändert vorhanden.");
                }
                else
                {
                    conflicts.Add(
                        $"Freigabe-Markierung '{approval.Tool}': Auf der Zielinstanz ist bereits "
                        + $"'{existing.Enforcement}' hinterlegt, der Export verlangt '{approval.Enforcement}'. "
                        + "Die bestehende Festlegung wird nicht überschrieben.");
                }

                continue;
            }

            result.Add(new ApprovalSnapshot(new NamespacedToolName(approval.Tool), approval.Enforcement));
            additions.Add($"Freigabe-Markierung '{approval.Tool}' ({approval.Enforcement}) wird gesetzt.");
        }

        return result;
    }

    private static List<SkillSnapshot> PlanSkills(
        ConfigurationExportDocument document,
        ConfigurationExportDocument current,
        List<string> additions,
        List<string> unchanged,
        List<string> conflicts)
    {
        var byName = Index(current.Skills, s => s.Name);
        var byId = current.Skills.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<SkillSnapshot>();

        foreach (var skill in document.Skills)
        {
            if (byId.TryGetValue(skill.Id, out var existingById))
            {
                if (Canonical(existingById) == Canonical(skill))
                {
                    unchanged.Add($"Skill '{skill.Name}': unverändert vorhanden, wird nicht erneut angelegt.");
                }
                else
                {
                    conflicts.Add(
                        $"Skill '{skill.Name}': Auf der Zielinstanz gibt es bereits einen Skill mit derselben "
                        + $"Id (Name '{existingById.Name}') mit anderem Inhalt. Er wird nicht überschrieben — "
                        + "ein Import ist keine neue Version.");
                }

                continue;
            }

            if (byName.ContainsKey(skill.Name))
            {
                conflicts.Add(
                    $"Skill '{skill.Name}': Der Name ist auf der Zielinstanz bereits vergeben. "
                    + "Er wird nicht überschrieben — über den Namen finden Agenten den Skill.");
                continue;
            }

            result.Add(ConfigurationDocumentBuilder.ToDomain(skill));
            additions.Add($"Skill '{skill.Name}' wird angelegt.");
        }

        return result;
    }

    /// <summary>
    /// Einstellungen sind kein Objekt, das man anlegt, sondern eine Festlegung, die schon gilt.
    /// <para>
    /// Deshalb die einzige Regel, die anders aussieht als die übrigen: Steht die Zielinstanz noch im
    /// Auslieferungszustand, werden die Einstellungen des Exports übernommen. Hat dort jemand etwas
    /// anderes eingestellt, ist das eine Entscheidung — und die wird gemeldet, nicht überfahren.
    /// </para>
    /// </summary>
    private static InstanceSettings? PlanSettings(
        ConfigurationExportDocument document,
        ConfigurationExportDocument current,
        List<string> additions,
        List<string> unchanged,
        List<string> conflicts)
    {
        if (document.Settings == current.Settings)
        {
            unchanged.Add("Einstellungen: unverändert.");
            return null;
        }

        var defaults = ConfigurationDocumentBuilder.ToExported(InstanceSettings.Defaults);
        if (current.Settings != defaults)
        {
            conflicts.Add(
                "Einstellungen: Auf der Zielinstanz weichen sie vom Auslieferungszustand ab und "
                + "unterscheiden sich von denen des Exports. Sie werden nicht überschrieben.");
            return null;
        }

        additions.Add("Einstellungen werden aus dem Export übernommen (die Zielinstanz steht im Auslieferungszustand).");
        return ConfigurationDocumentBuilder.ToDomain(document.Settings);
    }

    /// <summary>
    /// Verweise, die ins Leere zeigen würden.
    /// <para>
    /// Geprüft wird dort, wo ein hängender Verweis die <b>Autorisierung</b> verändert: Grants einer
    /// Rolle und angeheftete Werkzeuge eines Profils. Freigabe-Markierungen und Skill-Referenzen sind
    /// Anmerkungen — ein Werkzeug darf vor seinem Server konfiguriert werden, und
    /// <c>ISkillValidator</c> hält Skill-Verweise ausdrücklich für Warnungen, nicht für Fehler. Wer
    /// das hier zum Blocker machte, würde Importe verhindern, die vollkommen in Ordnung sind.
    /// </para>
    /// </summary>
    private static void CheckDependencies(
        ConfigurationExportDocument document,
        ConfigurationExportDocument current,
        List<string> missing)
    {
        var knownIds = new HashSet<Guid>(document.Upstreams.Select(u => u.Id));
        knownIds.UnionWith(current.Upstreams.Select(u => u.Id));

        var knownSlugs = new HashSet<string>(document.Upstreams.Select(u => u.Slug), StringComparer.OrdinalIgnoreCase);
        knownSlugs.UnionWith(current.Upstreams.Select(u => u.Slug));

        foreach (var role in document.Roles)
        {
            foreach (var grant in role.Grants)
            {
                if (grant.Server is { } server && !knownIds.Contains(server))
                {
                    missing.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Rolle '{role.Name}' berechtigt auf den Upstream {server:N}, den es weder im Export "
                        + $"noch auf der Zielinstanz gibt."));
                }

                if (grant.Tool is { } tool && !IsKnownTool(tool, knownSlugs))
                {
                    missing.Add(
                        $"Rolle '{role.Name}' berechtigt auf das Werkzeug '{tool}', dessen Upstream weder im "
                        + "Export noch auf der Zielinstanz existiert.");
                }
            }
        }

        foreach (var profile in document.Profiles)
        {
            foreach (var tool in profile.PinnedTools)
            {
                if (!IsKnownTool(tool, knownSlugs))
                {
                    missing.Add(
                        $"Profil '{profile.Name}' heftet das Werkzeug '{tool}' an, dessen Upstream weder im "
                        + "Export noch auf der Zielinstanz existiert.");
                }
            }
        }
    }

    private static bool IsKnownTool(string tool, HashSet<string> knownSlugs)
        => new NamespacedToolName(tool).TrySplit(out var slug, out _) && knownSlugs.Contains(slug);

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            result[key(item)] = item;
        }

        return result;
    }

    private static string Canonical<T>(T value)
        => JsonSerializer.Serialize(value, ConfigurationExportJson.ComparisonOptions);

    private sealed record PreparedImport(
        IReadOnlyList<UpstreamSnapshot> Upstreams,
        IReadOnlyList<Role> Roles,
        IReadOnlyList<ToolProfile> Profiles,
        IReadOnlyList<GuardRule> GuardRules,
        IReadOnlyList<ApprovalSnapshot> Approvals,
        IReadOnlyList<SkillSnapshot> Skills,
        InstanceSettings? Settings,
        InstanceSettings PreviousSettings,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// Räumt abgelaufene Vormerkungen weg. Ein Vollimport hält entschlüsselte Zugangsdaten im
    /// Arbeitsspeicher — ein Plan, den niemand mehr anwendet, darf sie nicht bis zum Prozessende
    /// festhalten.
    /// </summary>
    private void DropExpiredImports()
    {
        var deadline = _time.GetUtcNow() - PlanTokens.Lifetime;
        foreach (var (token, prepared) in _prepared)
        {
            if (prepared.CreatedAt < deadline)
            {
                _prepared.TryRemove(token, out _);
            }
        }
    }
}
