using Bifrost.Abstractions;

namespace Bifrost.Core.Configuration;

/// <summary>Ein Upstream-Server in seiner jeweils neuesten Konfigurationsversion.</summary>
public sealed record UpstreamSnapshot(ServerId Id, UpstreamServerConfig Config);

/// <summary>Ein Werkzeug mit ausdrücklich hinterlegtem Freigabeweg (ADR-0022).</summary>
public sealed record ApprovalSnapshot(NamespacedToolName Tool, ApprovalEnforcement Enforcement);

/// <summary>Ein Skill in seiner neuesten Fassung.</summary>
public sealed record SkillSnapshot(
    AssetId Id,
    string Name,
    string? Description,
    string Content,
    SkillMetadata Metadata);

/// <summary>Die instanzweiten Schalter, die der Export überträgt.</summary>
public sealed record InstanceSettings(GuardOptions Guard, ApprovalEnforcement DefaultApprovalEnforcement)
{
    /// <summary>
    /// Der Auslieferungszustand. Er ist der Maßstab für die Frage, ob auf der Zielinstanz jemand
    /// eine eigene Entscheidung getroffen hat, die ein Import nicht überfahren darf.
    /// </summary>
    public static InstanceSettings Defaults { get; } = new(new GuardOptions(), ApprovalEnforcement.Queue);
}

/// <summary>
/// Was diese Instanz hält, aber nicht exportierbar ist — als Zählstand, nicht als Inhalt.
/// <para>
/// Der Zählstand genügt und ist das Maximum des Zulässigen: Ein Webhook-Secret muss zum Nachrechnen
/// der Signatur im Klartext vorliegen und wäre in einem Export das, was der Export verhindern soll;
/// ein API-Key liegt nur als Hash vor und wäre auf der Zielinstanz wertlos. Beides zu maskieren
/// statt wegzulassen hieße, eine Vollständigkeit vorzutäuschen, die es nicht gibt.
/// </para>
/// </summary>
public sealed record NonPortableInventory(
    int Identities = 0,
    int ApiKeys = 0,
    int Webhooks = 0,
    int UpstreamOAuthTokens = 0)
{
    public static NonPortableInventory None { get; } = new();
}

/// <summary>
/// Der vollständige Konfigurationszustand einer Instanz, wie ihn Export und Konfliktprüfung
/// brauchen. <b>Mit</b> Secretwerten — das Entfernen passiert eine Schicht höher, damit es genau
/// eine Stelle gibt, die es tut.
/// </summary>
public sealed record ConfigurationSnapshot(
    IReadOnlyList<UpstreamSnapshot> Upstreams,
    IReadOnlyList<Role> Roles,
    IReadOnlyList<ToolProfile> Profiles,
    IReadOnlyList<GuardRule> GuardRules,
    IReadOnlyList<ApprovalSnapshot> Approvals,
    IReadOnlyList<SkillSnapshot> Skills,
    InstanceSettings Settings,
    NonPortableInventory NonPortable)
{
    public static ConfigurationSnapshot Empty { get; } = new(
        [], [], [], [], [], [], InstanceSettings.Defaults, NonPortableInventory.None);
}

/// <summary>
/// Lese-Port auf die Konfiguration einer Instanz.
/// <para>
/// Eigener Port statt direkter Abhängigkeit auf <c>IUpstreamConfigStore</c>, <c>IRbacManagement</c>,
/// <c>IGuardRuleStore</c>, <c>IApprovalPolicy</c> und <c>IAssetStore</c>: Der Export braucht von
/// jedem dieser fünf genau eine Frage beantwortet, und ein Dienst, der fünf Verwaltungsschnittstellen
/// hält, kann mehr, als er darf. Die Zusammenführung ist Verdrahtung und gehört nach WP2.7.
/// </para>
/// </summary>
public interface IConfigurationSnapshotSource
{
    Task<ConfigurationSnapshot> ReadAsync(CancellationToken ct);
}

/// <summary>
/// Schreib-Port des Imports. <b>Ausschließlich additiv</b> — es gibt bewusst keine Operation, die
/// ein Objekt ersetzt oder entfernt, das nicht dieser Import selbst gerade angelegt hat.
/// <para>
/// Die <c>Remove…</c>-Methoden sind <b>keine</b> Löschfunktionen, sondern die Rücknahme eines
/// gescheiterten Imports (siehe <see cref="ConfigurationExportService.ApplyImportAsync"/>). Sie
/// werden nur mit Schlüsseln aufgerufen, die derselbe Vorgang unmittelbar zuvor angelegt hat.
/// </para>
/// <para>
/// <b>Hinweis an die Verdrahtung (WP2.7):</b> <c>IAssetStore</c> hat heute keine Operation, die
/// einen einzelnen Skill entfernt — nur <c>DeleteFromPackageAsync</c>. Eine Implementierung dieses
/// Ports braucht sie, sonst lässt sich ein Import, der beim vierten Skill scheitert, nicht
/// zurücknehmen.
/// </para>
/// </summary>
public interface IConfigurationImportTarget
{
    Task AddUpstreamAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);

    Task RemoveUpstreamAsync(ServerId id, CancellationToken ct);

    Task AddRoleAsync(Role role, CancellationToken ct);

    Task RemoveRoleAsync(RoleId id, CancellationToken ct);

    Task AddProfileAsync(ToolProfile profile, CancellationToken ct);

    Task RemoveProfileAsync(ProfileId id, CancellationToken ct);

    Task AddGuardRuleAsync(GuardRule rule, CancellationToken ct);

    Task RemoveGuardRuleAsync(string ruleId, CancellationToken ct);

    Task AddSkillAsync(SkillSnapshot skill, CancellationToken ct);

    Task RemoveSkillAsync(AssetId id, CancellationToken ct);

    /// <summary><c>null</c> nimmt die Markierung zurück — die Rücknahme eines gesetzten Wegs.</summary>
    Task SetApprovalAsync(NamespacedToolName tool, ApprovalEnforcement? enforcement, CancellationToken ct);

    Task ApplySettingsAsync(InstanceSettings settings, CancellationToken ct);
}
