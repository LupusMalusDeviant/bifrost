using System.Text.Json;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Configuration;

namespace Bifrost.Core.Policy;

/// <param name="RightsDelta">
/// <b>Wer darf danach was mehr.</b> Die eigentliche Aussage dieses Pakets — und der Grund, warum
/// ein Objekt-Diff nicht reicht.
/// </param>
/// <param name="MissingTools">
/// Werkzeuge, die das Pack pinnt oder freigibt und die es hier nicht gibt. Kein Grund zur Absage,
/// aber einer zum Hinsehen: Ein Profil aus einer fremden Instanz zeigt oft auf Server, die hier
/// anders heißen.
/// </param>
/// <param name="Updates">
/// Objekte, die <b>ersetzt</b> würden. Genau hier trennt sich ein Pack von einem Erstimport: Es
/// ändert Bestehendes, und das muss getrennt von den Zugängen dastehen.
/// </param>
/// <param name="Token">
/// Handle auf den vorgemerkten Import. Ohne Plan keine Anwendung — dieselbe Regel und dasselbe
/// Mittel wie beim Restore (M2-Vertrag, Nachtrag zum Handle).
/// </param>
public sealed record PolicyPackPlan(
    bool CanApply,
    PolicyPackManifest Manifest,
    string PublisherKeyId,
    ConnectorTrustLevel TrustLevel,
    string PayloadHash,
    RightsDelta RightsDelta,
    IReadOnlyList<string> Additions,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<string> Updates,
    IReadOnlyList<string> MissingTools,
    IReadOnlyList<string> Notes,
    string? Token);

/// <summary>
/// Prüft ein Policy Pack vollständig, <b>bevor</b> eine Berechtigung sich ändert (DoD WP6.1).
/// <para>
/// Der Planer baut nichts nach: Die Objektebene beantwortet der vorhandene Konfigurationsimport
/// (M2/WP2.5), die Vertrauensfrage der vorhandene Publisher-Trust-Store (ADR-0016). Neu ist genau
/// eine Sache — die Antwort auf „wer darf danach mehr".
/// </para>
/// <para>
/// <b>Stille Rechteausweitung gibt es nicht.</b> Der Zuwachs steht namentlich im Plan, je Identität
/// und Wirkungsebene. Wer anwendet, hat ihn gesehen; wer ihn nicht sehen will, kann nicht anwenden,
/// weil ohne Plan kein Handle entsteht.
/// </para>
/// </summary>
public sealed class PolicyPackPlanner(
    IConfigurationExportService import,
    IRbacManagement rbac,
    IToolCatalog catalog,
    IPublisherTrustStore publishers)
{
    public async Task<PolicyPackPlan> PlanAsync(string packDocument, CancellationToken ct)
    {
        var pack = PolicyPackReader.Parse(packDocument);
        var signer = PolicyPackReader.Verify(pack, publishers.All);

        var notes = new List<string>();
        if (signer.TrustLevel is ConnectorTrustLevel.ThirdParty)
        {
            // Kein Grund zur Absage — aber einer, der neben dem Rechte-Diff stehen muss.
            notes.Add(
                $"Herausgeber '{signer.Label}' ist als Drittanbieter eingetragen. Ein Pack von dort "
                + "ändert dieselben Rechte wie eines aus eigener Hand.");
        }

        // Die Objektebene beantwortet der vorhandene Weg — inklusive Konflikten mit lokal
        // Geaendertem und dem Handle, ueber das spaeter angewendet wird.
        //
        // Update-Modus: Ein Pack, das eine bestehende Rolle nicht anfassen darf, ist als Pack
        // wertlos — es koennte nur auf einer leeren Instanz etwas ausrichten. Der Preis dafuer ist
        // der Rueckweg, und den liefert derselbe Weg jetzt mit (ConfigurationImportUndo).
        var importPlan = await import.PlanImportAsync(
            pack.Payload, passphrase: null, ConfigurationImportMode.Update, ct);

        var document = ParseDocument(pack.Payload);
        var delta = await RightsDeltaAsync(document, ct);
        var missing = MissingTools(document);

        if (delta.GrantsMore)
        {
            notes.Add(
                $"{delta.Added.Count} zusätzliche Berechtigung(en) — sie stehen einzeln im Plan und "
                + "gelten erst nach der Übernahme.");
        }

        return new PolicyPackPlan(
            importPlan.CanApply,
            pack.Manifest,
            signer.KeyId,
            signer.TrustLevel,
            PolicyPackReader.PayloadHash(pack.Payload),
            delta,
            importPlan.Additions,
            importPlan.Conflicts,
            importPlan.Unchanged ?? [],
            importPlan.Updates ?? [],
            missing,
            notes,
            importPlan.Token);
    }

    /// <summary>
    /// Wendet an — über <b>denselben</b> Weg wie jeder andere Import, und nur mit dem Handle aus
    /// dem Plan. Der Rückweg entsteht vorher: ein Export des heutigen Standes.
    /// </summary>
    public async Task<PolicyPackApplication> ApplyAsync(PolicyPackPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.CanApply || plan.Token is null)
        {
            throw new PolicyPackException(
                "Dieser Plan ist nicht anwendbar. Ein Pack wird nie teilweise übernommen — erst die "
                + "Konflikte auflösen, dann neu planen.");
        }

        // Die Momentaufnahme ZUERST: Was man nach dem Schreiben zusammensucht, beschreibt nicht
        // mehr den Zustand davor. Sie ist Beleg und Beipack — der Rueckweg ist sie nicht.
        var snapshot = await import.ExportAsync(new ConfigurationExportRequest(IncludeSecrets: false), ct);

        var undo = await import.ApplyImportAsync(
            new ConfigurationImportPlan(
                true, plan.Additions, plan.Conflicts, [], plan.Unchanged, plan.Token, plan.Updates),
            ct);

        return new PolicyPackApplication(
            plan.Manifest.Id, plan.Manifest.Version, plan.PayloadHash, snapshot.Payload, undo);
    }

    /// <summary>
    /// Nimmt eine Übernahme zurück — über den Rückweg, den sie selbst erzeugt hat.
    /// <para>
    /// <b>Nicht über die Momentaufnahme.</b> Eine ganze Konfiguration zurückzuspielen entfernt
    /// nichts, was inzwischen dazugekommen ist, und fasst Objekte an, die mit dem Pack nie zu tun
    /// hatten. Der Rückweg berührt genau die Rollen und Profile, die das Pack berührt hat.
    /// </para>
    /// </summary>
    public Task RevertAsync(PolicyPackApplication application, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(application);
        return import.UndoImportAsync(application.Undo, ct);
    }

    private async Task<RightsDelta> RightsDeltaAsync(ConfigurationExportDocument document, CancellationToken ct)
    {
        var identities = await rbac.ListIdentitiesAsync(ct);
        var localRoles = await rbac.ListRolesAsync(ct);

        var before = RightsProjection.Project(
            identities, localRoles,
            localRoles.ToDictionary(r => r.Name, r => r.Grants, StringComparer.Ordinal));

        // Der Stand NACH der Uebernahme: die Rollen des Packs, wo der Name passt — und die lokalen
        // dort, wo das Pack nichts sagt. Ein Pack ersetzt nicht die Instanz, es ergaenzt sie.
        var after = new Dictionary<string, IReadOnlyList<Grant>>(StringComparer.Ordinal);
        foreach (var role in localRoles)
        {
            after[role.Name] = role.Grants;
        }

        foreach (var role in document.Roles)
        {
            after[role.Name] = [.. role.Grants.Select(ToGrant)];
        }

        return RightsProjection.Diff(before, RightsProjection.Project(identities, localRoles, after));
    }

    private List<string> MissingTools(ConfigurationExportDocument document)
    {
        var missing = new List<string>();

        foreach (var profile in document.Profiles)
        {
            foreach (var tool in profile.PinnedTools)
            {
                if (catalog.Find(new NamespacedToolName(tool)) is null)
                {
                    missing.Add($"Profil '{profile.Name}' pinnt '{tool}' — hier unbekannt.");
                }
            }
        }

        foreach (var role in document.Roles)
        {
            foreach (var grant in role.Grants.Where(g => g.Tool is { Length: > 0 }))
            {
                if (catalog.Find(new NamespacedToolName(grant.Tool!)) is null)
                {
                    missing.Add($"Rolle '{role.Name}' erlaubt '{grant.Tool}' — hier unbekannt.");
                }
            }
        }

        return missing;
    }

    private static Grant ToGrant(ExportedGrant grant) => new(
        new PermissionScope(
            grant.Server is { } server ? new ServerId(server) : null,
            grant.Tool is { Length: > 0 } tool ? new NamespacedToolName(tool) : null),
        [.. grant.Actions]);

    private static ConfigurationExportDocument ParseDocument(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ConfigurationExportDocument>(
                    payload, ConfigurationExportJson.Options)
                ?? throw new PolicyPackException("Die Nutzlast des Packs ist leer.");
        }
        catch (JsonException exception)
        {
            throw new PolicyPackException($"Die Nutzlast ist kein gültiger Export: {exception.Message}");
        }
    }
}

/// <param name="SnapshotBeforeApply">
/// Der Stand von <b>vor</b> der Übernahme, als Konfigurationsnutzlast — zum Nachlesen und Ablegen.
/// <para>
/// <b>Das ist eine Momentaufnahme und kein Rollback</b>, und das bleibt so: Sie einzuspielen
/// entfernt nichts, was inzwischen dazugekommen ist. Belegt ist der Unterschied in
/// <c>PolicyPackApiTests.The_snapshot_taken_before_applying_does_not_undo_the_change</c>. Der
/// Rückweg ist <paramref name="Undo"/>.
/// </para>
/// </param>
/// <param name="Undo">
/// <b>Der Rückweg.</b> Er trägt den vorigen Inhalt der ersetzten Rollen und Profile und die
/// Kennungen der angelegten — <see cref="PolicyPackPlanner.RevertAsync"/> führt ihn aus.
/// </param>
public sealed record PolicyPackApplication(
    string Id,
    string Version,
    string PayloadHash,
    string SnapshotBeforeApply,
    ConfigurationImportUndo Undo);
