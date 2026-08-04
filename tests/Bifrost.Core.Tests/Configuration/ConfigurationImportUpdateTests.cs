using AwesomeAssertions;
using Bifrost.Abstractions;
using Bifrost.Abstractions.Operations;
using Bifrost.Core.Configuration;
using Xunit;

namespace Bifrost.Core.Tests.Configuration;

/// <summary>
/// M6/WP6.1: Der Importweg kann jetzt <b>ersetzen</b> — und muss dafür einen Rückweg vorlegen.
/// <para>
/// <b>Warum diese Tests einzeln stehen:</b> Die Zusage des additiven Imports war nicht „es ist
/// bequemer so", sondern die Begründung seiner Rücknahme — was nur angelegt wurde, kann gefahrlos
/// entfernt werden. Mit dem Update-Modus fällt diese Begründung weg. Sie wird hier durch eine
/// andere ersetzt, und der Unterschied wird belegt statt behauptet:
/// <see cref="Taking_back_an_import_restores_a_replaced_role_instead_of_deleting_it"/> ist der
/// Test, der eine naive Umsetzung rot färbt.
/// </para>
/// </summary>
public sealed class ConfigurationImportUpdateTests
{
    private const string Vorhanden = "Leser";
    private const string Neu = "Schreiber";

    private static readonly RoleId FremdeId = new(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));

    // ── Der Modus ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Die Vorgabe bleibt, wie sie war. Ohne diesen Test wäre nicht belegt, dass der neue Modus
    /// etwas <em>hinzufügt</em>, statt das alte Verhalten stillschweigend zu ersetzen.
    /// </summary>
    [Fact]
    public async Task Add_only_still_treats_an_existing_role_as_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dienst, ziel, nutzlast) = await AufbauAsync(ct);

        var plan = await dienst.PlanImportAsync(nutzlast, null, ct);

        plan.CanApply.Should().BeFalse();
        plan.Conflicts.Should().Contain(c => c.Contains(Vorhanden, StringComparison.Ordinal));
        plan.Updates.Should().BeNullOrEmpty();
        ziel.Roles.Single(r => r.Name == Vorhanden).Grants.Should().ContainSingle()
            .Which.Scope.Server.Should().NotBeNull("planen ändert nichts");
    }

    [Fact]
    public async Task Update_turns_the_same_collision_into_a_replacement()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dienst, _, nutzlast) = await AufbauAsync(ct);

        var plan = await dienst.PlanImportAsync(nutzlast, null, ConfigurationImportMode.Update, ct);

        plan.CanApply.Should().BeTrue();
        plan.Conflicts.Should().BeEmpty();
        plan.Updates.Should().ContainSingle().Which.Should().Contain(Vorhanden);
        plan.Additions.Should().Contain(a => a.Contains(Neu, StringComparison.Ordinal),
            "was neu ist, bleibt ein Zugang — „angelegt\" und „ersetzt\" sind zwei verschiedene Sätze");
    }

    /// <summary>
    /// <b>Die Id der Zielinstanz gewinnt.</b> Ein Pack bringt eigene Guids mit; übernähme man sie,
    /// hinge jede Identität plötzlich an einer Rolle, die es unter ihrer alten Id nicht mehr gibt —
    /// die Zuordnung Identität→Rolle läuft über die Id, nicht über den Namen.
    /// </summary>
    [Fact]
    public async Task A_replacement_keeps_the_local_id_so_identities_still_point_somewhere()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dienst, ziel, nutzlast) = await AufbauAsync(ct);
        var vorherigeId = ziel.Roles.Single(r => r.Name == Vorhanden).Id;
        vorherigeId.Should().NotBe(FremdeId, "die Nutzlast bringt bewusst eine andere Id mit");

        await dienst.ApplyImportAsync(
            await dienst.PlanImportAsync(nutzlast, null, ConfigurationImportMode.Update, ct), ct);

        var rolle = ziel.Roles.Should().ContainSingle(r => r.Name == Vorhanden).Which;
        rolle.Id.Should().Be(vorherigeId);
        rolle.Grants.Should().ContainSingle().Which.Scope.Should()
            .Match<PermissionScope>(s => s.Server == null && s.Tool == null, "der Inhalt kommt aus der Nutzlast");
    }

    // ── Der Rückweg ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Der Test, der eine naive Umsetzung rot färbt.</b> Wer die Rücknahme aus dem alten Import
    /// übernimmt („entferne, was du angelegt hast"), löscht hier eine Rolle, die vorher da war —
    /// und entzieht damit jeder Identität, die an ihr hängt, sämtliche Rechte.
    /// </summary>
    [Fact]
    public async Task Taking_back_an_import_restores_a_replaced_role_instead_of_deleting_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dienst, ziel, nutzlast) = await AufbauAsync(ct);
        var vorher = ziel.Roles.Single(r => r.Name == Vorhanden);

        var undo = await dienst.ApplyImportAsync(
            await dienst.PlanImportAsync(nutzlast, null, ConfigurationImportMode.Update, ct), ct);
        await dienst.UndoImportAsync(undo, ct);

        // Inhaltsgleich, nicht dasselbe Objekt: Der vorige Stand wird aus dem gelesenen Dokument
        // rekonstruiert. Genau darauf kommt es an — der Rückweg haelt keinen Zeiger auf die alte
        // Instanz, sondern ihren Inhalt.
        ziel.Roles.Should().ContainSingle(r => r.Name == Vorhanden).Which.Should().BeEquivalentTo(vorher,
            "die Rolle steht wieder genau so da wie vor der Übernahme — mit Id, Grants und Ratenlimit");
    }

    [Fact]
    public async Task Taking_back_an_import_removes_what_it_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dienst, ziel, nutzlast) = await AufbauAsync(ct);

        var undo = await dienst.ApplyImportAsync(
            await dienst.PlanImportAsync(nutzlast, null, ConfigurationImportMode.Update, ct), ct);
        ziel.Roles.Should().Contain(r => r.Name == Neu, "erst muss die Übernahme gewirkt haben");

        await dienst.UndoImportAsync(undo, ct);

        ziel.Roles.Should().NotContain(r => r.Name == Neu);
    }

    /// <summary>
    /// Der Rückweg unterscheidet die beiden Fälle <b>an der Id</b>, nicht an der Reihenfolge. Steht
    /// das nicht fest, kippt das Verhalten, sobald jemand die Planung umsortiert.
    /// </summary>
    [Fact]
    public async Task The_undo_names_exactly_what_was_replaced_and_what_was_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dienst, ziel, nutzlast) = await AufbauAsync(ct);
        var vorherigeId = ziel.Roles.Single(r => r.Name == Vorhanden).Id;

        var undo = await dienst.ApplyImportAsync(
            await dienst.PlanImportAsync(nutzlast, null, ConfigurationImportMode.Update, ct), ct);

        undo.ReplacedRoles.Should().ContainSingle().Which.Id.Should().Be(vorherigeId);
        undo.CreatedRoles.Should().ContainSingle().Which.Should().NotBe(vorherigeId);
        undo.IsEmpty.Should().BeFalse();
    }

    /// <summary>
    /// Scheitert die Übernahme in der Mitte, greift dieselbe Unterscheidung sofort — und nicht erst,
    /// wenn jemand später zurückgeht. Ein Import, der beim zweiten Objekt abbricht und dabei das
    /// erste löscht, wäre schlimmer als einer, der gar nichts tut.
    /// </summary>
    [Fact]
    public async Task A_failed_apply_puts_a_replaced_role_back_rather_than_dropping_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dienst, ziel, nutzlast) = await AufbauAsync(ct);
        var vorher = ziel.Roles.Single(r => r.Name == Vorhanden);
        var plan = await dienst.PlanImportAsync(nutzlast, null, ConfigurationImportMode.Update, ct);

        ziel.FailWhenAdding = Neu;
        var act = async () => await dienst.ApplyImportAsync(plan, ct);

        await act.Should().ThrowAsync<ConfigurationImportException>();
        ziel.Roles.Should().ContainSingle(r => r.Name == Vorhanden).Which.Should().BeEquivalentTo(vorher);
        ziel.Roles.Should().NotContain(r => r.Name == Neu);
    }

    /// <summary>
    /// Ein Rückstand wird gemeldet, nicht verschluckt. Was bei einer Rücknahme stehen bleibt, sind
    /// Rechte, von denen sonst niemand mehr weiß.
    /// </summary>
    [Fact]
    public async Task An_incomplete_undo_says_what_stayed_behind()
    {
        var ct = TestContext.Current.CancellationToken;
        var (dienst, ziel, nutzlast) = await AufbauAsync(ct);

        var undo = await dienst.ApplyImportAsync(
            await dienst.PlanImportAsync(nutzlast, null, ConfigurationImportMode.Update, ct), ct);

        ziel.FailWhenAdding = Vorhanden;
        var act = async () => await dienst.UndoImportAsync(undo, ct);

        (await act.Should().ThrowAsync<ConfigurationImportException>())
            .Which.Message.Should().Contain(Vorhanden).And.Contain("von Hand");

        ziel.Roles.Should().NotContain(r => r.Name == Neu,
            "der Rest wird trotzdem zurückgenommen — ein Abbruch beim ersten Fehler ließe mehr stehen");
    }

    /// <summary>
    /// Baut Quelle und Ziel: dieselbe Instanz, aber die Nutzlast erweitert die vorhandene Rolle
    /// (unter <b>anderer</b> Id, wie ein weitergegebenes Pack) und bringt eine zweite mit.
    /// </summary>
    private static async Task<(ConfigurationExportService Dienst, FakeInstance Ziel, string Nutzlast)>
        AufbauAsync(CancellationToken ct)
    {
        var quelle = ConfigurationFixtures.SecretFree();
        var alt = quelle.Roles[0];
        alt.Name.Should().Be(Vorhanden, "die Aufbauten und dieser Test müssen dieselbe Rolle meinen");

        quelle.Roles[0] = new Role(
            FremdeId, alt.Name, [new Grant(new PermissionScope(null, null), [ToolAction.UseTool])], alt.RateLimit);
        quelle.Roles.Add(new Role(RoleId.New(), Neu, [], null));

        var export = await ConfigurationFixtures.ServiceFor(quelle)
            .ExportAsync(new ConfigurationExportRequest(IncludeSecrets: false), ct);

        var ziel = ConfigurationFixtures.SecretFree();
        return (ConfigurationFixtures.ServiceFor(ziel), ziel, export.Payload);
    }
}
