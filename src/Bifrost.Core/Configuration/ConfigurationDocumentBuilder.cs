using System.Globalization;
using Bifrost.Abstractions;

namespace Bifrost.Core.Configuration;

/// <summary>
/// Bildet einen Instanzzustand auf das Exportdokument ab — <b>die einzige Stelle</b>, an der das
/// passiert.
/// <para>
/// Auch die Konfliktprüfung des Imports läuft darüber: Sie vergleicht das eingelesene Dokument mit
/// dem Dokument, das die Zielinstanz gerade ergäbe. Zwei Abbildungen (eine fürs Schreiben, eine
/// fürs Vergleichen) würden auseinanderlaufen, und der Import meldete Konflikte, wo keine sind —
/// oder schlimmer, keine, wo welche sind.
/// </para>
/// </summary>
internal static class ConfigurationDocumentBuilder
{
    public static ConfigurationExportDocument Build(
        ConfigurationSnapshot snapshot,
        bool includeSecrets,
        string productVersion,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var upstreams = new List<ExportedUpstream>(snapshot.Upstreams.Count);
        var references = new List<SecretPlaceholder>();

        foreach (var upstream in snapshot.Upstreams.OrderBy(u => u.Config.Slug, StringComparer.Ordinal))
        {
            if (includeSecrets)
            {
                upstreams.Add(new ExportedUpstream(upstream.Id.Value, upstream.Config.Slug, upstream.Config));
                continue;
            }

            var scrubbed = ConfigurationSecretScrubber.Scrub(upstream.Config.Slug, upstream.Config);
            upstreams.Add(new ExportedUpstream(upstream.Id.Value, upstream.Config.Slug, scrubbed.Config));
            references.AddRange(scrubbed.References);
        }

        return new ConfigurationExportDocument(
            ConfigurationExportService.FormatVersion,
            productVersion,
            createdAt,
            includeSecrets,
            upstreams,
            [.. snapshot.Roles.OrderBy(r => r.Name, StringComparer.Ordinal).Select(ToExported)],
            [.. snapshot.Profiles.OrderBy(p => p.Name, StringComparer.Ordinal).Select(ToExported)],
            [.. snapshot.GuardRules.OrderBy(r => r.Id, StringComparer.Ordinal).Select(ToExported)],
            [.. snapshot.Approvals.OrderBy(a => a.Tool.Value, StringComparer.Ordinal)
                .Select(a => new ExportedApproval(a.Tool.Value, a.Enforcement))],
            [.. snapshot.Skills.OrderBy(s => s.Name, StringComparer.Ordinal).Select(ToExported)],
            ToExported(snapshot.Settings),
            references,
            [.. Notes(snapshot.NonPortable)]);
    }

    public static ExportedSettings ToExported(InstanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ExportedSettings(
            settings.Guard.Enabled,
            settings.Guard.MaxScanChars,
            (int)settings.Guard.MatchTimeout.TotalMilliseconds,
            settings.Guard.AllowCustomPatterns,
            settings.DefaultApprovalEnforcement);
    }

    public static InstanceSettings ToDomain(ExportedSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new InstanceSettings(
            new GuardOptions
            {
                Enabled = settings.GuardEnabled,
                MaxScanChars = settings.GuardMaxScanChars,
                MatchTimeout = TimeSpan.FromMilliseconds(settings.GuardMatchTimeoutMs),
                AllowCustomPatterns = settings.GuardAllowCustomPatterns,
            },
            settings.DefaultApprovalEnforcement);
    }

    public static Role ToDomain(ExportedRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return new Role(
            new RoleId(role.Id),
            role.Name,
            [.. role.Grants.Select(g => new Grant(
                new PermissionScope(
                    g.Server is { } server ? new ServerId(server) : null,
                    g.Tool is { } tool ? new NamespacedToolName(tool) : null),
                [.. g.Actions]))],
            role.RateLimitPerMinute is { } limit ? new RateLimit(limit) : null);
    }

    public static ToolProfile ToDomain(ExportedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ToolProfile(
            new ProfileId(profile.Id),
            profile.Name,
            [.. profile.PinnedTools.Select(t => new NamespacedToolName(t))],
            profile.LazyToolsEnabled);
    }

    public static GuardRule ToDomain(ExportedGuardRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new GuardRule(
            rule.Id, rule.Description, rule.Pattern, rule.Keyword, rule.Direction, rule.Mode,
            rule.Enabled, rule.IsCustom);
    }

    public static SkillSnapshot ToDomain(ExportedSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return new SkillSnapshot(
            new AssetId(skill.Id),
            skill.Name,
            skill.Description,
            skill.Content,
            skill.Metadata is { } metadata
                ? new SkillMetadata(metadata.WhenToUse, metadata.References, metadata.RequiredTools)
                : SkillMetadata.Empty);
    }

    private static ExportedRole ToExported(Role role) => new(
        role.Id.Value,
        role.Name,
        [.. role.Grants.Select(g => new ExportedGrant(g.Scope.Server?.Value, g.Scope.Tool?.Value, [.. g.Actions]))],
        role.RateLimit?.CallsPerMinute);

    private static ExportedProfile ToExported(ToolProfile profile) => new(
        profile.Id.Value,
        profile.Name,
        [.. profile.PinnedTools.Select(t => t.Value)],
        profile.LazyToolsEnabled);

    private static ExportedGuardRule ToExported(GuardRule rule) => new(
        rule.Id, rule.Description, rule.Pattern, rule.Keyword, rule.Direction, rule.Mode,
        rule.Enabled, rule.IsCustom);

    private static ExportedSkill ToExported(SkillSnapshot skill) => new(
        skill.Id.Value,
        skill.Name,
        skill.Description,
        skill.Content,
        skill.Metadata.IsEmpty
            ? null
            : new ExportedSkillMetadata(
                skill.Metadata.WhenToUse,
                skill.Metadata.References,
                skill.Metadata.RequiredTools));

    private static IEnumerable<NotExportableNote> Notes(NonPortableInventory inventory)
    {
        if (inventory.Identities > 0)
        {
            yield return new NotExportableNote(
                Count(inventory.Identities, "Identität", "Identitäten"),
                "Eine Identität ist ein Aufrufer dieser Instanz, kein Konfigurationsobjekt. Ohne ihre "
                + "API-Keys wäre sie auf der Zielinstanz ein Name ohne Zugang — und die Keys liegen nur "
                + "als Hash vor und lassen sich nicht übertragen. Rollen und Profile, an denen die "
                + "Berechtigungen hängen, gehen mit; die Zuordnung legt der Betreiber neu an.");
        }

        if (inventory.ApiKeys > 0)
        {
            yield return new NotExportableNote(
                Count(inventory.ApiKeys, "API-Key", "API-Keys"),
                "Liegen ausschließlich als Hash vor (FR-27). Ein Hash ist auf der Zielinstanz kein "
                + "benutzbarer Schlüssel, und der Klartext existiert nirgends mehr.");
        }

        if (inventory.Webhooks > 0)
        {
            yield return new NotExportableNote(
                Count(inventory.Webhooks, "Webhook", "Webhooks"),
                "Das HMAC-Secret muss zum Nachrechnen der Signatur im Klartext vorliegen und ist damit "
                + "genau das, was ein Konfigurationsexport nicht enthalten darf (ADR-0013/ADR-0024 E8). "
                + "Ein Webhook ohne sein Secret wäre ein Eingang, der nichts prüft — deshalb geht er gar "
                + "nicht mit.");
        }

        if (inventory.UpstreamOAuthTokens > 0)
        {
            yield return new NotExportableNote(
                Count(inventory.UpstreamOAuthTokens, "OAuth-Token", "OAuth-Token"),
                "Access- und Refresh-Token sind an Instanz, Issuer und Redirect-URI gebunden und "
                + "erneuern sich laufend. Auf der Zielinstanz wird die Autorisierung neu durchlaufen.");
        }
    }

    private static string Count(int value, string singular, string plural)
        => string.Create(CultureInfo.InvariantCulture, $"{value} {(value == 1 ? singular : plural)}");
}
