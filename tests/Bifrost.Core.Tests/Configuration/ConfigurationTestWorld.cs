using Bifrost.Abstractions;
using Bifrost.Core.Configuration;

namespace Bifrost.Core.Tests.Configuration;

/// <summary>
/// Eine Instanz im Arbeitsspeicher: Lesequelle und Importziel in einem, wie es auf einer echten
/// Instanz auch ist. Der Schreibpfad ist bewusst so streng wie die echte Ablage — wer zweimal
/// denselben Slug anlegt, bekommt einen Fehler statt einer stillen Verdrängung; sonst würde ein
/// Test grün, den die Wirklichkeit rot färbt.
/// </summary>
internal sealed class FakeInstance : IConfigurationSnapshotSource, IConfigurationImportTarget
{
    public List<UpstreamSnapshot> Upstreams { get; } = [];

    public List<Role> Roles { get; } = [];

    public List<ToolProfile> Profiles { get; } = [];

    public List<GuardRule> GuardRules { get; } = [];

    public Dictionary<string, ApprovalEnforcement> Approvals { get; } = new(StringComparer.Ordinal);

    public List<SkillSnapshot> Skills { get; } = [];

    public InstanceSettings Settings { get; set; } = InstanceSettings.Defaults;

    public NonPortableInventory NonPortable { get; set; } = NonPortableInventory.None;

    /// <summary>
    /// Name/Slug/Id, bei dessen Anlegen der Schreibvorgang scheitert — für den Nachweis, dass ein
    /// Teilfehler nichts stehen lässt.
    /// </summary>
    public string? FailWhenAdding { get; set; }

    /// <summary>Lässt jede Rücknahme scheitern — für den Nachweis, dass ein Rückstand gemeldet wird.</summary>
    public bool FailOnRollback { get; set; }

    /// <summary>Zählt jeden erfolgreichen Schreibvorgang — inklusive der Rücknahmen.</summary>
    public int Writes { get; private set; }

    public bool IsEmpty
        => Upstreams.Count == 0
            && Roles.Count == 0
            && Profiles.Count == 0
            && GuardRules.Count == 0
            && Approvals.Count == 0
            && Skills.Count == 0
            && Settings == InstanceSettings.Defaults;

    public Task<ConfigurationSnapshot> ReadAsync(CancellationToken ct)
        => Task.FromResult(new ConfigurationSnapshot(
            [.. Upstreams],
            [.. Roles],
            [.. Profiles],
            [.. GuardRules],
            [.. Approvals.Select(p => new ApprovalSnapshot(new NamespacedToolName(p.Key), p.Value))],
            [.. Skills],
            Settings,
            NonPortable));

    public Task AddUpstreamAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        Guard(config.Slug);
        if (Upstreams.Any(u => u.Id == id || string.Equals(u.Config.Slug, config.Slug, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Upstream '{config.Slug}' existiert bereits.");
        }

        Upstreams.Add(new UpstreamSnapshot(id, config));
        Writes++;
        return Task.CompletedTask;
    }

    public Task RemoveUpstreamAsync(ServerId id, CancellationToken ct)
    {
        GuardRollback();
        Upstreams.RemoveAll(u => u.Id == id);
        Writes++;
        return Task.CompletedTask;
    }

    public Task AddRoleAsync(Role role, CancellationToken ct)
    {
        Guard(role.Name);
        Roles.Add(role);
        Writes++;
        return Task.CompletedTask;
    }

    public Task RemoveRoleAsync(RoleId id, CancellationToken ct)
    {
        Roles.RemoveAll(r => r.Id == id);
        Writes++;
        return Task.CompletedTask;
    }

    public Task AddProfileAsync(ToolProfile profile, CancellationToken ct)
    {
        Guard(profile.Name);
        Profiles.Add(profile);
        Writes++;
        return Task.CompletedTask;
    }

    public Task RemoveProfileAsync(ProfileId id, CancellationToken ct)
    {
        Profiles.RemoveAll(p => p.Id == id);
        Writes++;
        return Task.CompletedTask;
    }

    public Task AddGuardRuleAsync(GuardRule rule, CancellationToken ct)
    {
        Guard(rule.Id);
        GuardRules.Add(rule);
        Writes++;
        return Task.CompletedTask;
    }

    public Task RemoveGuardRuleAsync(string ruleId, CancellationToken ct)
    {
        GuardRules.RemoveAll(r => string.Equals(r.Id, ruleId, StringComparison.Ordinal));
        Writes++;
        return Task.CompletedTask;
    }

    public Task AddSkillAsync(SkillSnapshot skill, CancellationToken ct)
    {
        Guard(skill.Name);
        Skills.Add(skill);
        Writes++;
        return Task.CompletedTask;
    }

    public Task RemoveSkillAsync(AssetId id, CancellationToken ct)
    {
        Skills.RemoveAll(s => s.Id == id);
        Writes++;
        return Task.CompletedTask;
    }

    public Task SetApprovalAsync(NamespacedToolName tool, ApprovalEnforcement? enforcement, CancellationToken ct)
    {
        Guard(tool.Value);
        if (enforcement is { } value)
        {
            Approvals[tool.Value] = value;
        }
        else
        {
            Approvals.Remove(tool.Value);
        }

        Writes++;
        return Task.CompletedTask;
    }

    public Task ApplySettingsAsync(InstanceSettings settings, CancellationToken ct)
    {
        Settings = settings;
        Writes++;
        return Task.CompletedTask;
    }

    private void GuardRollback()
    {
        if (FailOnRollback)
        {
            throw new InvalidOperationException("Rücknahme nicht möglich.");
        }
    }

    private void Guard(string key)
    {
        if (string.Equals(key, FailWhenAdding, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Absichtlicher Fehler beim Schreiben von '{key}'.");
        }
    }
}
