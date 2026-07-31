using AwesomeAssertions;

using Bifrost.Abstractions;
using Bifrost.Server.Bootstrap;

using Xunit;

namespace Bifrost.Security.Tests.Bootstrap;

/// <summary>
/// Der Erstzugang (WP3.4): einmalig, kurzlebig, nur gehasht abgelegt — und für eine bestehende
/// Installation folgenlos.
/// <para>
/// <b>Was hier auf dem Spiel steht.</b> Zwei Fehler wären teuer und beide sind hier abgedeckt. Der
/// eine: ein Token, das mehr als einmal gilt — dann ist der Erstzugang kein Erstzugang, sondern ein
/// Zweitschlüssel. Der andere: eine Installation, die nach dem Upgrade niemanden mehr hereinlässt.
/// Der zweite ist der schlimmere, denn ein alter Logeintrag lässt sich rotieren, ein ausgesperrter
/// Betreiber nicht.
/// </para>
/// </summary>
public class BootstrapAccessTests
{
    private const string Password = "ein-langes-passwort";

    // ── Ausstellen ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task First_start_issues_a_token_and_stores_only_its_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        await world.Service().EnsureFirstAccessAsync(ct);

        var record = world.Record();
        record.Should().NotBeNull();
        record!.Phase.Should().Be(BootstrapPhase.Pending);
        record.TokenHash.Should().NotBeNullOrWhiteSpace();

        // Die Kernaussage: Auf der Platte steht der Hash, nicht das Token. Geprueft wird gegen den
        // Klartext aus der Uebergabedatei — also gegen genau den Wert, den ein Angreifer suchen
        // wuerde.
        var handover = await File.ReadAllTextAsync(world.HandoverPath, ct);
        var token = ExtractToken(handover);

        var stateContent = await File.ReadAllTextAsync(
            BootstrapLayout.StatePathFor(world.DataDirectory), ct);
        stateContent.Should().NotContain(token,
            "die dauerhafte Ablage traegt nur den Hash — sonst waere sie eine Kopie des Geheimnisses");
        BootstrapToken.Matches(token, record.TokenHash).Should().BeTrue();
    }

    [Fact]
    public async Task First_start_creates_no_ui_user_and_no_api_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        await world.Service().EnsureFirstAccessAsync(ct);

        (await world.UiUsers.ListAsync(ct)).Should().BeEmpty(
            "ein Zugang, den niemand angefordert hat, muesste sein Passwort irgendwo bekanntgeben — "
            + "und genau das war der Logeintrag, den dieses Paket abschafft");
    }

    // ── Einmaligkeit ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_token_can_only_be_redeemed_once()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var issued = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);
        issued.Outcome.Should().Be(BootstrapOutcome.Issued);

        var first = await world.Service().RedeemAsync(issued.Token, "betreiber", Password, ct);
        first.Outcome.Should().Be(BootstrapOutcome.Redeemed);

        var second = await world.Service().RedeemAsync(issued.Token, "zweiter", Password, ct);
        second.Outcome.Should().Be(BootstrapOutcome.NotPending,
            "ein Setup-Token, das ein zweites Mal gilt, ist kein Erstzugang, sondern ein Zweitschluessel");

        (await world.UiUsers.ListAsync(ct)).Select(user => user.Username)
            .Should().ContainSingle().Which.Should().Be("betreiber");
    }

    [Fact]
    public async Task Redeeming_removes_the_handover_file()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var issued = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);
        File.Exists(world.HandoverPath).Should().BeTrue();

        await world.Service().RedeemAsync(issued.Token, "betreiber", Password, ct);

        File.Exists(world.HandoverPath).Should().BeFalse(
            "der Zettel mit dem Klartext ist nach Gebrauch Altpapier — er darf nicht im naechsten "
            + "Backup des Datenverzeichnisses landen");
    }

    // ── Frist ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_expired_token_is_refused_and_invalidated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var issued = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);
        world.Time.Now = issued.ExpiresAt!.Value.AddSeconds(1);

        var result = await world.Service().RedeemAsync(issued.Token, "betreiber", Password, ct);

        result.Outcome.Should().Be(BootstrapOutcome.Expired);
        world.Record()!.TokenHash.Should().BeNull("ein abgelaufenes Token wird entwertet, nicht nur abgelehnt");
        File.Exists(world.HandoverPath).Should().BeFalse();
        (await world.UiUsers.ListAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_start_after_expiry_issues_a_new_token_while_nobody_has_access()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var first = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);
        world.Time.Now = first.ExpiresAt!.Value.AddMinutes(5);

        await world.Service().EnsureFirstAccessAsync(ct);

        // Der Fall, der eine Installation sonst unbenutzbar machte: Frist verstrichen, niemand
        // eingerichtet. Solange es keinen Zugang gibt, schuetzt ein verweigertes Token nichts.
        world.Record()!.TokenHash.Should().NotBeNull();
        var token = ExtractToken(await File.ReadAllTextAsync(world.HandoverPath, ct));
        BootstrapToken.Matches(first.Token, world.Record()!.TokenHash).Should().BeFalse(
            "das alte Token bleibt tot");
        (await world.Service().RedeemAsync(token, "betreiber", Password, ct))
            .Outcome.Should().Be(BootstrapOutcome.Redeemed);
    }

    // ── Wettlauf ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_simultaneous_redemptions_leave_exactly_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var issued = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);

        // Beide Versuche bestehen die Vorpruefung (Benutzername frei, Passwort lang genug). Der
        // Unterschied entsteht erst beim Anspruch auf das Token — genau dort, wo er entstehen soll.
        var gate = new TaskCompletionSource();
        var left = Task.Run(async () =>
        {
            await gate.Task;
            return await world.Service().RedeemAsync(issued.Token, "links", Password, ct);
        }, ct);
        var right = Task.Run(async () =>
        {
            await gate.Task;
            return await world.Service().RedeemAsync(issued.Token, "rechts", Password, ct);
        }, ct);

        gate.SetResult();
        var results = await Task.WhenAll(left, right);

        results.Count(r => r.Outcome is BootstrapOutcome.Redeemed).Should().Be(1,
            "zwei Gewinner hiessen zwei Administratoren aus einem Token");
        results.Count(r => r.Outcome is BootstrapOutcome.NotPending).Should().Be(1);
        (await world.UiUsers.ListAsync(ct)).Should().ContainSingle();
    }

    // ── Bestehende Instanz ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_established_instance_gets_no_second_token_without_local_proof()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();
        await world.UiUsers.CreateAsync("betreiber", Password, UiRole.Admin, ct);

        var overNetwork = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);
        overNetwork.Outcome.Should().Be(BootstrapOutcome.AlreadyEstablished);

        world.Proof.Proven = false;
        var withoutProof = await world.Service().IssueAsync(BootstrapOrigin.LocalRecovery, ct);
        withoutProof.Outcome.Should().Be(BootstrapOutcome.RecoveryProofMissing);

        File.Exists(world.HandoverPath).Should().BeFalse();
        world.Record()?.TokenHash.Should().BeNull();
    }

    [Fact]
    public async Task With_local_proof_an_established_instance_can_be_reopened()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();
        await world.UiUsers.CreateAsync("betreiber", Password, UiRole.Admin, ct);
        await world.Service().EnsureFirstAccessAsync(ct);

        world.Proof.Proven = true;
        var issued = await world.Service().IssueAsync(BootstrapOrigin.LocalRecovery, ct);

        issued.Outcome.Should().Be(BootstrapOutcome.Issued);
        var result = await world.Service().RedeemAsync(issued.Token, "zweiter", Password, ct);
        result.Outcome.Should().Be(BootstrapOutcome.Redeemed);

        // Der bestehende Zugang bleibt: Wiedereroeffnen heisst hinzufuegen, nicht ersetzen.
        (await world.UiUsers.ValidateCredentialsAsync("betreiber", Password, ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_used_up_bootstrap_never_reopens_by_itself()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var issued = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);
        var redeemed = await world.Service().RedeemAsync(issued.Token, "betreiber", Password, ct);
        redeemed.Outcome.Should().Be(BootstrapOutcome.Redeemed);

        // Der letzte Administrator verschwindet — durch ein Versehen, eine Migration, was auch
        // immer. Der Start darf daraufhin NICHT von selbst ein Token ausstellen: Sonst haenge das
        // Wiederoeffnen dieser Installation daran, dass jemand die richtige Tabelle leert.
        var users = await world.UiUsers.ListAsync(ct);
        await world.UiUsers.DeleteAsync(users[0].Id, ct);

        await world.Service().EnsureFirstAccessAsync(ct);

        world.Record()!.Phase.Should().Be(BootstrapPhase.Redeemed);
        world.Record()!.TokenHash.Should().BeNull();
        File.Exists(world.HandoverPath).Should().BeFalse();
    }

    // ── Migration bestehender Installationen ────────────────────────────────────────────────────

    [Fact]
    public async Task An_upgrade_leaves_an_existing_admin_able_to_log_in()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        // Der Zustand vor dem Upgrade: ein Administrator aus der Zeit des geloggten Passworts,
        // und keine Erstzugangs-Ablage, weil es sie damals nicht gab.
        await world.UiUsers.CreateAsync("admin", "altes-passwort-1", UiRole.Admin, ct);
        File.Exists(BootstrapLayout.StatePathFor(world.DataDirectory)).Should().BeFalse();

        // Der erste Start nach dem Upgrade.
        await world.Service().EnsureFirstAccessAsync(ct);

        var user = await world.UiUsers.ValidateCredentialsAsync("admin", "altes-passwort-1", ct);
        user.Should().NotBeNull("eine Instanz, die nach dem Upgrade niemanden mehr hereinlaesst, "
            + "ist schlimmer als eine mit einem alten Logeintrag");
        user!.Role.Should().Be(UiRole.Admin);

        world.Record()!.Phase.Should().Be(BootstrapPhase.Established);
        world.Record()!.TokenHash.Should().BeNull();
        File.Exists(world.HandoverPath).Should().BeFalse("es gab nie ein Token");
    }

    [Fact]
    public async Task A_second_start_of_an_upgraded_instance_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();
        await world.UiUsers.CreateAsync("admin", "altes-passwort-1", UiRole.Admin, ct);

        await world.Service().EnsureFirstAccessAsync(ct);
        var afterFirst = world.Record();
        world.Time.Now = world.Time.Now.AddDays(3);
        await world.Service().EnsureFirstAccessAsync(ct);

        world.Record().Should().Be(afterFirst, "der Vermerk wird gesetzt, nicht laufend neu geschrieben");
        (await world.UiUsers.ValidateCredentialsAsync("admin", "altes-passwort-1", ct)).Should().NotBeNull();
    }

    // ── Ablehnungen und Audit ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_wrong_token_is_refused_without_consuming_the_pending_one()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var issued = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);

        var wrong = await world.Service().RedeemAsync(
            BootstrapToken.Create(), "betreiber", Password, ct);
        wrong.Outcome.Should().Be(BootstrapOutcome.InvalidToken);

        // Das echte Token muss den Fehlversuch ueberleben — sonst legte ein Angreifer den
        // Erstzugang mit einem einzigen falschen Wert lahm.
        (await world.Service().RedeemAsync(issued.Token, "betreiber", Password, ct))
            .Outcome.Should().Be(BootstrapOutcome.Redeemed);
    }

    [Fact]
    public async Task A_typo_in_the_username_does_not_burn_the_token()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();
        await world.UiUsers.CreateAsync("belegt", Password, UiRole.Admin, ct);

        world.Proof.Proven = true;
        var issued = await world.Service().IssueAsync(BootstrapOrigin.LocalRecovery, ct);

        (await world.Service().RedeemAsync(issued.Token, "belegt", Password, ct))
            .Outcome.Should().Be(BootstrapOutcome.UsernameTaken);
        (await world.Service().RedeemAsync(issued.Token, "neu", "kurz", ct))
            .Outcome.Should().Be(BootstrapOutcome.PasswordTooShort);

        (await world.Service().RedeemAsync(issued.Token, "neu", Password, ct))
            .Outcome.Should().Be(BootstrapOutcome.Redeemed,
                "wer sich vertippt, darf nicht die Installation verlieren");
    }

    [Fact]
    public async Task Issuing_and_refusing_both_reach_the_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var issued = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);
        await world.Service().RedeemAsync("bfsetup_falsch", "betreiber", Password, ct);
        await world.Service().RedeemAsync(issued.Token, "betreiber", Password, ct);

        var details = world.Audit.Events.Select(e => e.Detail ?? string.Empty).ToList();
        details.Should().Contain(d => d.Contains("Setup-Token ausgestellt", StringComparison.Ordinal));
        details.Should().Contain(d => d.Contains("InvalidToken", StringComparison.Ordinal));
        details.Should().Contain(d => d.Contains("eingeloest", StringComparison.Ordinal));

        world.Audit.Events.Should().AllSatisfy(evt =>
            (evt.Detail ?? string.Empty).Should().NotContain(issued.Token!,
                "das Audit ist eine Ausgabe wie jede andere"));
    }

    // ── Die Ablage selbst ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_damaged_state_file_does_not_look_like_a_fresh_installation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var world = new BootstrapWorld();
        await world.InitializeAsync();

        var issued = await world.Service().IssueAsync(BootstrapOrigin.FirstStart, ct);
        await world.Service().RedeemAsync(issued.Token, "betreiber", Password, ct);

        await File.WriteAllTextAsync(
            BootstrapLayout.StatePathFor(world.DataDirectory), "{ kaputt", ct);

        // Eine beschaedigte Datei saehe sonst aus wie eine Neuinstallation — und die
        // Neuinstallation ist genau der Weg, der ein Token ausstellt. Auf einer Installation mit
        // bestehenden Admins waere das ein Zweitschluessel aus einem Lesefehler.
        var service = world.Service();
        await Assert.ThrowsAsync<BootstrapStateException>(
            async () => await service.EnsureFirstAccessAsync(ct));
        File.Exists(world.HandoverPath).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("", 60)]
    [InlineData("keine-zahl", 60)]
    [InlineData("0", 60)]
    [InlineData("-5", 60)]
    [InlineData("15", 15)]
    public void The_time_to_live_falls_back_to_the_default_on_nonsense(string? configured, int expected)
        => BootstrapRegistration.ResolveOptions(configured).TimeToLive
            .Should().Be(TimeSpan.FromMinutes(expected));

    /// <summary>Holt das Token aus dem Zettel — dieselbe Handbewegung wie beim Betreiber.</summary>
    private static string ExtractToken(string handover)
        => handover
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith(BootstrapToken.Prefix, StringComparison.Ordinal));
}
