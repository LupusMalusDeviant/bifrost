using Bifrost.Abstractions;
using Bifrost.Core.Configuration;
using Bifrost.Core.Upstreams;
using Bifrost.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Bifrost.Server.Operations;

/// <summary>
/// Die Verdrahtung des Konfigurationsexports an die echten Ablagen (WP2.7).
/// <para>
/// WP2.5 hat dafür bewusst zwei schmale Ports gelegt statt fünf Verwaltungsschnittstellen in den
/// Dienst zu hängen (siehe <see cref="IConfigurationSnapshotSource"/>): „Die Zusammenführung ist
/// Verdrahtung und gehört nach WP2.7." Genau das steht hier — und <b>nur</b> das. Es gibt hier
/// keine Regel darüber, was exportiert werden darf oder was ein Konflikt ist; das entscheidet der
/// Dienst.
/// </para>
/// </summary>
public sealed partial class ServerConfigurationPorts : IConfigurationSnapshotSource, IConfigurationImportTarget
{
    private readonly IDbContextFactory<BifrostDbContext> _factory;
    private readonly IUpstreamConfigStore _upstreamConfigs;
    private readonly UpstreamSupervisor _supervisor;
    private readonly IRbacManagement _rbac;
    private readonly IGuardRuleStore _guardRules;
    private readonly IApprovalPolicy _approvals;
    private readonly IAssetStore _assets;
    private readonly GuardOptions _guardOptions;
    private readonly ILogger<ServerConfigurationPorts> _logger;

    public ServerConfigurationPorts(
        IDbContextFactory<BifrostDbContext> factory,
        IUpstreamConfigStore upstreamConfigs,
        UpstreamSupervisor supervisor,
        IRbacManagement rbac,
        IGuardRuleStore guardRules,
        IApprovalPolicy approvals,
        IAssetStore assets,
        GuardOptions guardOptions,
        ILogger<ServerConfigurationPorts> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(upstreamConfigs);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(rbac);
        ArgumentNullException.ThrowIfNull(guardRules);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(guardOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _factory = factory;
        _upstreamConfigs = upstreamConfigs;
        _supervisor = supervisor;
        _rbac = rbac;
        _guardRules = guardRules;
        _approvals = approvals;
        _assets = assets;
        _guardOptions = guardOptions;
        _logger = logger;
    }

    // ── Lesen ───────────────────────────────────────────────────────────────────────────────────

    public async Task<ConfigurationSnapshot> ReadAsync(CancellationToken ct)
    {
        var upstreams = await _upstreamConfigs.GetAllLatestAsync(ct).ConfigureAwait(false);
        var roles = await _rbac.ListRolesAsync(ct).ConfigureAwait(false);
        var profiles = await _rbac.ListProfilesAsync(ct).ConfigureAwait(false);

        var skills = new List<SkillSnapshot>();
        foreach (var asset in await _assets.ListAsync(ct).ConfigureAwait(false))
        {
            // Die Liste trägt keine Inhalte; der Export braucht sie. Ein Aufruf je Skill ist der
            // Preis dafür, dass die Liste billig bleibt — Export ist kein heißer Pfad.
            var content = await _assets.GetAsync(asset.Id, null, ct).ConfigureAwait(false);
            skills.Add(new SkillSnapshot(
                asset.Id, asset.Name, asset.Description, content.Content, content.MetadataOrEmpty));
        }

        return new ConfigurationSnapshot(
            [.. upstreams.Select(pair => new UpstreamSnapshot(pair.Key, pair.Value.Config))],
            roles,
            profiles,
            _guardRules.All,
            [.. _approvals.All
                .Select(tool => (Tool: tool, Enforcement: _approvals.EnforcementFor(tool)))
                .Where(entry => entry.Enforcement is not null)
                .Select(entry => new ApprovalSnapshot(entry.Tool, entry.Enforcement!.Value))],
            skills,
            new InstanceSettings(_guardOptions, _approvals.DefaultEnforcement),
            await CountNonPortableAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Was diese Instanz hält, aber nicht exportierbar ist — als <b>Zählstand</b>. Mehr wäre nicht
    /// zulässig: Ein Webhook-Secret muss im Klartext vorliegen, ein API-Key liegt nur als Hash vor.
    /// </summary>
    private async Task<NonPortableInventory> CountNonPortableAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return new NonPortableInventory(
            await db.Identities.CountAsync(ct).ConfigureAwait(false),
            await db.ApiKeys.CountAsync(ct).ConfigureAwait(false),
            await db.Webhooks.CountAsync(ct).ConfigureAwait(false),
            await db.UpstreamOAuthTokens.CountAsync(ct).ConfigureAwait(false));
    }

    // ── Schreiben (ausschließlich additiv) ──────────────────────────────────────────────────────

    public async Task AddUpstreamAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        // Mit der Id aus dem Export, nicht mit einer neuen: Ein mitgelieferter Grant zeigt sonst ins
        // Leere, und Default-Deny meldet das nicht — es erlaubt dann nur nichts (Lead-Entscheidung
        // zu WP2.5).
        var version = await _upstreamConfigs.AppendVersionAsync(id, config, ct).ConfigureAwait(false);
        await _supervisor
            .RestoreAsync(id, new UpstreamConfigVersion(version, config, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    public async Task RemoveUpstreamAsync(ServerId id, CancellationToken ct)
    {
        try
        {
            await _supervisor.RemoveAsync(id, DrainPolicy.Immediate, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // Der Upstream war noch nicht im Supervisor — dann bleibt nur die Konfiguration.
        }

        await _upstreamConfigs.RemoveAsync(id, ct).ConfigureAwait(false);
    }

    public Task AddRoleAsync(Role role, CancellationToken ct) => _rbac.UpsertRoleAsync(role, ct);

    public Task RemoveRoleAsync(RoleId id, CancellationToken ct) => _rbac.RemoveRoleAsync(id, ct);

    public Task AddProfileAsync(ToolProfile profile, CancellationToken ct)
        => _rbac.UpsertProfileAsync(profile, ct);

    public Task RemoveProfileAsync(ProfileId id, CancellationToken ct) => _rbac.RemoveProfileAsync(id, ct);

    public Task AddGuardRuleAsync(GuardRule rule, CancellationToken ct) => _guardRules.UpsertAsync(rule, ct);

    public Task RemoveGuardRuleAsync(string ruleId, CancellationToken ct)
        => _guardRules.RemoveAsync(ruleId, ct);

    public async Task AddSkillAsync(SkillSnapshot skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);
        await _assets
            .CreateAsync(skill.Name, skill.Description, skill.Content, skill.Metadata, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <b>Nicht möglich, und deshalb laut.</b> <see cref="IAssetStore"/> kennt nur
    /// <c>DeleteFromPackageAsync</c>; einen einzelnen Skill zu entfernen gibt es nicht. Der Ausweg
    /// wäre eine Änderung an einem fremden Vertrag — WP2.7 verdrahtet, es ändert keine Dienste.
    /// <para>
    /// Diese Ausnahme landet in der Rückstandsliste der Rücknahme („diese Objekte konnten nicht
    /// entfernt werden"). Das ist die richtige Stelle dafür: Ein stilles Nichtstun hätte einen zur
    /// Hälfte angewendeten Import als vollständig zurückgenommen gemeldet.
    /// </para>
    /// </summary>
    public Task RemoveSkillAsync(AssetId id, CancellationToken ct)
    {
        Log.SkillRollbackImpossible(_logger, id.Value);
        throw new NotSupportedException(
            $"Der Skill {id.Value} lässt sich nicht einzeln entfernen: IAssetStore kennt dafür keine "
            + "Operation (nur das Entfernen aller Skills eines Pakets). Der Skill ist angelegt und "
            + "muss von Hand geprüft werden.");
    }

    public Task SetApprovalAsync(
        NamespacedToolName tool, ApprovalEnforcement? enforcement, CancellationToken ct)
        => _approvals.SetAsync(tool, enforcement, ct);

    /// <summary>
    /// Übernimmt die instanzweiten Schalter, <b>soweit sie zur Laufzeit veränderbar sind</b>.
    /// <para>
    /// Der Freigabe-Vorgabeweg liegt in der Datenbank und wird übernommen. Die
    /// <see cref="GuardOptions"/> dagegen entstehen beim Start aus der Umgebung
    /// (<c>BIFROST_GUARD_*</c>) und sind ein unveränderliches Singleton — sie hier zu „setzen"
    /// hieße, eine Übernahme zu melden, die beim nächsten Start wieder weg wäre. Stattdessen wird
    /// der Unterschied protokolliert, damit ein Betreiber ihn in seiner <c>.env</c> nachzieht.
    /// </para>
    /// </summary>
    public async Task ApplySettingsAsync(InstanceSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _approvals.SetDefaultEnforcementAsync(settings.DefaultApprovalEnforcement, ct).ConfigureAwait(false);

        if (settings.Guard != _guardOptions)
        {
            Log.GuardOptionsNotApplied(_logger);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 2710,
            Level = LogLevel.Error,
            Message = "Ein Skill (Id {AssetId}) laesst sich nicht einzeln entfernen — die Ruecknahme "
                + "des Imports bleibt an dieser Stelle unvollstaendig.")]
        public static partial void SkillRollbackImpossible(ILogger logger, Guid assetId);

        [LoggerMessage(
            EventId = 2711,
            Level = LogLevel.Warning,
            Message = "Der Import enthaelt abweichende Guardrail-Schalter. Sie stammen aus der Umgebung "
                + "(BIFROST_GUARD_*) und lassen sich zur Laufzeit nicht setzen — bitte in der "
                + "Konfiguration der Zielinstanz nachziehen.")]
        public static partial void GuardOptionsNotApplied(ILogger logger);
    }
}
